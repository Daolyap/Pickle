using Pickle.Abstractions;
using Pickle.Abstractions.Services;

namespace Pickle.Tui.Panels.Admin;

/// <summary><c>pk timers</c> / <c>pk schedule</c> on Linux and macOS: cron, systemd timers and launchd agents.</summary>
internal sealed class TimersCommand(string name) : PanelCommand
{
    public override string Name => name;

    public override string Description => "Scheduled jobs: cron, systemd timers and launchd agents (list, add, remove, run, enable, disable)";

    public override string Usage =>
        $"pk {name} [filter] | list | add \"<when>\" <command…> [--name n] [--kind cron|systemd|launchd] | remove|run|enable|disable <name> [--yes]   (when: {ScheduleParser.Examples})";

    public override IReadOnlyList<string> Examples =>
    [
        $"pk {name} add \"daily 09:00\" \"pk upgrade --yes\" --name morning-upgrade",
        $"pk {name} add \"every 30m\" \"~/bin/backup\" --kind systemd",
    ];

    protected override string PanelId => TimersPanel.PanelId;

    protected override async Task<int> RunAsync(CommandOutput output, IReadOnlyList<string> raw, CancellationToken cancellationToken)
    {
        var verb = raw.Count > 0 ? raw[0].ToLowerInvariant() : string.Empty;
        if (verb is not ("add" or "new" or "create" or "remove" or "rm" or "delete" or "run" or "enable" or "disable"))
        {
            return await base.RunAsync(output, raw, cancellationToken).ConfigureAwait(false);
        }

        var args = CommandArgs.Parse(raw.Skip(1).ToList(), "name", "kind", "description");
        var scheduler = Scheduler(output);
        if (verb is "add" or "new" or "create")
        {
            return await AddAsync(output, scheduler, args, cancellationToken).ConfigureAwait(false);
        }

        if (args.Arg(0) is not { } target)
        {
            return UsageError(output, $"Name the job to {verb}.");
        }

        var matches = (await scheduler.ListAsync(cancellationToken).ConfigureAwait(false))
            .Where(j => string.Equals(j.Name, target, StringComparison.OrdinalIgnoreCase) || string.Equals(j.Id, target, StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (matches.Count != 1)
        {
            output.Failure(matches.Count == 0 ? $"No job called '{target}'." : $"'{target}' matches {matches.Count} jobs; use its id: {string.Join(", ", matches.Select(m => m.Id))}.");
            return 1;
        }

        var job = matches[0];
        if (verb is "remove" or "rm" or "delete" or "run" && !output.Confirm(args, $"{(verb == "run" ? "Run" : "Delete")} '{job.Name}'?", verb == "run"))
        {
            output.Muted("Cancelled.");
            return 1;
        }

        var result = verb switch
        {
            "run" => await scheduler.RunNowAsync(job, cancellationToken).ConfigureAwait(false),
            "enable" => await scheduler.SetEnabledAsync(job, true, cancellationToken).ConfigureAwait(false),
            "disable" => await scheduler.SetEnabledAsync(job, false, cancellationToken).ConfigureAwait(false),
            _ => await scheduler.DeleteAsync(job, cancellationToken).ConfigureAwait(false),
        };
        return ResultOutput.Report(output, result);
    }

    protected override async Task<int> ListAsync(CommandOutput output, IReadOnlyList<string> args, CancellationToken cancellationToken)
    {
        var filter = args.FirstOrDefault();
        foreach (var job in await Scheduler(output).ListAsync(cancellationToken).ConfigureAwait(false))
        {
            if (filter is null || job.Name.Contains(filter, StringComparison.OrdinalIgnoreCase) || job.Command.Contains(filter, StringComparison.OrdinalIgnoreCase))
            {
                output.Object(Display.Columns(job, "Name", "Kind", "Schedule", "Enabled", "NextRun"));
            }
        }

        return 0;
    }

    private async Task<int> AddAsync(CommandOutput output, IJobScheduler scheduler, CommandArgs args, CancellationToken cancellationToken)
    {
        if (args.Arg(0) is not { } when || args.Positional.Count < 2)
        {
            return UsageError(output, "Give a schedule and a command.");
        }

        var command = args.Rest(1);
        var trigger = ScheduleParser.Parse(when);
        var kind = args.Value("kind") is { } text
            ? KindFrom(text)
            : scheduler.CreatableKinds.Count > 0 ? scheduler.CreatableKinds[0] : throw new InvalidOperationException("Nothing here can create jobs.");
        var jobName = args.Value("name") ?? "job-" + DateTime.Now.ToString("yyMMdd-HHmmss", System.Globalization.CultureInfo.InvariantCulture);
        return ResultOutput.Report(output, await scheduler.CreateAsync(new NewJob(jobName, trigger, command, args.Value("description")), kind, cancellationToken).ConfigureAwait(false));
    }

    private static JobKind KindFrom(string text) => text.ToLowerInvariant() switch
    {
        "cron" => JobKind.Cron,
        "systemd" or "timer" => JobKind.SystemdTimer,
        "launchd" => JobKind.Launchd,
        _ => throw new ArgumentException($"'{text}' is not cron, systemd or launchd."),
    };

    private static IJobScheduler Scheduler(CommandOutput output) =>
        output.Pickle.Services.Get<IJobScheduler>() is { IsSupported: true } scheduler
            ? scheduler
            : throw new InvalidOperationException("Neither crontab, systemd timers nor launchd were found on this machine. On Windows use 'pk schedule'.");
}
