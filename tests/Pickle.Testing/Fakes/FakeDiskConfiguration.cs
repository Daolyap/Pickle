using Pickle.Abstractions.Services;

namespace Pickle.Testing.Fakes;

/// <summary>Three disks for disk tests: a system disk, a data disk with free space, and a blank (RAW, offline) one.</summary>
public static class SampleDisks
{
    public static PhysicalDisk System { get; } = new(
        0, "Samsung SSD 980", "GPT", 1000L << 30, 1000L << 30, "Online", false, false, true, true, "NVMe",
        [
            new DiskPartition(0, 1, null, 100L << 20, "System", false, true, "FAT32", null, 60L << 20),
            new DiskPartition(0, 2, null, 16L << 20, "Reserved", false, false, null, null, null),
            new DiskPartition(0, 3, 'C', 999L << 30, "Basic", true, false, "NTFS", "Windows", 400L << 30),
        ]);

    public static PhysicalDisk Data { get; } = new(
        1, "WD Elements", "GPT", 2000L << 30, 500L << 30, "Online", false, false, false, false, "USB",
        [new DiskPartition(1, 1, 'E', 500L << 30, "Basic", false, false, "exFAT", "Backup", 100L << 30)]);

    public static PhysicalDisk Blank { get; } = new(2, "Blank SSD", "RAW", 500L << 30, 0, "Offline", true, false, false, false, "SATA", []);

    public static IReadOnlyList<PhysicalDisk> All => [System, Data, Blank];
}

/// <summary>
/// A disk configuration service over an in-memory layout: <see cref="ApplyAsync"/> checks and projects each operation with
/// <see cref="DiskOperationRules"/> (as the real helper does against the live disks) and records what was applied.
/// </summary>
public sealed class FakeDiskConfigurationService(IEnumerable<PhysicalDisk>? disks = null) : IDiskConfigurationService
{
    public List<PhysicalDisk> Disks { get; } = [.. disks ?? SampleDisks.All];

    public bool Supported { get; set; } = true;

    public bool Elevation { get; set; } = true;

    public List<IReadOnlyList<DiskOperation>> Batches { get; } = [];

    /// <summary>Index within a batch that fails (the operations before it are applied).</summary>
    public int? FailAt { get; set; }

    /// <summary>Thrown by <see cref="ApplyAsync"/> (e.g. an <see cref="OperationCanceledException"/> for a declined UAC prompt).</summary>
    public Exception? ThrowOnApply { get; set; }

    public DiskResizeRange? Range { get; set; }

    public int Reads { get; private set; }

    public bool IsSupported => Supported;

    public bool NeedsElevation => Elevation;

    public Task<IReadOnlyList<PhysicalDisk>> GetDisksAsync(CancellationToken cancellationToken = default)
    {
        Reads++;
        return Task.FromResult<IReadOnlyList<PhysicalDisk>>([.. Disks]);
    }

    public Task<DiskResizeRange?> GetResizeRangeAsync(int diskNumber, int partitionNumber, CancellationToken cancellationToken = default) => Task.FromResult(Range);

    public Task<IReadOnlyList<DiskOperationResult>> ApplyAsync(IReadOnlyList<DiskOperation> operations, IProgress<string>? progress = null, CancellationToken cancellationToken = default)
    {
        if (ThrowOnApply is { } ex)
        {
            throw ex;
        }

        Batches.Add(operations);
        var results = new List<DiskOperationResult>();
        for (var i = 0; i < operations.Count; i++)
        {
            progress?.Report(DiskOperationRules.Describe(operations[i]));
            if (FailAt == i)
            {
                results.Add(new DiskOperationResult(operations[i], false, "The disk is in use."));
                break;
            }

            try
            {
                var next = DiskOperationRules.Project(operations[i], Disks);
                Disks.Clear();
                Disks.AddRange(next.Select(Settle));
                results.Add(new DiskOperationResult(operations[i], true, "Done."));
            }
            catch (ArgumentException e)
            {
                results.Add(new DiskOperationResult(operations[i], false, e.Message));
                break;
            }
        }

        return Task.FromResult<IReadOnlyList<DiskOperationResult>>(results);
    }

    // A created volume gets a real partition number once it exists.
    private static PhysicalDisk Settle(PhysicalDisk disk)
    {
        var next = disk.Partitions.Where(p => !DiskOperationRules.IsPlanned(p)).Select(p => p.PartitionNumber).DefaultIfEmpty(0).Max() + 1;
        return disk with
        {
            Partitions = [.. disk.Partitions.Select(p => DiskOperationRules.IsPlanned(p) ? p with { PartitionNumber = next++ } : p)],
        };
    }
}
