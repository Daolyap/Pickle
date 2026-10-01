using System.Globalization;
using Pickle.Abstractions;
using Pickle.Abstractions.Services;
using Terminal.Gui.Input;

namespace Pickle.Tui.Panels.Admin;

/// <summary>
/// Scheduled jobs on Linux and macOS (Alt+S, the Task Scheduler's counterpart): the user's crontab, systemd timers
/// (system and user) and launchd agents. New jobs use the same friendly schedule syntax as <c>pk schedule</c> on Windows.
/// </summary>
internal sealed class TimersPanel : ResourcePanel<ScheduledJob>
{
    public const string PanelId = "scheduler";

    private readonly IJobScheduler _scheduler;

    public static PanelDescriptor Descriptor { get; } = new()
    {
        Id = PanelId,
        Title = "Scheduler",
        Description = "cron jobs, systemd timers and launchd agents: run, enable, disable, create, delete",
        DefaultKey = "Alt+S",
        CreateView = context => new TimersPanel(context),
    };

    public TimersPanel(PanelContext context)
        : base(context, "Scheduler", j => j.Name, "Job")
    {
        _scheduler = context.Pickle.Services.Get<IJobScheduler>() is { IsSupported: true } scheduler
            ? scheduler
            : throw new InvalidOperationException("Neither crontab, systemd timers nor launchd were found on this machine.");
        PanelTitle = $"Scheduler ({_scheduler.Name})";
        if (!string.IsNullOrEmpty(context.Argument))
        {
            List.FilterText = context.Argument;
        }

        AddAction(Key.F2, "Run now", (j, ct) => Report(_scheduler.RunNowAsync(j, ct)), confirm: j => $"Run '{j.Name}' now?");
        AddAction(Key.F3, "On/Off", (j, ct) => Report(_scheduler.SetEnabledAsync(j, !j.Enabled, ct)));
        AddAction(Key.F4, "Delete", (j, ct) => Report(_scheduler.DeleteAsync(j, ct)), confirm: j => $"Delete '{j.Name}'?", enabled: j => j.Managed);
        AddHint(Key.F6, "New job", NewJob);
        AddCommand(Key.F8, "History", j => _scheduler.HistoryCommand(j));
    }

    protected override TimeSpan? AutoRefresh => TimeSpan.FromSeconds(30);

    protected override string EmptyMessage => "No scheduled jobs yet. F6 creates one.";

    protected override Task<IReadOnlyList<ScheduledJob>> LoadAsync(CancellationToken cancellationToken) => _scheduler.ListAsync(cancellationToken);

    protected override string KeyOf(ScheduledJob item) => item.Kind + "|" + item.Scope + "|" + item.Id;

    protected override string? Hint(ScheduledJob item) => item.Enabled ? item.Schedule : "off · " + item.Schedule;

    protected override string? Detail(ScheduledJob item) => item.Command;

    protected override string? Category(ScheduledJob item) => $"{Label(item.Kind)} ({item.Scope})";

    protected override Terminal.Gui.Drawing.Color? ItemColor(ScheduledJob item) => item.Enabled ? null : Schemes.Muted.Foreground;

    protected override Task<IReadOnlyList<string>> DescribeAsync(ScheduledJob item, CancellationToken cancellationToken)
    {
        string When(DateTimeOffset? time) => time is { } t ? t.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture) : "—";
        return Task.FromResult<IReadOnlyList<string>>(
        [
            $"Name:     {item.Name}",
            $"Kind:     {Label(item.Kind)} ({item.Scope}){(item.Managed ? " · created by Pickle" : string.Empty)}",
            $"State:    {(item.Enabled ? "on" : "off")}",
            $"Schedule: {item.Schedule}",
            $"Next run: {When(item.NextRun)}",
            $"Last run: {When(item.LastRun)}",
            string.Empty,
            "Command:",
            "  " + item.Command,
            .. item.Description is { Length: > 0 } d && d != item.Schedule ? new[] { string.Empty, d } : [],
        ]);
    }

    internal static string Label(JobKind kind) => kind switch { JobKind.Cron => "cron", JobKind.SystemdTimer => "systemd timer", _ => "launchd" };

    private static async Task<ActionOutcome> Report(Task<ServiceOperationResult> operation)
    {
        var result = await operation.ConfigureAwait(false);
        return result.Success ? ActionOutcome.Done(result.Message) : new ActionOutcome(false, result.Message) { ShellCommand = result.ShellCommand };
    }

    private void NewJob()
    {
        var kinds = _scheduler.CreatableKinds;
        if (kinds.Count == 0)
        {
            ShowError("Nothing here can create jobs.");
            return;
        }

        var kind = kinds.Count == 1 ? kinds[0] : Pick("Create as", kinds, Label);
        if (kinds.Count > 1 && !kinds.Contains(kind))
        {
            return;
        }

        if (Prompt("New job", "name") is not { Length: > 0 } name
            || Prompt("When", "schedule (" + ScheduleParser.Examples + ")", "daily 09:00") is not { Length: > 0 } when
            || Prompt("Command", "command line") is not { Length: > 0 } command)
        {
            return;
        }

        TaskTriggerSpec trigger;
        try
        {
            trigger = ScheduleParser.Parse(when);
        }
        catch (FormatException ex)
        {
            ShowError(ex.Message);
            return;
        }

        RunInBackground(
            ct => _scheduler.CreateAsync(new NewJob(name.Trim(), trigger, command.Trim()), kind, ct),
            result =>
            {
                Details.ShowMessage(result.Success ? "Created" : "Not created", result.Message);
                Reload();
            },
            "creating…");
    }
}
