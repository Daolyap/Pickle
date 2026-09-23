using Pickle.Core.Contracts;

namespace Pickle.Core.Sync;

/// <summary>FOUNDATION PLACEHOLDER (workstream W5 replaces this file): sync not configured.</summary>
public sealed class SyncService : ISyncService
{
    private readonly PickleRuntime _runtime;

    public SyncService(PickleRuntime runtime) => _runtime = runtime;

    public Task<SyncReport> PushAsync(CancellationToken cancellationToken = default) => NotConfigured();

    public Task<SyncReport> PullAsync(CancellationToken cancellationToken = default) => NotConfigured();

    public Task<SyncReport> StatusAsync(CancellationToken cancellationToken = default) => NotConfigured();

    public Task<SyncReport> InitAsync(string backend, string target, CancellationToken cancellationToken = default) => NotConfigured();

    private static Task<SyncReport> NotConfigured() => Task.FromResult(new SyncReport(false, "Sync is not configured.", []));
}
