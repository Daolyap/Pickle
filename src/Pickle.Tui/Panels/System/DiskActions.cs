using Pickle.Abstractions.Services;

namespace Pickle.Tui.Panels.SystemMonitoring;

internal enum DiskInputKind
{
    Text,
    Choice,
}

/// <summary>One question an action asks before it becomes a <see cref="DiskOperation"/>.</summary>
internal sealed record DiskInput(string Id, string Label, DiskInputKind Kind, string Default = "", IReadOnlyList<string>? Choices = null);

/// <summary>An entry of the action menu of a row: what it asks, and how the answers become a disk operation.</summary>
internal sealed record DiskActionSpec(string Label, IReadOnlyList<DiskInput> Inputs, Func<IReadOnlyDictionary<string, string>, DiskOperation> Build)
{
    /// <summary>The Build function needs <c>_max</c>: the largest size Windows allows (the panel asks Get-PartitionSupportedSize).</summary>
    public bool NeedsRange { get; init; }
}

/// <summary>
/// What can be done with a disk, a partition or free space in the Disk Configuration panel. The conditions mirror
/// <see cref="DiskOperationRules"/> (which has the last word, in the panel and again in the elevated helper), so the menu
/// only offers things that can work, and never anything for Windows or the boot files.
/// </summary>
internal static class DiskActions
{
    public const string Unformatted = "(leave unformatted)";

    private static readonly string[] CheckChoices = ["scan  (online, changes nothing)", "spotfix  (quick repair, short offline)", "fix  (full repair, may dismount the volume)"];
    private static readonly string[] OptimizeChoices = ["retrim  (SSDs)", "defrag  (hard disks)", "analyze  (report only)"];

    public static IReadOnlyList<DiskActionSpec> For(LayoutRow row, IReadOnlyList<PhysicalDisk> disks)
    {
        var disk = row.Disk;
        var actions = new List<DiskActionSpec>();
        if (row.Free)
        {
            if (CanCreate(disk))
            {
                actions.Add(NewVolume(disk, disks));
            }

            return actions;
        }

        if (row.Partition is { } partition)
        {
            return DiskOperationRules.IsPlanned(partition) || disk.IsOffline || disk.IsReadOnly ? [] : ForPartition(disk, partition, disks);
        }

        if (disk.IsOffline)
        {
            actions.Add(State("Bring online", disk, "online"));
            return actions;
        }

        if (disk.IsRaw)
        {
            if (!disk.IsReadOnly)
            {
                actions.Add(new DiskActionSpec(
                    "Initialize…",
                    [new DiskInput("style", "Partition style", DiskInputKind.Choice, "GPT", ["GPT  (modern; disks over 2 TB, UEFI)", "MBR  (old BIOS machines)"])],
                    v => new DiskOperation(DiskOperationKind.InitializeDisk, disk.Number) { Option = First(v["style"]) }));
            }
        }
        else if (CanCreate(disk))
        {
            actions.Add(NewVolume(disk, disks));
        }

        if (!disk.IsBoot && !disk.IsSystem)
        {
            actions.Add(State("Take offline", disk, "offline"));
            actions.Add(State(disk.IsReadOnly ? "Make writable" : "Make read-only", disk, disk.IsReadOnly ? "readwrite" : "readonly"));
            if (!disk.IsRaw && !disk.IsReadOnly && (disk.Partitions.Count > 0 || disk.AllocatedSize > 0))
            {
                actions.Add(new DiskActionSpec("Wipe the disk…", [], _ => new DiskOperation(DiskOperationKind.CleanDisk, disk.Number)));
            }
        }

        return actions;
    }

    /// <summary>Why nothing is offered for a row (shown instead of an empty menu).</summary>
    public static string WhyNothing(LayoutRow row)
    {
        if (row.Partition is { } p)
        {
            if (DiskOperationRules.IsPlanned(p))
            {
                return "This volume is only planned. Apply the pending changes (F9) before changing it again, or undo it (F8).";
            }

            if (row.Disk.IsOffline)
            {
                return $"Disk {row.Disk.Number} is offline: bring it online first.";
            }

            if (row.Disk.IsReadOnly)
            {
                return $"Disk {row.Disk.Number} is read-only.";
            }
        }

        if (row.Free)
        {
            return row.Disk.IsOffline ? $"Disk {row.Disk.Number} is offline: bring it online first." : "This space cannot be used for a volume.";
        }

        return "Nothing can be done with this from here.";
    }

    private static bool CanCreate(PhysicalDisk disk) =>
        !disk.IsRaw && !disk.IsOffline && !disk.IsReadOnly && disk.UnallocatedBytes >= DiskOperationRules.MinimumVolumeBytes;

    private static DiskActionSpec NewVolume(PhysicalDisk disk, IReadOnlyList<PhysicalDisk> disks) =>
        new(
            "New volume…",
            [
                new DiskInput("size", $"Size (like 200GB, or max for all {DiskOperationRules.FormatBytes(disk.UnallocatedBytes)} free)", DiskInputKind.Text, "max"),
                new DiskInput("fs", "File system", DiskInputKind.Choice, "NTFS", [.. DiskOperationRules.FileSystems, Unformatted]),
                new DiskInput("label", "Label (optional)", DiskInputKind.Text),
                new DiskInput("letter", "Drive letter (auto, or C to Z)", DiskInputKind.Text, "auto"),
            ],
            v =>
            {
                var fileSystem = v["fs"] == Unformatted ? null : v["fs"];
                return new DiskOperation(DiskOperationKind.NewVolume, disk.Number)
                {
                    SizeBytes = IsMax(v["size"]) ? null : Size(v["size"]),
                    FileSystem = fileSystem,
                    Label = fileSystem is null || string.IsNullOrWhiteSpace(v["label"]) ? null : v["label"].Trim(),
                    DriveLetter = Letter(v["letter"]),
                };
            });

    private static IReadOnlyList<DiskActionSpec> ForPartition(PhysicalDisk disk, DiskPartition partition, IReadOnlyList<PhysicalDisk> disks)
    {
        var actions = new List<DiskActionSpec>();
        var number = partition.PartitionNumber;
        var data = partition.Type is "Basic" or "IFS";
        var protectedPartition = partition.IsBoot || partition.IsSystem;
        DiskOperation Op(DiskOperationKind kind) => new(kind, disk.Number) { PartitionNumber = number };

        if (data && !protectedPartition)
        {
            actions.Add(new DiskActionSpec(
                "Format…",
                [
                    new DiskInput("fs", "File system", DiskInputKind.Choice, partition.FileSystem is { } current && DiskOperationRules.FileSystems.Contains(current, StringComparer.OrdinalIgnoreCase) ? current : "NTFS", DiskOperationRules.FileSystems),
                    new DiskInput("label", "Label (optional)", DiskInputKind.Text, partition.Label ?? string.Empty),
                ],
                v => Op(DiskOperationKind.FormatVolume) with { FileSystem = v["fs"], Label = string.IsNullOrWhiteSpace(v["label"]) ? null : v["label"].Trim() }));
        }

        if (data)
        {
            actions.Add(new DiskActionSpec(
                "Resize…",
                [new DiskInput("size", "New size (like 200GB, or max)", DiskInputKind.Text, "max")],
                v => Op(DiskOperationKind.ResizePartition) with { SizeBytes = IsMax(v["size"]) ? Size(v["_max"]) : Size(v["size"]) })
            {
                NeedsRange = true,
            });
        }

        if (data && !protectedPartition)
        {
            actions.Add(new DiskActionSpec(
                partition.DriveLetter is null ? "Assign a drive letter…" : "Change the drive letter…",
                [new DiskInput("letter", "Drive letter (C to Z)", DiskInputKind.Text, partition.DriveLetter?.ToString() ?? DiskOperationRules.NextFreeLetter(disks)?.ToString() ?? string.Empty)],
                v => Op(DiskOperationKind.SetDriveLetter) with { DriveLetter = Letter(v["letter"]) ?? throw new ArgumentException("Give a drive letter.") }));
        }

        if (partition.FileSystem is not null)
        {
            actions.Add(new DiskActionSpec(
                "Label…",
                [new DiskInput("label", "Label (empty clears it)", DiskInputKind.Text, partition.Label ?? string.Empty)],
                v => Op(DiskOperationKind.SetLabel) with { Label = v["label"].Trim() }));
            actions.Add(new DiskActionSpec(
                "Check for errors…",
                [new DiskInput("mode", "Mode", DiskInputKind.Choice, CheckChoices[0], CheckChoices)],
                v => Op(DiskOperationKind.CheckVolume) with { Option = First(v["mode"]) }));
            actions.Add(new DiskActionSpec(
                "Optimize…",
                [new DiskInput("mode", "Mode", DiskInputKind.Choice, OptimizeChoices[0], OptimizeChoices)],
                v => Op(DiskOperationKind.OptimizeVolume) with { Option = First(v["mode"]) }));
        }

        if (data && !protectedPartition)
        {
            actions.Add(new DiskActionSpec("Delete…", [], _ => Op(DiskOperationKind.DeletePartition)));
        }

        return actions;
    }

    private static DiskActionSpec State(string label, PhysicalDisk disk, string state) =>
        new(label, [], _ => new DiskOperation(DiskOperationKind.SetDiskState, disk.Number) { Option = state });

    private static string First(string choice) => choice.Split(' ', 2)[0];

    private static bool IsMax(string text) => text.Trim().Equals("max", StringComparison.OrdinalIgnoreCase) || text.Trim().Length == 0;

    private static long Size(string text) =>
        DiskOperationRules.TryParseSize(text, out var bytes) ? bytes : throw new ArgumentException($"'{text}' is not a size. Use something like 200GB, 512MB or 1.5TB.");

    private static char? Letter(string text)
    {
        var value = text.Trim().TrimEnd(':');
        return value.Length == 0 || value.Equals("auto", StringComparison.OrdinalIgnoreCase)
            ? null
            : value.Length == 1 && char.IsAsciiLetter(value[0]) ? char.ToUpperInvariant(value[0]) : throw new ArgumentException($"'{text}' is not a drive letter.");
    }
}
