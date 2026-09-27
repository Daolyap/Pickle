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

/// <summary>A diskpart-style action on a row: its menu label and the wizard it opens, pre-filled.</summary>
internal sealed record DiskAction(string Label, string WizardId, string Command);

/// <summary>Builds the Partitions tab's rows and the Storage-module commands (as wizard input) for each row.</summary>
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

    /// <summary>What can be done with a row, most common first.</summary>
    public static List<DiskAction> Actions(LayoutRow row)
    {
        var d = row.Disk;
        var disk = N(d.Number);
        var actions = new List<DiskAction>();
        if (row.Free)
        {
            actions.Add(new("New partition in this space", "new-partition", $"New-Partition -DiskNumber {disk} -UseMaximumSize -AssignDriveLetter"));
            return actions;
        }

        if (row.Partition is { } p)
        {
            var select = p.DriveLetter is { } letter ? $"-DriveLetter {letter}" : $"-DiskNumber {disk} -PartitionNumber {N(p.PartitionNumber)}";
            if (p.DriveLetter is { } l)
            {
                actions.Add(new("Format…", "format-volume", $"Format-Volume -DriveLetter {l} -FileSystem NTFS"));
                actions.Add(new("Check for errors…", "repair-volume", $"Repair-Volume -DriveLetter {l} -Scan"));
                actions.Add(new("Optimize (retrim / defragment)…", "optimize-volume", $"Optimize-Volume -DriveLetter {l} -ReTrim"));
            }

            actions.Add(new("Extend to the maximum…", "resize-partition", $"Resize-Partition {select} -Size (Get-PartitionSupportedSize {select}).SizeMax"));
            actions.Add(new("Shrink or resize…", "resize-partition", $"Resize-Partition {select} -Size {Math.Max(1, p.Size >> 30).ToString(CultureInfo.InvariantCulture)}GB"));
            actions.Add(new(p.DriveLetter is null ? "Assign a drive letter…" : "Change the drive letter…", "set-partition", $"Set-Partition {select}"));
            if (!row.IsProtected)
            {
                actions.Add(new("Delete partition…", "remove-partition", $"Remove-Partition {select}"));
            }

            return actions;
        }

        if (d.IsRaw)
        {
            actions.Add(new("Initialize (GPT)…", "initialize-disk", $"Initialize-Disk -Number {disk} -PartitionStyle GPT"));
        }
        else if (d.UnallocatedBytes >= MinimumFreeBytes)
        {
            actions.Add(new("New partition…", "new-partition", $"New-Partition -DiskNumber {disk} -UseMaximumSize -AssignDriveLetter"));
        }

        actions.Add(d.IsOffline
            ? new("Bring online…", "set-disk", $"Set-Disk -Number {disk} -IsOffline $false")
            : new("Take offline…", "set-disk", $"Set-Disk -Number {disk} -IsOffline $true"));
        if (d.IsReadOnly)
        {
            actions.Add(new("Clear read-only…", "set-disk", $"Set-Disk -Number {disk} -IsReadOnly $false"));
        }

        if (!row.IsProtected && !d.IsRaw)
        {
            actions.Add(new("Wipe the disk (clean)…", "clear-disk", $"Clear-Disk -Number {disk} -RemoveData -RemoveOEM"));
        }

        return actions;
    }

    private static string N(int value) => value.ToString(CultureInfo.InvariantCulture);
}
