using System.Globalization;
using System.Management.Automation;
using Pickle.Abstractions;
using Pickle.Abstractions.Services;

namespace Pickle.Windows.Logs;

/// <summary>The Windows Event Log through <c>Get-WinEvent</c> (run in a background runspace, values passed as parameters).</summary>
internal sealed class WindowsEventLogSource(Func<IPickleShell> shell) : ILogSource
{
    internal const string Script = """
        param($Log, $Level, $Since, $Max, $Provider)
        $filter = @{ LogName = $Log }
        if ($Since) { $filter['StartTime'] = (Get-Date).AddSeconds(-$Since) }
        if ($Level) { $filter['Level'] = @($Level) }
        if ($Provider) { $filter['ProviderName'] = $Provider }
        try {
            Get-WinEvent -FilterHashtable $filter -MaxEvents $Max -ErrorAction Stop | ForEach-Object {
                [pscustomobject]@{
                    Time = $_.TimeCreated; Level = [int]$_.Level; Provider = $_.ProviderName; Id = [int]$_.Id
                    Message = [string]$_.Message; Machine = $_.MachineName; Task = $_.TaskDisplayName
                }
            }
        } catch [System.Exception] {
            if ($_.FullyQualifiedErrorId -notlike 'NoMatchingEventsFound*') { throw }
        }
        """;

    public string Name => "Windows Event Log";

    public bool IsSupported => OperatingSystem.IsWindows();

    public IReadOnlyList<string> Sources => ["System", "Application", "Setup", "Windows PowerShell", "Microsoft-Windows-PowerShell/Operational"];

    public async Task<IReadOnlyList<LogEntry>> QueryAsync(LogQuery query, CancellationToken cancellationToken = default)
    {
        var log = query.Source ?? "System";
        if (!Sources.Contains(log, StringComparer.OrdinalIgnoreCase) && !IsLogName(log))
        {
            throw new ArgumentException($"'{log}' is not a log name.");
        }

        var parameters = new Dictionary<string, object?>
        {
            ["Log"] = log,
            ["Level"] = Levels(query.MinSeverity),
            ["Since"] = query.Since is { } since ? (int)since.TotalSeconds : null,
            ["Max"] = Math.Clamp(query.Max, 1, 5000),
            ["Provider"] = query.Unit,
        };
        var result = await shell().InvokeAsync(Script, parameters, ShellTarget.Background, cancellationToken).ConfigureAwait(false);
        if (result.HadErrors && result.Output.Count == 0)
        {
            var message = result.Errors[0].Exception.Message;
            throw new InvalidOperationException(message.Contains("Attempted to perform an unauthorized operation", StringComparison.OrdinalIgnoreCase)
                ? $"{log} can only be read by administrators."
                : message);
        }

        var entries = result.Output.Select(Map).OfType<LogEntry>();
        if (query.Text is { Length: > 0 } text)
        {
            entries = entries.Where(e => e.Message.Contains(text, StringComparison.OrdinalIgnoreCase) || e.Source.Contains(text, StringComparison.OrdinalIgnoreCase));
        }

        return [.. entries];
    }

    public string? FollowCommand(LogQuery query) => null;

    internal static LogEntry? Map(PSObject row)
    {
        if (row.Properties["Time"]?.Value is not DateTime time)
        {
            return null;
        }

        var fields = new Dictionary<string, string>();
        foreach (var (property, label) in new[] { ("Id", "Event id"), ("Machine", "Machine"), ("Task", "Task") })
        {
            if (row.Properties[property]?.Value?.ToString() is { Length: > 0 } value)
            {
                fields[label] = value;
            }
        }

        return new LogEntry(
            new DateTimeOffset(time),
            FromLevel(row.Properties["Level"]?.Value is int level ? level : 4),
            row.Properties["Provider"]?.Value?.ToString() ?? string.Empty,
            (row.Properties["Message"]?.Value?.ToString() ?? string.Empty).Replace("\r\n", " ", StringComparison.Ordinal).Replace('\n', ' '))
        { Fields = fields };
    }

    // 1 critical, 2 error, 3 warning, 4 information, 5 verbose (0 = LogAlways)
    internal static LogSeverity FromLevel(int level) => level switch
    {
        1 => LogSeverity.Critical,
        2 => LogSeverity.Error,
        3 => LogSeverity.Warning,
        5 => LogSeverity.Debug,
        _ => LogSeverity.Info,
    };

    internal static int[]? Levels(LogSeverity min) => min switch
    {
        LogSeverity.Critical => [1],
        LogSeverity.Error => [1, 2],
        LogSeverity.Warning or LogSeverity.Notice => [1, 2, 3],
        _ => null,
    };

    private static bool IsLogName(string name) =>
        name.Length is > 0 and <= 128 && name.All(c => char.IsAsciiLetterOrDigit(c) || c is ' ' or '-' or '_' or '/' or '.');

    internal static string Describe(int level) => level.ToString(CultureInfo.InvariantCulture);
}
