using System.Globalization;
using System.Text.Json;
using Pickle.Abstractions;
using Pickle.Abstractions.Services;

namespace Pickle.Admin.Logs;

/// <summary>The systemd journal through <c>journalctl -o json</c>.</summary>
public sealed class JournaldLogSource(IProgramRunner runner) : ILogSource
{
    public string Name => "systemd journal";

    public bool IsSupported => OperatingSystem.IsLinux() && runner.Find("journalctl") is not null;

    public IReadOnlyList<string> Sources => ["system", "kernel", "user"];

    public async Task<IReadOnlyList<LogEntry>> QueryAsync(LogQuery query, CancellationToken cancellationToken = default)
    {
        var result = await runner.RunAsync("journalctl", Arguments(query, follow: false), new ProgramRunOptions { Timeout = TimeSpan.FromSeconds(30) }, cancellationToken).ConfigureAwait(false);
        if (!result.WasFound)
        {
            throw new InvalidOperationException("journalctl was not found.");
        }

        if (!result.Success && string.IsNullOrWhiteSpace(result.StdOut))
        {
            throw new InvalidOperationException(result.Message);
        }

        var entries = Parse(result.StdOut);
        return query.Text is { Length: > 0 } text
            ? [.. entries.Where(e => e.Message.Contains(text, StringComparison.OrdinalIgnoreCase) || e.Source.Contains(text, StringComparison.OrdinalIgnoreCase))]
            : entries;
    }

    public string? FollowCommand(LogQuery query) => PowerShellQuote.Command("journalctl", Arguments(query, follow: true));

    internal static IReadOnlyList<string> Arguments(LogQuery query, bool follow)
    {
        var args = new List<string> { "--no-pager", "-o", follow ? "short-iso" : "json", "-n", Math.Clamp(query.Max, 1, 5000).ToString(CultureInfo.InvariantCulture) };
        if (follow)
        {
            args.Add("-f");
        }

        if (query.Since is { } since && !follow)
        {
            args.Add("--since");
            args.Add(DateTimeOffset.Now.Subtract(since).ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture));
        }

        if (query.MinSeverity > LogSeverity.Debug)
        {
            args.Add("-p");
            args.Add(Priority(query.MinSeverity).ToString(CultureInfo.InvariantCulture));
        }

        switch (query.Source)
        {
            case "kernel":
                args.Add("-k");
                break;
            case "user":
                args.Add("--user");
                break;
        }

        if (!string.IsNullOrEmpty(query.Unit))
        {
            if (!query.Unit.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '-' or '_' or '@' or ':' or '\\'))
            {
                throw new ArgumentException($"'{query.Unit}' is not a unit name.");
            }

            args.Add("-u");
            args.Add(query.Unit);
        }

        return args;
    }

    public static IReadOnlyList<LogEntry> Parse(string output)
    {
        var entries = new List<LogEntry>();
        foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                using var document = JsonDocument.Parse(line);
                var root = document.RootElement;
                if (root.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                var micros = long.TryParse(Text(root, "__REALTIME_TIMESTAMP"), CultureInfo.InvariantCulture, out var us) ? us : 0;
                var source = Text(root, "SYSLOG_IDENTIFIER") ?? Text(root, "_COMM") ?? "journal";
                var fields = new Dictionary<string, string>();
                foreach (var (key, label) in new[] { ("_SYSTEMD_UNIT", "Unit"), ("_PID", "Pid"), ("_HOSTNAME", "Host"), ("_UID", "Uid") })
                {
                    if (Text(root, key) is { } value)
                    {
                        fields[label] = value;
                    }
                }

                entries.Add(new LogEntry(
                    DateTimeOffset.FromUnixTimeMilliseconds(micros / 1000).ToLocalTime(),
                    FromPriority(int.TryParse(Text(root, "PRIORITY"), CultureInfo.InvariantCulture, out var p) ? p : 6),
                    source,
                    Message(root))
                { Fields = fields });
            }
            catch (JsonException)
            {
            }
        }

        return [.. entries.OrderByDescending(e => e.Time)];
    }

    public static LogSeverity FromPriority(int priority) => priority switch
    {
        <= 2 => LogSeverity.Critical,
        3 => LogSeverity.Error,
        4 => LogSeverity.Warning,
        5 => LogSeverity.Notice,
        6 => LogSeverity.Info,
        _ => LogSeverity.Debug,
    };

    public static int Priority(LogSeverity severity) => severity switch
    {
        LogSeverity.Critical => 2,
        LogSeverity.Error => 3,
        LogSeverity.Warning => 4,
        LogSeverity.Notice => 5,
        LogSeverity.Info => 6,
        _ => 7,
    };

    private static string? Text(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    // MESSAGE is an array of bytes when the text isn't valid UTF-8.
    private static string Message(JsonElement root)
    {
        if (!root.TryGetProperty("MESSAGE", out var message))
        {
            return string.Empty;
        }

        return message.ValueKind switch
        {
            JsonValueKind.String => message.GetString() ?? string.Empty,
            JsonValueKind.Array => System.Text.Encoding.UTF8.GetString([.. message.EnumerateArray().Select(b => (byte)b.GetInt32())]),
            _ => message.ToString(),
        };
    }
}
