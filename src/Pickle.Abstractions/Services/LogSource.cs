using System.Globalization;
using System.Text.RegularExpressions;

namespace Pickle.Abstractions.Services;

public enum LogSeverity
{
    Debug,
    Info,
    Notice,
    Warning,
    Error,
    Critical,
}

public sealed record LogEntry(DateTimeOffset Time, LogSeverity Severity, string Source, string Message)
{
    /// <summary>Provider-specific extra detail (event id, pid, unit) shown in the details pane.</summary>
    public IReadOnlyDictionary<string, string> Fields { get; init; } = new Dictionary<string, string>();
}

/// <summary>What to fetch. Newest first; <see cref="Max"/> bounds the result.</summary>
public sealed record LogQuery
{
    public int Max { get; init; } = 300;

    public TimeSpan? Since { get; init; } = TimeSpan.FromHours(1);

    public LogSeverity MinSeverity { get; init; } = LogSeverity.Debug;

    /// <summary>A named log or scope from <see cref="ILogSource.Sources"/> ("System", "system", "kernel").</summary>
    public string? Source { get; init; }

    /// <summary>Only entries whose text contains this (applied by the backend where it can, otherwise after reading).</summary>
    public string? Text { get; init; }

    /// <summary>Only this unit or provider (systemd unit, Windows provider name, macOS process).</summary>
    public string? Unit { get; init; }

    /// <summary><c>30m</c>, <c>2h</c>, <c>7d</c>, <c>1w</c> as a time span; null for anything else.</summary>
    public static TimeSpan? ParseSince(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var match = Regex.Match(text.Trim(), @"^(\d{1,4})\s*([smhdw])$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        if (!match.Success)
        {
            return null;
        }

        var n = int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
        return char.ToLowerInvariant(match.Groups[2].Value[0]) switch
        {
            's' => TimeSpan.FromSeconds(n),
            'm' => TimeSpan.FromMinutes(n),
            'h' => TimeSpan.FromHours(n),
            'd' => TimeSpan.FromDays(n),
            _ => TimeSpan.FromDays(7 * n),
        };
    }
}

/// <summary>The machine's logs: Windows Event Log, systemd journal (or /var/log files), macOS unified log.</summary>
public interface ILogSource
{
    string Name { get; }

    bool IsSupported { get; }

    IReadOnlyList<string> Sources { get; }

    Task<IReadOnlyList<LogEntry>> QueryAsync(LogQuery query, CancellationToken cancellationToken = default);

    /// <summary>The PowerShell command that follows the log live, or null where there is no way to (Windows Event Log).</summary>
    string? FollowCommand(LogQuery query);
}
