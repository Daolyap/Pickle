using Pickle.Abstractions;
using Pickle.Abstractions.Services;

namespace Pickle.Admin.Scheduling;

/// <summary>The Task Scheduler equivalent for Linux and macOS: crontab, systemd timers and launchd agents behind one list.</summary>
public sealed class UnixJobScheduler : IJobScheduler
{
    private readonly IReadOnlyList<IJobBackend> _backends;

    public UnixJobScheduler(IProgramRunner runner, IPrivilegeService privilege, Func<DateTimeOffset>? now = null, string? userUnitDirectory = null, string? agentsDirectory = null)
    {
        now ??= () => DateTimeOffset.Now;
        _backends = [new SystemdTimerBackend(runner, privilege, userUnitDirectory), new CronBackend(runner, now), new LaunchdBackend(runner, agentsDirectory)];
    }

    public string Name => string.Join(" + ", Available.Select(b => b.Kind switch { JobKind.Cron => "cron", JobKind.SystemdTimer => "systemd timers", _ => "launchd" }));

    public bool IsSupported => !OperatingSystem.IsWindows() && Available.Any();

    public IReadOnlyList<JobKind> CreatableKinds => [.. Available.Where(b => b.CanCreate).Select(b => b.Kind)];

    private IEnumerable<IJobBackend> Available => _backends.Where(b => b.IsAvailable);

    public async Task<IReadOnlyList<ScheduledJob>> ListAsync(CancellationToken cancellationToken = default)
    {
        var jobs = new List<ScheduledJob>();
        foreach (var backend in Available)
        {
            jobs.AddRange(await backend.ListAsync(cancellationToken).ConfigureAwait(false));
        }

        return [.. jobs.OrderBy(j => j.Kind).ThenBy(j => j.Scope).ThenBy(j => j.Name, StringComparer.OrdinalIgnoreCase)];
    }

    public Task<ServiceOperationResult> CreateAsync(NewJob job, JobKind kind, CancellationToken cancellationToken = default)
    {
        try
        {
            return Backend(kind).CreateAsync(job, cancellationToken);
        }
        catch (NotSupportedException ex)
        {
            return Task.FromResult(new ServiceOperationResult(false, ex.Message));
        }
    }

    public Task<ServiceOperationResult> RunNowAsync(ScheduledJob job, CancellationToken cancellationToken = default) => Backend(job.Kind).RunNowAsync(job, cancellationToken);

    public Task<ServiceOperationResult> SetEnabledAsync(ScheduledJob job, bool enabled, CancellationToken cancellationToken = default) =>
        Backend(job.Kind).SetEnabledAsync(job, enabled, cancellationToken);

    public Task<ServiceOperationResult> DeleteAsync(ScheduledJob job, CancellationToken cancellationToken = default) => Backend(job.Kind).DeleteAsync(job, cancellationToken);

    public string? HistoryCommand(ScheduledJob job) => Backend(job.Kind).HistoryCommand(job);

    private IJobBackend Backend(JobKind kind) =>
        _backends.FirstOrDefault(b => b.Kind == kind && b.IsAvailable) ?? throw new InvalidOperationException($"{kind} is not available on this machine.");
}
