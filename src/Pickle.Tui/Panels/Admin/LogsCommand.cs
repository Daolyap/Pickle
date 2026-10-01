using Pickle.Abstractions;
using Pickle.Abstractions.Services;

namespace Pickle.Tui.Panels.Admin;

/// <summary><c>pk logs</c>: the panel interactively, objects in scripts.</summary>
internal sealed class LogsCommand : PanelCommand
{
    public override string Name => "logs";

    public override string Description => "Event Log, systemd journal or unified log";

    public override string Usage => "pk logs [text] | list [text] [--source name] [--level warning|error|critical] [--since 1h] [--unit name] [-n 200]";

    public override IReadOnlyList<string> Examples =>
    [
        "pk logs list --level error --since 24h",
        "pk logs list --unit ssh.service --since 7d | Where-Object Message -like '*Failed*'",
    ];

    protected override string PanelId => LogsPanel.PanelId;

    protected override async Task<int> ListAsync(CommandOutput output, IReadOnlyList<string> raw, CancellationToken cancellationToken)
    {
        var source = output.Pickle.Services.Get<ILogSource>() is { IsSupported: true } s ? s : throw new InvalidOperationException("No log source is available on this machine.");
        var args = CommandArgs.Parse(raw, "source", "level", "since", "unit", "n");
        var query = new LogQuery
        {
            Source = args.Value("source"),
            Unit = args.Value("unit"),
            Text = args.Arg(0),
            Max = int.TryParse(args.Value("n"), out var n) && n > 0 ? n : 200,
            Since = args.Value("since") is { } since ? LogQuery.ParseSince(since) ?? throw new ArgumentException($"'{since}' is not a time range like 30m, 2h or 7d.") : TimeSpan.FromHours(1),
            MinSeverity = args.Value("level") is { } level
                ? (Enum.TryParse<LogSeverity>(level, ignoreCase: true, out var severity) ? severity : throw new ArgumentException($"'{level}' is not a severity (debug, info, notice, warning, error, critical)."))
                : LogSeverity.Debug,
        };
        foreach (var entry in await source.QueryAsync(query, cancellationToken).ConfigureAwait(false))
        {
            output.Object(Display.Columns(entry, "Time", "Severity", "Source", "Message"));
        }

        return 0;
    }
}
