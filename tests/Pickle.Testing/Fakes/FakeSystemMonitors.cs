using Pickle.Abstractions.Services;

namespace Pickle.Testing.Fakes;

/// <summary>In-memory process monitor: set <see cref="Processes"/>; kills and priority changes are recorded in <see cref="Calls"/>.</summary>
public sealed class FakeProcessMonitor : IProcessMonitor
{
    public List<ProcessSample> Processes { get; } = [];
    public List<string> Calls { get; } = [];
    public double CpuPercent { get; set; } = 25;
    public long MemoryUsed { get; set; } = 4L << 30;
    public long MemoryTotal { get; set; } = 16L << 30;
    public int Samples { get; private set; }

    /// <summary>Kill also removes the process (and, for a tree, its descendants) from <see cref="Processes"/>.</summary>
    public bool RemoveKilled { get; set; } = true;

    public Task<ProcessSnapshot> SampleAsync(CancellationToken cancellationToken = default)
    {
        lock (Processes)
        {
            Samples++;
            return Task.FromResult(new ProcessSnapshot(DateTimeOffset.Now, CpuPercent, MemoryUsed, MemoryTotal, [.. Processes]));
        }
    }

    public Task<ProcessDetails?> GetDetailsAsync(ProcessSample process, CancellationToken cancellationToken = default) =>
        Task.FromResult<ProcessDetails?>(new ProcessDetails(
            process.Id, process.Name, $"/usr/bin/{process.Name}", $"{process.Name} --flag", process.ParentId, "init", process.User,
            process.StartTime, ProcessPriority.Normal, process.WorkingSet, process.WorkingSet / 2, process.Threads, TimeSpan.FromSeconds(3)));

    public Task<ProcessActionResult> KillAsync(ProcessSample process, bool entireTree, CancellationToken cancellationToken = default)
    {
        lock (Processes)
        {
            Calls.Add($"{(entireTree ? "kill-tree" : "kill")} {process.Id}");
            if (RemoveKilled)
            {
                var doomed = new HashSet<int> { process.Id };
                while (entireTree && Processes.FirstOrDefault(p => p.ParentId is { } parent && doomed.Contains(parent) && !doomed.Contains(p.Id)) is { } child)
                {
                    doomed.Add(child.Id);
                }

                Processes.RemoveAll(p => doomed.Contains(p.Id));
            }
        }

        return Task.FromResult(new ProcessActionResult(true, $"Ended {process.Name}."));
    }

    public Task<ProcessActionResult> SetPriorityAsync(ProcessSample process, ProcessPriority priority, CancellationToken cancellationToken = default)
    {
        Calls.Add($"priority {process.Id} {priority}");
        return Task.FromResult(new ProcessActionResult(true, $"{process.Name} priority changed."));
    }
}

/// <summary>In-memory network monitor with scripted interfaces, connections, ping replies and lookups.</summary>
public sealed class FakeNetworkMonitor : INetworkMonitor
{
    public List<NetworkInterfaceSample> Interfaces { get; } = [];
    public List<NetworkConnection> Connections { get; } = [];
    public Dictionary<string, IReadOnlyList<string>> Hosts { get; } = new(StringComparer.OrdinalIgnoreCase);
    public List<string> Calls { get; } = [];
    public bool CanFlushDns { get; set; }
    public int InterfaceSamples { get; private set; }

    public Task<IReadOnlyList<NetworkInterfaceSample>> SampleInterfacesAsync(CancellationToken cancellationToken = default)
    {
        lock (Interfaces)
        {
            InterfaceSamples++;
            return Task.FromResult<IReadOnlyList<NetworkInterfaceSample>>([.. Interfaces]);
        }
    }

    public Task<IReadOnlyList<NetworkConnection>> GetConnectionsAsync(CancellationToken cancellationToken = default)
    {
        lock (Connections)
        {
            return Task.FromResult<IReadOnlyList<NetworkConnection>>([.. Connections]);
        }
    }

    public Task<PingResult> PingAsync(string host, TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        lock (Calls)
        {
            Calls.Add("ping " + host);
        }

        return Task.FromResult(Hosts.TryGetValue(host, out var addresses)
            ? new PingResult(true, "Success", addresses[0], 12, 64)
            : new PingResult(false, "TimedOut", null, 0, null));
    }

    public Task<IReadOnlyList<string>> LookupAsync(string host, CancellationToken cancellationToken = default)
    {
        lock (Calls)
        {
            Calls.Add("lookup " + host);
        }

        return Hosts.TryGetValue(host, out var addresses)
            ? Task.FromResult(addresses)
            : Task.FromException<IReadOnlyList<string>>(new InvalidOperationException($"No such host is known: {host}"));
    }

    public Task<NetworkToolResult> FlushDnsAsync(CancellationToken cancellationToken = default)
    {
        lock (Calls)
        {
            Calls.Add("flushdns");
        }

        return Task.FromResult(new NetworkToolResult(true, "Successfully flushed the DNS Resolver Cache."));
    }
}

/// <summary>In-memory disk monitor: volumes, I/O samples and a canned (or real) analyzer result.</summary>
public sealed class FakeDiskMonitor : IDiskMonitor
{
    public List<DiskVolume> Volumes { get; } = [];
    public List<DiskIoSample> Io { get; } = [];
    public List<string> Calls { get; } = [];
    public bool SupportsIoRates { get; set; } = true;
    public bool SupportsSystemTools { get; set; }

    /// <summary>Builds the analyzer result for a path (default: a small fixed tree rooted at the path).</summary>
    public Func<string, CancellationToken, Task<DiskUsageNode>>? Analyzer { get; set; }

    public Task<IReadOnlyList<DiskVolume>> GetVolumesAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<DiskVolume>>([.. Volumes]);

    public Task<IReadOnlyList<DiskIoSample>> SampleIoAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<DiskIoSample>>([.. Io]);

    public Task<DiskUsageNode> AnalyzeAsync(string path, IProgress<DiskScanProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        lock (Calls)
        {
            Calls.Add("analyze " + path);
        }

        progress?.Report(new DiskScanProgress(1, 0, 0, path));
        return Analyzer?.Invoke(path, cancellationToken) ?? Task.FromResult(SampleTree(path));
    }

    public Task<DiskToolResult> OpenDiskCleanupAsync(string? volume, CancellationToken cancellationToken = default)
    {
        Calls.Add("cleanup " + volume);
        return Task.FromResult(new DiskToolResult(true, "Opened Disk Cleanup."));
    }

    public Task<DiskToolResult> OpenDiskManagementAsync(CancellationToken cancellationToken = default)
    {
        Calls.Add("diskmgmt");
        return Task.FromResult(new DiskToolResult(true, "Opened Disk Management."));
    }

    /// <summary>path/ → big/ (6000: movie.mkv 5000, nested/ 1000: a.bin), notes.txt 100.</summary>
    public static DiskUsageNode SampleTree(string path)
    {
        var root = new DiskUsageNode(path, isDirectory: true, path: path) { Size = 6100, FileCount = 3, DirectoryCount = 3 };
        var big = new DiskUsageNode("big", isDirectory: true, root) { Size = 6000, FileCount = 2, DirectoryCount = 2 };
        var nested = new DiskUsageNode("nested", isDirectory: true, big) { Size = 1000, FileCount = 1, DirectoryCount = 1 };
        nested.AddChild(new DiskUsageNode("a.bin", isDirectory: false, nested) { Size = 1000, FileCount = 1 });
        big.AddChild(new DiskUsageNode("movie.mkv", isDirectory: false, big) { Size = 5000, FileCount = 1 });
        big.AddChild(nested);
        root.AddChild(big);
        root.AddChild(new DiskUsageNode("notes.txt", isDirectory: false, root) { Size = 100, FileCount = 1 });
        return root;
    }
}
