namespace Pickle.Abstractions.Services;

public enum JobKind
{
    Cron,
    SystemdTimer,
    Launchd,
}

/// <summary>A cron entry, systemd timer or launchd agent: the Linux and macOS counterpart of a Windows scheduled task.</summary>
public sealed record ScheduledJob(string Id, string Name, JobKind Kind, string Schedule, string Command, bool Enabled)
{
    public DateTimeOffset? NextRun { get; init; }

    public DateTimeOffset? LastRun { get; init; }

    /// <summary>"user" or "system".</summary>
    public string Scope { get; init; } = "user";

    /// <summary>Created by Pickle (so Pickle may also change its definition and delete it).</summary>
    public bool Managed { get; init; }

    public string? Description { get; init; }
}

public sealed record NewJob(string Name, TaskTriggerSpec Trigger, string Command, string? Description = null);

/// <summary>Scheduled jobs of the current user (and, read-only plus enable/disable/run, of the system).</summary>
public interface IJobScheduler
{
    string Name { get; }

    bool IsSupported { get; }

    /// <summary>The kinds a new job can be created as, preferred first.</summary>
    IReadOnlyList<JobKind> CreatableKinds { get; }

    Task<IReadOnlyList<ScheduledJob>> ListAsync(CancellationToken cancellationToken = default);

    Task<ServiceOperationResult> CreateAsync(NewJob job, JobKind kind, CancellationToken cancellationToken = default);

    Task<ServiceOperationResult> RunNowAsync(ScheduledJob job, CancellationToken cancellationToken = default);

    Task<ServiceOperationResult> SetEnabledAsync(ScheduledJob job, bool enabled, CancellationToken cancellationToken = default);

    Task<ServiceOperationResult> DeleteAsync(ScheduledJob job, CancellationToken cancellationToken = default);

    /// <summary>The shell command that shows what the job printed last (journalctl, a log file), or null.</summary>
    string? HistoryCommand(ScheduledJob job);
}
