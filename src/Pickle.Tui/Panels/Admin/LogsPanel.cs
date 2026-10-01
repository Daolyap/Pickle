using System.Globalization;
using Pickle.Abstractions;
using Pickle.Abstractions.Services;
using Terminal.Gui.Input;

namespace Pickle.Tui.Panels.Admin;

/// <summary>
/// The machine's logs (Alt+L): Windows Event Log, systemd journal (or /var/log files) or the macOS unified log. The filter box
/// searches what is loaded; F6 raises the minimum severity, F7 picks the log, F9 the time range, F8 follows it live in the shell.
/// </summary>
internal sealed class LogsPanel : ResourcePanel<LogEntry>
{
    public const string PanelId = "logs";

    private static readonly string[] Ranges = ["15m", "1h", "6h", "24h", "7d"];
    private readonly ILogSource _source;
    private LogQuery _query = new();

    public static PanelDescriptor Descriptor { get; } = new()
    {
        Id = PanelId,
        Title = "Logs",
        Description = "Event Log, journal or unified log: filter by severity, log and time, follow live",
        DefaultKey = "Alt+L",
        CreateView = context => new LogsPanel(context),
    };

    public LogsPanel(PanelContext context)
        : base(context, "Logs", e => $"{e.Time:MM-dd HH:mm:ss} {e.Source}", "Entry")
    {
        _source = context.Pickle.Services.Require<ILogSource>();
        List.Keywords = e => e.Message;
        List.Detail = e => First(e.Message);
        if (!string.IsNullOrEmpty(context.Argument))
        {
            List.FilterText = context.Argument;
        }

        PanelTitle = Heading(_query);
        AddHint(Key.F6, "Severity", CycleSeverity);
        AddHint(Key.F7, "Log", PickSource);
        AddHint(Key.F9, "Time range", CycleRange);
        AddCommand(Key.F8, "Follow", _ => _source.FollowCommand(_query));
    }

    protected override string EmptyMessage => _source.IsSupported ? "No entries match. F6 lowers the severity, F9 widens the time range." : _source.Name + " is not available here.";

    protected override Task<IReadOnlyList<LogEntry>> LoadAsync(CancellationToken cancellationToken) => _source.QueryAsync(_query, cancellationToken);

    protected override string KeyOf(LogEntry item) => $"{item.Time.UtcTicks}|{item.Source}|{item.Message.GetHashCode(StringComparison.Ordinal)}";

    protected override string? Hint(LogEntry item) => item.Severity.ToString();

    protected override Task<IReadOnlyList<string>> DescribeAsync(LogEntry item, CancellationToken cancellationToken)
    {
        var lines = new List<string>
        {
            "Time:     " + item.Time.ToString("yyyy-MM-dd HH:mm:ss zzz", CultureInfo.InvariantCulture),
            "Severity: " + item.Severity,
            "Source:   " + item.Source,
        };
        lines.AddRange(item.Fields.Select(f => $"{f.Key + ":",-9} {f.Value}"));
        lines.Add(string.Empty);
        lines.AddRange(Wrap(item.Message, 76));
        return Task.FromResult<IReadOnlyList<string>>(lines);
    }

    protected override Terminal.Gui.Drawing.Color? ItemColor(LogEntry item) => item.Severity switch
    {
        LogSeverity.Critical or LogSeverity.Error => Schemes.ErrorText.Foreground,
        LogSeverity.Warning => Schemes.Warning.Foreground,
        LogSeverity.Debug => Schemes.Muted.Foreground,
        _ => null,
    };

    internal static IEnumerable<string> Wrap(string text, int width)
    {
        foreach (var raw in text.Replace("\r", string.Empty, StringComparison.Ordinal).Split('\n'))
        {
            var line = raw;
            while (line.Length > width)
            {
                var cut = line.LastIndexOf(' ', width);
                cut = cut <= 0 ? width : cut;
                yield return line[..cut];
                line = line[cut..].TrimStart();
            }

            yield return line;
        }
    }

    private static string First(string message)
    {
        var line = message.Split('\n', 2)[0];
        return line.Length <= 120 ? line : line[..117] + "…";
    }

    private string Heading(LogQuery query) =>
        $"Logs ({_source.Name}) · {query.Source ?? "default"} · ≥{query.MinSeverity} · last {Ranges.FirstOrDefault(r => LogQuery.ParseSince(r) == query.Since) ?? "?"}";

    private void CycleSeverity()
    {
        var next = _query.MinSeverity switch
        {
            LogSeverity.Debug => LogSeverity.Warning,
            LogSeverity.Info or LogSeverity.Notice => LogSeverity.Warning,
            LogSeverity.Warning => LogSeverity.Error,
            LogSeverity.Error => LogSeverity.Critical,
            _ => LogSeverity.Debug,
        };
        Apply(_query with { MinSeverity = next });
    }

    private void CycleRange()
    {
        var index = Array.FindIndex(Ranges, r => LogQuery.ParseSince(r) == _query.Since);
        Apply(_query with { Since = LogQuery.ParseSince(Ranges[(index + 1) % Ranges.Length]) });
    }

    private void PickSource()
    {
        if (_source.Sources.Count == 0)
        {
            return;
        }

        if (Pick("Log", _source.Sources, s => s) is { } chosen)
        {
            Apply(_query with { Source = chosen });
        }
    }

    private void Apply(LogQuery query)
    {
        _query = query;
        PanelTitle = Heading(query);
        Reload();
    }
}
