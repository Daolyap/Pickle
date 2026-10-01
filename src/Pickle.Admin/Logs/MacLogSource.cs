using System.Globalization;
using System.Text.Json;
using Pickle.Abstractions;
using Pickle.Abstractions.Services;

namespace Pickle.Admin.Logs;

/// <summary>The macOS unified log through <c>log show --style ndjson</c> (best effort).</summary>
public sealed class MacLogSource(IProgramRunner runner) : ILogSource
{
    public string Name => "macOS unified log";

    public bool IsSupported => OperatingSystem.IsMacOS() && runner.Find("log") is not null;

    public IReadOnlyList<string> Sources => ["system"];

    public async Task<IReadOnlyList<LogEntry>> QueryAsync(LogQuery query, CancellationToken cancellationToken = default)
    {
        var result = await runner.RunAsync("log", Arguments(query), new ProgramRunOptions { Timeout = TimeSpan.FromSeconds(60) }, cancellationToken).ConfigureAwait(false);
        if (!result.Success && string.IsNullOrWhiteSpace(result.StdOut))
        {
            throw new InvalidOperationException(result.Message);
        }

        return [.. Parse(result.StdOut).Where(e => e.Severity >= query.MinSeverity).Take(query.Max)];
    }

    public string? FollowCommand(LogQuery query) => PowerShellQuote.Command("log", ["stream", "--style", "compact", .. Predicate(query)]);

    internal static IReadOnlyList<string> Arguments(LogQuery query) =>
        ["show", "--style", "ndjson", "--last", Last(query.Since ?? TimeSpan.FromHours(1)), .. Predicate(query)];

    private static string Last(TimeSpan span) =>
        span.TotalDays >= 1 ? $"{(int)Math.Ceiling(span.TotalDays)}d" : span.TotalHours >= 1 ? $"{(int)Math.Ceiling(span.TotalHours)}h" : $"{Math.Max(1, (int)span.TotalMinutes)}m";

    private static IEnumerable<string> Predicate(LogQuery query)
    {
        var parts = new List<string>();
        if (query.MinSeverity >= LogSeverity.Error)
        {
            parts.Add("messageType >= 16");
        }

        if (!string.IsNullOrEmpty(query.Unit))
        {
            if (!query.Unit.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '-' or '_'))
            {
                throw new ArgumentException($"'{query.Unit}' is not a process name.");
            }

            parts.Add($"process == \"{query.Unit}\"");
        }

        return parts.Count == 0 ? [] : ["--predicate", string.Join(" AND ", parts)];
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
                if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("eventMessage", out var message))
                {
                    continue;
                }

                var time = root.TryGetProperty("timestamp", out var ts) && DateTimeOffset.TryParse(ts.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed) ? parsed : DateTimeOffset.MinValue;
                var path = root.TryGetProperty("processImagePath", out var image) ? image.GetString() ?? string.Empty : string.Empty;
                var kind = root.TryGetProperty("messageType", out var type) ? type.GetString() : null;
                var fields = new Dictionary<string, string>();
                if (root.TryGetProperty("subsystem", out var subsystem) && subsystem.GetString() is { Length: > 0 } sub)
                {
                    fields["Subsystem"] = sub;
                }

                if (root.TryGetProperty("processID", out var pid))
                {
                    fields["Pid"] = pid.ToString();
                }

                entries.Add(new LogEntry(time, kind switch { "Fault" => LogSeverity.Critical, "Error" => LogSeverity.Error, "Debug" => LogSeverity.Debug, _ => LogSeverity.Info }, Path.GetFileName(path), message.GetString() ?? string.Empty) { Fields = fields });
            }
            catch (JsonException)
            {
            }
        }

        return [.. entries.OrderByDescending(e => e.Time)];
    }
}
