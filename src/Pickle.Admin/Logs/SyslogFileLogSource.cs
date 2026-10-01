using System.Globalization;
using System.Text.RegularExpressions;
using Pickle.Abstractions.Services;

namespace Pickle.Admin.Logs;

/// <summary>/var/log/syslog (or messages) for systems without a journal: classic <c>Oct  1 12:00:00 host program[pid]: message</c> lines, newest last in the file.</summary>
public sealed partial class SyslogFileLogSource(string? directory = null, Func<DateTimeOffset>? now = null) : ILogSource
{
    private static readonly string[] Names = ["syslog", "messages", "auth.log", "kern.log"];
    private readonly string _directory = directory ?? "/var/log";
    private readonly Func<DateTimeOffset> _now = now ?? (() => DateTimeOffset.Now);

    public string Name => "/var/log";

    public bool IsSupported => !OperatingSystem.IsWindows() && Sources.Count > 0;

    public IReadOnlyList<string> Sources => [.. Names.Where(n => File.Exists(Path.Combine(_directory, n)))];

    public async Task<IReadOnlyList<LogEntry>> QueryAsync(LogQuery query, CancellationToken cancellationToken = default)
    {
        var source = query.Source is { } s && Names.Contains(s) ? s : Sources.FirstOrDefault() ?? throw new InvalidOperationException("No readable log file in " + _directory);
        var path = Path.Combine(_directory, source);
        string[] lines;
        try
        {
            lines = await File.ReadAllLinesAsync(path, cancellationToken).ConfigureAwait(false);
        }
        catch (UnauthorizedAccessException)
        {
            throw new InvalidOperationException($"{path} is not readable by your user (try the adm group, or sudo).");
        }

        var entries = new List<LogEntry>();
        var cutoff = query.Since is { } since ? _now().Subtract(since) : (DateTimeOffset?)null;
        foreach (var line in lines.Reverse())
        {
            if (Parse(line, _now()) is not { } entry)
            {
                continue;
            }

            if ((cutoff is { } c && entry.Time < c)
                || (query.Text is { Length: > 0 } text && !line.Contains(text, StringComparison.OrdinalIgnoreCase))
                || (query.Unit is { Length: > 0 } unit && !entry.Source.Contains(unit, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            entries.Add(entry);
            if (entries.Count >= query.Max)
            {
                break;
            }
        }

        return [.. entries.Where(e => e.Severity >= query.MinSeverity)];
    }

    public string? FollowCommand(LogQuery query) =>
        (Sources.FirstOrDefault(n => n == query.Source) ?? Sources.FirstOrDefault()) is { } name
            ? $"Get-Content -Wait -Tail 100 {Abstractions.PowerShellQuote.Single(Path.Combine(_directory, name))}"
            : null;

    public static LogEntry? Parse(string line, DateTimeOffset now)
    {
        if (ClassicLine().Match(line) is { Success: true } classic)
        {
            if (!DateTime.TryParseExact(
                    $"{now.Year} {classic.Groups[1].Value}",
                    ["yyyy MMM d HH:mm:ss", "yyyy MMM  d HH:mm:ss"],
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.AllowWhiteSpaces,
                    out var time))
            {
                return null;
            }

            var stamp = new DateTimeOffset(time, now.Offset);
            if (stamp > now.AddDays(1))
            {
                stamp = stamp.AddYears(-1);
            }

            var message = classic.Groups[4].Value;
            return new LogEntry(stamp, Guess(message), classic.Groups[3].Value, message) { Fields = new Dictionary<string, string> { ["Host"] = classic.Groups[2].Value } };
        }

        if (IsoLine().Match(line) is { Success: true } iso && DateTimeOffset.TryParse(iso.Groups[1].Value, CultureInfo.InvariantCulture, DateTimeStyles.None, out var isoTime))
        {
            var message = iso.Groups[4].Value;
            return new LogEntry(isoTime, Guess(message), iso.Groups[3].Value, message) { Fields = new Dictionary<string, string> { ["Host"] = iso.Groups[2].Value } };
        }

        return null;
    }

    private static LogSeverity Guess(string message) =>
        message.Contains("error", StringComparison.OrdinalIgnoreCase) || message.Contains("failed", StringComparison.OrdinalIgnoreCase) ? LogSeverity.Error
        : message.Contains("warn", StringComparison.OrdinalIgnoreCase) ? LogSeverity.Warning
        : LogSeverity.Info;

    // "Oct  1 12:00:00 host program[123]: text"
    [GeneratedRegex(@"^([A-Z][a-z]{2}\s+\d{1,2}\s\d{2}:\d{2}:\d{2})\s+(\S+)\s+([^:\[\s]+)(?:\[\d+\])?:\s?(.*)$")]
    private static partial Regex ClassicLine();

    // "2025-10-01T12:00:00.123456+00:00 host program[123]: text" (rsyslog RFC 3339 format)
    [GeneratedRegex(@"^(\d{4}-\d{2}-\d{2}T\S+)\s+(\S+)\s+([^:\[\s]+)(?:\[\d+\])?:\s?(.*)$")]
    private static partial Regex IsoLine();
}
