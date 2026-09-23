using System.Runtime.Versioning;
using System.Security.Principal;
using Microsoft.Win32.TaskScheduler;
using Pickle.Abstractions.Services;
using TaskAction = Microsoft.Win32.TaskScheduler.Action;

namespace Pickle.Windows.TaskScheduler;

/// <summary>Builds and registers Task Scheduler definitions (shared by the service and the elevated helper).</summary>
[SupportedOSPlatform("windows")]
internal static class TaskRegistrar
{
    public static ScheduledTaskInfo Register(ScheduledTaskDefinition definition)
    {
        TaskDefinitionCodec.Validate(definition, definition.RunElevated);
        var user = WindowsIdentity.GetCurrent().Name;
        using var service = new TaskService();
        var folder = EnsureFolder(service, definition.Folder);
        using var task = service.NewTask();
        task.RegistrationInfo.Description = definition.Description ?? "Created by Pickle";
        task.RegistrationInfo.Author = user;
        task.Settings.Hidden = definition.Hidden;
        task.Settings.StartWhenAvailable = true;
        task.Settings.DisallowStartIfOnBatteries = false;
        task.Settings.StopIfGoingOnBatteries = false;
        task.Settings.MultipleInstances = TaskInstancesPolicy.IgnoreNew;
        task.Triggers.Add(BuildTrigger(definition.Trigger, user));
        task.Actions.Add(new ExecAction(definition.Action.Program, definition.Action.Arguments, definition.Action.WorkingDirectory));

        var logonType = TaskLogonType.InteractiveToken;
        if (definition.RunElevated)
        {
            task.Principal.RunLevel = TaskRunLevel.Highest;
        }

        if (definition.Trigger.Kind == TaskTriggerKind.AtStartup)
        {
            // Nobody is logged on at boot: S4U runs as the user without storing a password (requires elevation).
            logonType = TaskLogonType.S4U;
        }

        if (definition.Trigger.Kind == TaskTriggerKind.OnIdle)
        {
            task.Settings.IdleSettings.IdleDuration = TimeSpan.FromMinutes(10);
            task.Settings.IdleSettings.StopOnIdleEnd = false;
        }

        task.Principal.LogonType = logonType;
        using var registered = folder.RegisterTaskDefinition(definition.Name, task, TaskCreation.CreateOrUpdate, user, null, logonType);
        return TaskMapper.Map(registered);
    }

    public static TaskFolder EnsureFolder(TaskService service, string path)
    {
        var folder = service.RootFolder;
        foreach (var segment in path.Split('\\', StringSplitOptions.RemoveEmptyEntries))
        {
            var existing = folder.SubFolders.FirstOrDefault(f => string.Equals(f.Name, segment, StringComparison.OrdinalIgnoreCase));
            folder = existing ?? folder.CreateFolder(segment, (string?)null, false);
        }

        return folder;
    }

    internal static Trigger BuildTrigger(TaskTriggerSpec spec, string user)
    {
        // DateTimeOffset.DateTime is the wall-clock time as written; Task Scheduler interprets it as local time.
        var start = spec.Start?.DateTime ?? DateTime.Now;
        Trigger trigger = spec.Kind switch
        {
            TaskTriggerKind.Once => new TimeTrigger(start),
            TaskTriggerKind.Daily => new DailyTrigger((short)spec.DaysInterval) { StartBoundary = start },
            TaskTriggerKind.Weekly => new WeeklyTrigger(ToDays(spec.DaysOfWeek ?? []), (short)spec.WeeksInterval) { StartBoundary = start },
            TaskTriggerKind.Monthly => new MonthlyTrigger((spec.DaysOfMonth ?? [1])[0], MonthsOfTheYear.AllMonths)
            {
                DaysOfMonth = [.. spec.DaysOfMonth ?? [1]],
                StartBoundary = start,
            },
            TaskTriggerKind.AtLogon => new LogonTrigger { UserId = user },
            TaskTriggerKind.AtStartup => new BootTrigger(),
            TaskTriggerKind.OnIdle => new IdleTrigger(),
            TaskTriggerKind.Interval => new TimeTrigger(start),
            _ => throw new ArgumentException($"Unsupported trigger {spec.Kind}."),
        };

        if (spec.RepeatEvery is { } every)
        {
            trigger.Repetition = new RepetitionPattern(every, spec.RepeatDuration ?? TimeSpan.Zero, false);
        }

        return trigger;
    }

    private static DaysOfTheWeek ToDays(IReadOnlyList<DayOfWeek> days)
    {
        DaysOfTheWeek result = 0;
        foreach (var day in days)
        {
            result |= day switch
            {
                DayOfWeek.Sunday => DaysOfTheWeek.Sunday,
                DayOfWeek.Monday => DaysOfTheWeek.Monday,
                DayOfWeek.Tuesday => DaysOfTheWeek.Tuesday,
                DayOfWeek.Wednesday => DaysOfTheWeek.Wednesday,
                DayOfWeek.Thursday => DaysOfTheWeek.Thursday,
                DayOfWeek.Friday => DaysOfTheWeek.Friday,
                _ => DaysOfTheWeek.Saturday,
            };
        }

        return result;
    }
}

[SupportedOSPlatform("windows")]
internal static class TaskMapper
{
    public static ScheduledTaskInfo Map(Microsoft.Win32.TaskScheduler.Task task)
    {
        string? description = null;
        string? author = null;
        IReadOnlyList<string> triggers = [];
        IReadOnlyList<string> actions = [];
        var elevated = false;
        try
        {
            var definition = task.Definition;
            description = definition.RegistrationInfo.Description;
            author = definition.RegistrationInfo.Author;
            triggers = [.. definition.Triggers.Select(t => t.ToString())];
            actions = [.. definition.Actions.Select(Describe)];
            elevated = definition.Principal.RunLevel == TaskRunLevel.Highest;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Runtime.InteropServices.COMException or InvalidOperationException)
        {
        }

        var lastRun = Date(task.LastRunTime);
        return new ScheduledTaskInfo(
            task.Path,
            task.Name,
            task.Folder?.Path ?? "\\",
            task.Enabled,
            task.State.ToString(),
            lastRun,
            Date(task.NextRunTime),
            lastRun is null ? null : task.LastTaskResult,
            description,
            author,
            triggers,
            actions,
            elevated);
    }

    private static string Describe(TaskAction action) =>
        action is ExecAction exec ? $"{exec.Path} {exec.Arguments}".TrimEnd() : action.ToString();

    private static DateTimeOffset? Date(DateTime value) =>
        value.Year < 2000 || value == DateTime.MaxValue ? null : new DateTimeOffset(DateTime.SpecifyKind(value, DateTimeKind.Local));
}
