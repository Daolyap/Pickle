namespace Pickle.Abstractions.Services;

public sealed record ScheduledTaskInfo(
    string Path,
    string Name,
    string Folder,
    bool Enabled,
    string State,
    DateTimeOffset? LastRunTime,
    DateTimeOffset? NextRunTime,
    int? LastResult,
    string? Description,
    string? Author,
    IReadOnlyList<string> Triggers,
    IReadOnlyList<string> Actions,
    bool RunElevated);

public sealed record ScheduledTaskRun(DateTimeOffset Time, string Event, int? ResultCode, string? Message);

public enum TaskTriggerKind
{
    Once,
    Daily,
    Weekly,
    Monthly,
    AtLogon,
    AtStartup,
    OnIdle,

    /// <summary>Starts at <see cref="TaskTriggerSpec.Start"/> and repeats every <see cref="TaskTriggerSpec.RepeatEvery"/> indefinitely.</summary>
    Interval,
}

public sealed record TaskTriggerSpec(
    TaskTriggerKind Kind,
    DateTimeOffset? Start = null,
    int DaysInterval = 1,
    IReadOnlyList<DayOfWeek>? DaysOfWeek = null,
    int WeeksInterval = 1,
    IReadOnlyList<int>? DaysOfMonth = null,
    TimeSpan? RepeatEvery = null,
    TimeSpan? RepeatDuration = null);

public sealed record TaskActionSpec(string Program, string? Arguments = null, string? WorkingDirectory = null);

public sealed record ScheduledTaskDefinition(
    string Name,
    TaskTriggerSpec Trigger,
    TaskActionSpec Action,
    string Folder = @"\Pickle",
    string? Description = null,
    bool RunElevated = false,
    bool Hidden = false);

public interface ITaskSchedulerService
{
    bool IsSupported { get; }

    Task<IReadOnlyList<string>> GetFoldersAsync(string root = @"\", CancellationToken cancellationToken = default);

    Task<IReadOnlyList<ScheduledTaskInfo>> GetTasksAsync(string folder = @"\", bool recurse = false, CancellationToken cancellationToken = default);

    Task<ScheduledTaskInfo?> GetTaskAsync(string path, CancellationToken cancellationToken = default);

    Task RunAsync(string path, CancellationToken cancellationToken = default);

    Task StopAsync(string path, CancellationToken cancellationToken = default);

    Task SetEnabledAsync(string path, bool enabled, CancellationToken cancellationToken = default);

    Task DeleteAsync(string path, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<ScheduledTaskRun>> GetHistoryAsync(string path, int max = 50, CancellationToken cancellationToken = default);

    /// <summary>Creates or replaces a task. Elevated tasks go through the elevation broker when the process isn't elevated.</summary>
    Task<ScheduledTaskInfo> CreateAsync(ScheduledTaskDefinition definition, CancellationToken cancellationToken = default);

    /// <summary>Parse a friendly schedule ("every 30m", "daily 09:00", "weekly mon,fri 18:30", "at logon", "once 2026-10-01 12:00").</summary>
    TaskTriggerSpec ParseSchedule(string text);
}
