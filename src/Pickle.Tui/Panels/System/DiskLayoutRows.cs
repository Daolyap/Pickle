using System.Globalization;
using Pickle.Abstractions.Services;

namespace Pickle.Tui.Panels.SystemMonitoring;

/// <summary>A line of the Partitions tab: a disk, one of its partitions, or its unallocated space.</summary>
internal sealed record LayoutRow(int Order, PhysicalDisk Disk, DiskPartition? Partition, bool Free)
{
    public bool IsDisk => Partition is null && !Free;

    /// <summary>The boot or system partition, or a disk holding one: never offered for deletion or wiping.</summary>
    public bool IsProtected => Partition is { } p ? p.IsBoot || p.IsSystem : Disk.IsBoot || Disk.IsSystem;
}

/// <summary>Builds the rows of the Partitions tab and the Disk Configuration panel: a line per disk, partition and stretch of free space.</summary>
internal static class DiskLayoutRows
{
    private const long MinimumFreeBytes = 16L << 20;

    public static List<LayoutRow> Build(IReadOnlyList<PhysicalDisk> disks)
    {
        var rows = new List<LayoutRow>();
        foreach (var disk in disks.OrderBy(d => d.Number))
        {
            rows.Add(new LayoutRow(rows.Count, disk, null, false));
            foreach (var partition in disk.Partitions.OrderBy(p => p.PartitionNumber))
            {
                rows.Add(new LayoutRow(rows.Count, disk, partition, false));
            }

            if (!disk.IsRaw && disk.UnallocatedBytes >= MinimumFreeBytes)
            {
                rows.Add(new LayoutRow(rows.Count, disk, null, true));
            }
        }

        return rows;
    }

    public static string Name(LayoutRow row) => row switch
    {
        { Free: true } => "    unallocated",
        { Partition: { } p } when DiskOperationRules.IsPlanned(p) => $"    {(p.DriveLetter is { } pl ? pl + ":" : "  ")} new volume",
        { Partition: { } p } => $"    {(p.DriveLetter is { } l ? l + ":" : "  ")} partition {N(p.PartitionNumber)}",
        _ => $"Disk {N(row.Disk.Number)}  {row.Disk.Name}",
    };

    public static string Kind(LayoutRow row) => row switch
    {
        { Free: true } => "free space",
        { Partition: { } p } => string.Join(", ", new[] { p.Type, p.FileSystem, p.Label is { } l ? $"\"{l}\"" : null }.Where(s => !string.IsNullOrEmpty(s))),
        _ => $"{(row.Disk.IsRaw ? "RAW (not initialized)" : row.Disk.PartitionStyle)} · {row.Disk.BusType}",
    };

    public static long Size(LayoutRow row) => row switch
    {
        { Free: true } => row.Disk.UnallocatedBytes,
        { Partition: { } p } => p.Size,
        _ => row.Disk.Size,
    };

    public static string Status(LayoutRow row)
    {
        if (row.Partition is { } p)
        {
            var flags = new List<string>();
            if (DiskOperationRules.IsPlanned(p))
            {
                flags.Add("planned");
            }

            if (p.IsBoot)
            {
                flags.Add("boot");
            }

            if (p.IsSystem)
            {
                flags.Add("system");
            }

            if (p.FreeBytes is { } free)
            {
                flags.Add(SystemFormat.Bytes(free) + " free");
            }

            return string.Join(" · ", flags);
        }

        if (row.Free)
        {
            return string.Empty;
        }

        var d = row.Disk;
        return string.Join(" · ", new[] { d.IsOffline ? "offline" : "online", d.IsReadOnly ? "read-only" : null, d.IsBoot ? "boot" : null, d.Status }
            .Where(s => !string.IsNullOrEmpty(s)));
    }

    private static string N(int value) => value.ToString(CultureInfo.InvariantCulture);
}
