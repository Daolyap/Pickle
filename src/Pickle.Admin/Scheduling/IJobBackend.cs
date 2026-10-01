using Pickle.Abstractions.Services;

namespace Pickle.Admin.Scheduling;

internal interface IJobBackend
{
    JobKind Kind { get; }

    bool IsAvailable { get; }

    bool CanCreate { get; }

    Task<IReadOnlyList<ScheduledJob>> ListAsync(CancellationToken cancellationToken);

    Task<ServiceOperationResult> CreateAsync(NewJob job, CancellationToken cancellationToken);

    Task<ServiceOperationResult> RunNowAsync(ScheduledJob job, CancellationToken cancellationToken);

    Task<ServiceOperationResult> SetEnabledAsync(ScheduledJob job, bool enabled, CancellationToken cancellationToken);

    Task<ServiceOperationResult> DeleteAsync(ScheduledJob job, CancellationToken cancellationToken);

    string? HistoryCommand(ScheduledJob job);
}
