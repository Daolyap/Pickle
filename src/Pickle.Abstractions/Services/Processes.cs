namespace Pickle.Abstractions.Services;

/// <summary>One process in a <see cref="ProcessSnapshot"/>. <see cref="CpuPercent"/> is a share of the whole machine (0–100).</summary>
public sealed record ProcessSample(
    int Id,
    string Name,
    double CpuPercent,
    long WorkingSet,
    int Threads,
    DateTimeOffset? StartTime,
    string? User = null,
    int? ParentId = null);

/// <summary>All processes plus machine totals. <see cref="MemoryTotal"/> is 0 when unknown.</summary>
public sealed record ProcessSnapshot(
    DateTimeOffset Time,
    double CpuPercent,
    long MemoryUsed,
    long MemoryTotal,
    IReadOnlyList<ProcessSample> Processes);

public enum ProcessPriority
{
    Idle,
    BelowNormal,
    Normal,
    AboveNormal,
    High,
}

public sealed record ProcessDetails(
    int Id,
    string Name,
    string? Path,
    string? CommandLine,
    int? ParentId,
    string? ParentName,
    string? User,
    DateTimeOffset? StartTime,
    ProcessPriority? Priority,
    long WorkingSet,
    long? PrivateMemory,
    int Threads,
    TimeSpan? TotalCpuTime);

public sealed record ProcessActionResult(bool Success, string Message);

/// <summary>Process list and actions for the Processes panel (Alt+P). Implemented in Pickle.Core; fakes in Pickle.Testing.</summary>
public interface IProcessMonitor
{
    /// <summary>Samples every accessible process. CPU % is measured since the previous call.</summary>
    Task<ProcessSnapshot> SampleAsync(CancellationToken cancellationToken = default);

    /// <summary>Null when the process has exited (or its id now belongs to another process).</summary>
    Task<ProcessDetails?> GetDetailsAsync(ProcessSample process, CancellationToken cancellationToken = default);

    Task<ProcessActionResult> KillAsync(ProcessSample process, bool entireTree, CancellationToken cancellationToken = default);

    Task<ProcessActionResult> SetPriorityAsync(ProcessSample process, ProcessPriority priority, CancellationToken cancellationToken = default);
}
