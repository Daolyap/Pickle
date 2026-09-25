namespace Pickle.Abstractions.Services;

public sealed record DiskVolume(string Name, string? Label, string? FileSystem, string Type, long TotalBytes, long FreeBytes)
{
    public long UsedBytes => Math.Max(0, TotalBytes - FreeBytes);

    public double UsedFraction => TotalBytes > 0 ? (double)UsedBytes / TotalBytes : 0;
}

/// <summary>Cumulative bytes read/written by a block device and the rates since the previous sample (bytes per second).</summary>
public sealed record DiskIoSample(string Device, long BytesRead, long BytesWritten, double ReadRate, double WriteRate);

public sealed record DiskScanProgress(long Directories, long Files, long Bytes, string CurrentPath);

public sealed record DiskToolResult(bool Success, string Message);

/// <summary>
/// A directory or file in a disk usage scan. Sizes and counts include the whole subtree. Directories keep their
/// largest files; the rest are summed into one <see cref="IsGroup"/> entry.
/// </summary>
public sealed class DiskUsageNode
{
    private readonly string? _path;
    private List<DiskUsageNode>? _children;

    public DiskUsageNode(string name, bool isDirectory, DiskUsageNode? parent = null, string? path = null)
    {
        if (parent is null && path is null)
        {
            throw new ArgumentException("A root node needs a path.", nameof(path));
        }

        Name = name;
        IsDirectory = isDirectory;
        Parent = parent;
        _path = path;
    }

    public string Name { get; }

    public bool IsDirectory { get; }

    /// <summary>Several smaller files shown as one entry; its <see cref="FullPath"/> is the containing directory.</summary>
    public bool IsGroup { get; init; }

    public DiskUsageNode? Parent { get; }

    public string FullPath => _path ?? (IsGroup ? Parent!.FullPath : Path.Join(Parent!.FullPath, Name));

    public long Size { get; set; }

    public long FileCount { get; set; }

    public long DirectoryCount { get; set; }

    /// <summary>Entries not scanned: inaccessible directories, links/junctions/reparse points and other file systems.</summary>
    public long Skipped { get; set; }

    /// <summary>Largest first once the scan has finished (a group of smaller files comes last).</summary>
    public IReadOnlyList<DiskUsageNode> Children => _children ?? (IReadOnlyList<DiskUsageNode>)[];

    public void AddChild(DiskUsageNode child) => (_children ??= []).Add(child);

    public void SortChildren() =>
        _children?.Sort(static (a, b) =>
            a.IsGroup != b.IsGroup ? a.IsGroup.CompareTo(b.IsGroup)
            : b.Size != a.Size ? b.Size.CompareTo(a.Size)
            : string.CompareOrdinal(a.Name, b.Name));
}

/// <summary>Volumes, block-device throughput and a du-like analyzer for the Disks panel (Alt+D). Implemented in Pickle.Core.</summary>
public interface IDiskMonitor
{
    Task<IReadOnlyList<DiskVolume>> GetVolumesAsync(CancellationToken cancellationToken = default);

    /// <summary>True where per-device I/O counters are available (Linux <c>/proc/diskstats</c>).</summary>
    bool SupportsIoRates { get; }

    /// <summary>Per-device throughput since the previous call (0 on the first).</summary>
    Task<IReadOnlyList<DiskIoSample>> SampleIoAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Scans <paramref name="path"/> without following symlinks, junctions or other reparse points, staying on one
    /// file system and skipping inaccessible directories.
    /// </summary>
    Task<DiskUsageNode> AnalyzeAsync(string path, IProgress<DiskScanProgress>? progress = null, CancellationToken cancellationToken = default);

    /// <summary>True on Windows: Disk Cleanup and Disk Management can be opened.</summary>
    bool SupportsSystemTools { get; }

    Task<DiskToolResult> OpenDiskCleanupAsync(string? volume, CancellationToken cancellationToken = default);

    Task<DiskToolResult> OpenDiskManagementAsync(CancellationToken cancellationToken = default);
}
