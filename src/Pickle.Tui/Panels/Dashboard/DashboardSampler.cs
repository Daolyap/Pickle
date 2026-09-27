using Pickle.Abstractions;
using Pickle.Abstractions.Services;
using Pickle.Tui.Panels.SystemMonitoring;

namespace Pickle.Tui.Panels.Dashboard;

/// <summary>
/// Collects dashboard frames from the system monitors. Processes and network are sampled every call (their rates are
/// measured between calls); disks every 10 s and the system summary every 5 s. A source that fails keeps its last value.
/// </summary>
internal sealed class DashboardSampler(IPickleContext pickle, Func<DateTimeOffset>? clock = null)
{
    private static readonly TimeSpan VolumeInterval = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan SystemInterval = TimeSpan.FromSeconds(5);

    private readonly SampleHistory _cpu = new();
    private readonly SampleHistory _memory = new();
    private readonly SampleHistory _down = new();
    private readonly SampleHistory _up = new();
    private readonly Func<DateTimeOffset> _clock = clock ?? (() => DateTimeOffset.UtcNow);
    private ProcessSnapshot? _processes;
    private IReadOnlyList<NetworkInterfaceSample> _interfaces = [];
    private IReadOnlyList<DiskVolume> _volumes = [];
    private IReadOnlyList<DiskIoSample> _diskIo = [];
    private SystemSummary? _system;
    private DateTimeOffset _volumesAt = DateTimeOffset.MinValue;
    private DateTimeOffset _systemAt = DateTimeOffset.MinValue;

    public async Task<DashboardData> SampleAsync(CancellationToken cancellationToken)
    {
        var services = pickle.Services;
        var now = _clock();
        var processes = Try("processes", services.Get<IProcessMonitor>() is { } p ? p.SampleAsync(cancellationToken) : null);
        var interfaces = Try("network", services.Get<INetworkMonitor>() is { } n ? n.SampleInterfacesAsync(cancellationToken) : null);
        var disks = services.Get<IDiskMonitor>();
        var volumes = now - _volumesAt >= VolumeInterval ? Try("disks", disks?.GetVolumesAsync(cancellationToken)) : Task.FromResult<IReadOnlyList<DiskVolume>?>(null);
        var diskIo = Try("disk I/O", disks is { SupportsIoRates: true } ? disks.SampleIoAsync(cancellationToken) : null);
        var system = now - _systemAt >= SystemInterval ? Try("system", services.Get<ISystemInfo>()?.GetSummaryAsync(cancellationToken)) : Task.FromResult<SystemSummary?>(null);
        await Task.WhenAll(processes, interfaces, volumes, diskIo, system).ConfigureAwait(false);

        if (await processes.ConfigureAwait(false) is { } snapshot)
        {
            _processes = snapshot;
            _cpu.Add(snapshot.CpuPercent);
            _memory.Add(snapshot.MemoryTotal > 0 ? 100.0 * snapshot.MemoryUsed / snapshot.MemoryTotal : 0);
        }

        if (await interfaces.ConfigureAwait(false) is { } nics)
        {
            _interfaces = nics;
            _down.Add(nics.Sum(i => i.ReceiveRate));
            _up.Add(nics.Sum(i => i.SendRate));
        }

        if (await volumes.ConfigureAwait(false) is { } found)
        {
            (_volumes, _volumesAt) = (found, now);
        }

        _diskIo = await diskIo.ConfigureAwait(false) ?? _diskIo;
        if (await system.ConfigureAwait(false) is { } summary)
        {
            (_system, _systemAt) = (summary, now);
        }

        return new DashboardData(
            _system, _processes, _interfaces, _volumes, _diskIo, [.. _cpu.Values], [.. _memory.Values], [.. _down.Values], [.. _up.Values],
            OperatingSystem.IsWindows());
    }

    private async Task<T?> Try<T>(string what, Task<T>? task)
        where T : class
    {
        if (task is null)
        {
            return null;
        }

        try
        {
            return await task.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            pickle.Log.Warn("dashboard", $"Reading {what} failed", ex);
            return null;
        }
    }
}
