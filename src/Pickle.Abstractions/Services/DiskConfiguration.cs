using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Pickle.Abstractions.Services;

public enum DiskOperationKind
{
    /// <summary>Give a RAW disk a partition table. <see cref="DiskOperation.Option"/>: GPT or MBR.</summary>
    InitializeDisk,

    /// <summary>Create a partition in the free space of an initialized disk, optionally formatted and with a drive letter.</summary>
    NewVolume,

    /// <summary>Quick-format an existing partition (erases it).</summary>
    FormatVolume,

    /// <summary>Grow or shrink a partition to <see cref="DiskOperation.SizeBytes"/>.</summary>
    ResizePartition,

    /// <summary>Remove a partition (erases it).</summary>
    DeletePartition,

    /// <summary>Assign or change a partition's drive letter.</summary>
    SetDriveLetter,

    /// <summary>Change a volume's label.</summary>
    SetLabel,

    /// <summary>Repair-Volume. <see cref="DiskOperation.Option"/>: scan, spotfix or fix.</summary>
    CheckVolume,

    /// <summary>Optimize-Volume. <see cref="DiskOperation.Option"/>: retrim, defrag or analyze.</summary>
    OptimizeVolume,

    /// <summary><see cref="DiskOperation.Option"/>: online, offline, readonly or readwrite.</summary>
    SetDiskState,

    /// <summary>Erase everything on a disk, partition table included (Clear-Disk).</summary>
    CleanDisk,
}

/// <summary>
/// One disk change. A flat record on purpose: it crosses to the elevated helper as JSON, and
/// <see cref="DiskOperationRules"/> (shared by the panel and the helper) accepts only the fields each kind uses.
/// </summary>
public sealed record DiskOperation(DiskOperationKind Kind, int DiskNumber)
{
    public int? PartitionNumber { get; init; }

    /// <summary>New volume (null: all the free space) or the new partition size.</summary>
    public long? SizeBytes { get; init; }

    public string? FileSystem { get; init; }

    public string? Label { get; init; }

    public char? DriveLetter { get; init; }

    /// <summary>The partition style, check mode, optimize mode or disk state, depending on <see cref="Kind"/>.</summary>
    public string? Option { get; init; }
}

public sealed record DiskOperationResult(DiskOperation Operation, bool Success, string Message);

/// <summary>What Windows says a partition can be resized to (<c>Get-PartitionSupportedSize</c>).</summary>
public sealed record DiskResizeRange(long MinimumBytes, long MaximumBytes);

/// <summary>Changes disks, partitions and volumes. Reading needs no rights; changing goes through the elevation broker (one UAC prompt).</summary>
public interface IDiskConfigurationService
{
    bool IsSupported { get; }

    /// <summary>True when applying changes will ask for administrator approval (this Pickle is not elevated).</summary>
    bool NeedsElevation { get; }

    Task<IReadOnlyList<PhysicalDisk>> GetDisksAsync(CancellationToken cancellationToken = default);

    /// <summary>Null when Windows cannot say (the volume is in use in a way that blocks the query).</summary>
    Task<DiskResizeRange?> GetResizeRangeAsync(int diskNumber, int partitionNumber, CancellationToken cancellationToken = default);

    /// <summary>
    /// Runs the operations in order and stops at the first failure; the result has one entry per operation that ran.
    /// Throws <see cref="OperationCanceledException"/> when the administrator prompt is declined.
    /// </summary>
    Task<IReadOnlyList<DiskOperationResult>> ApplyAsync(IReadOnlyList<DiskOperation> operations, IProgress<string>? progress = null, CancellationToken cancellationToken = default);
}

/// <summary>JSON for the elevated helper: strict, with no unknown members.</summary>
public static class DiskOperationCodec
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        RespectRequiredConstructorParameters = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
        MaxDepth = 4,
    };

    public static string Serialize(IReadOnlyList<DiskOperation> operations) => JsonSerializer.Serialize(operations, Options);

    /// <summary>Throws <see cref="ArgumentException"/> for anything that is not an array of valid operations.</summary>
    public static IReadOnlyList<DiskOperation> Deserialize(string json)
    {
        try
        {
            var operations = JsonSerializer.Deserialize<List<DiskOperation>>(json, Options) ?? throw new ArgumentException("No disk operations were given.");
            if (operations.Count is 0 or > DiskOperationRules.MaxOperations)
            {
                throw new ArgumentException($"Give between 1 and {DiskOperationRules.MaxOperations} disk operations.");
            }

            foreach (var operation in operations)
            {
                DiskOperationRules.Validate(operation ?? throw new ArgumentException("A disk operation is empty."));
            }

            return operations;
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException or InvalidOperationException)
        {
            throw new ArgumentException("The disk operations are not valid: " + ex.Message, ex);
        }
    }
}

/// <summary>
/// The rules for disk changes, used by the Disk Configuration panel before it queues anything and again by the elevated
/// helper before it runs anything: shape (<see cref="Validate"/>), the state of the disks (<see cref="CheckAgainst"/>),
/// what the layout will look like afterwards (<see cref="Project"/>), and the equivalent PowerShell (<see cref="ToCommand"/>).
/// </summary>
public static class DiskOperationRules
{
    public const int MaxOperations = 32;
    public const long MinimumVolumeBytes = 8L << 20;

    /// <summary>Partition numbers from here up are planned volumes that do not exist yet.</summary>
    public const int PlannedPartitionBase = 10_000;

    public static readonly IReadOnlyList<string> FileSystems = ["NTFS", "exFAT", "FAT32", "ReFS"];
    public static readonly IReadOnlyList<string> PartitionStyles = ["GPT", "MBR"];
    public static readonly IReadOnlyList<string> CheckModes = ["scan", "spotfix", "fix"];
    public static readonly IReadOnlyList<string> OptimizeModes = ["retrim", "defrag", "analyze"];
    public static readonly IReadOnlyList<string> DiskStates = ["online", "offline", "readonly", "readwrite"];

    private const string ForbiddenLabelCharacters = "\"\\/:*?<>|;,=+[]";
    private const int MaxDiskNumber = 4095;

    public static bool IsPlanned(DiskPartition partition) => partition.PartitionNumber >= PlannedPartitionBase;

    public static int MaxLabelLength(string? fileSystem) => fileSystem?.ToUpperInvariant() switch
    {
        "FAT32" => 11,
        "EXFAT" => 15,
        _ => 32,
    };

    public static bool IsDestructive(DiskOperation operation) =>
        operation.Kind is DiskOperationKind.FormatVolume or DiskOperationKind.DeletePartition or DiskOperationKind.CleanDisk;

    /// <summary>Throws <see cref="ArgumentException"/> unless the operation uses exactly the fields its kind allows, with valid values.</summary>
    public static void Validate(DiskOperation operation)
    {
        ArgumentNullException.ThrowIfNull(operation);
        if (!Enum.IsDefined(operation.Kind))
        {
            throw new ArgumentException($"Disk operation {(int)operation.Kind} is not allowed.");
        }

        if (operation.DiskNumber is < 0 or > MaxDiskNumber)
        {
            throw new ArgumentException($"'{operation.DiskNumber}' is not a disk number.");
        }

        var kind = operation.Kind;
        var needsPartition = kind is not (DiskOperationKind.InitializeDisk or DiskOperationKind.NewVolume or DiskOperationKind.SetDiskState or DiskOperationKind.CleanDisk);
        if (needsPartition != operation.PartitionNumber.HasValue || operation.PartitionNumber is <= 0 or >= PlannedPartitionBase)
        {
            throw new ArgumentException($"{kind} {(needsPartition ? "needs" : "does not take")} a partition number.");
        }

        Forbid(operation, kind is DiskOperationKind.NewVolume or DiskOperationKind.ResizePartition, operation.SizeBytes is not null, "a size");
        Forbid(operation, kind is DiskOperationKind.NewVolume or DiskOperationKind.FormatVolume, operation.FileSystem is not null, "a file system");
        Forbid(operation, kind is DiskOperationKind.NewVolume or DiskOperationKind.FormatVolume or DiskOperationKind.SetLabel, operation.Label is not null, "a label");
        Forbid(operation, kind is DiskOperationKind.NewVolume or DiskOperationKind.SetDriveLetter, operation.DriveLetter is not null, "a drive letter");
        Forbid(operation, kind is DiskOperationKind.InitializeDisk or DiskOperationKind.CheckVolume or DiskOperationKind.OptimizeVolume or DiskOperationKind.SetDiskState, operation.Option is not null, "an option");

        if (operation.SizeBytes is { } size && (size < MinimumVolumeBytes || size > (1L << 62)))
        {
            throw new ArgumentException($"A size must be between {FormatBytes(MinimumVolumeBytes)} and 4 EB.");
        }

        if (operation.FileSystem is { } fileSystem && !FileSystems.Contains(fileSystem, StringComparer.OrdinalIgnoreCase))
        {
            throw new ArgumentException($"'{fileSystem}' is not a file system ({string.Join(", ", FileSystems)}).");
        }

        if (kind == DiskOperationKind.FormatVolume && operation.FileSystem is null)
        {
            throw new ArgumentException("FormatVolume needs a file system.");
        }

        if (kind == DiskOperationKind.ResizePartition && operation.SizeBytes is null)
        {
            throw new ArgumentException("ResizePartition needs a size.");
        }

        if (kind == DiskOperationKind.SetLabel && operation.Label is null)
        {
            throw new ArgumentException("SetLabel needs a label (empty clears it).");
        }

        if (kind == DiskOperationKind.SetDriveLetter && operation.DriveLetter is null)
        {
            throw new ArgumentException("SetDriveLetter needs a drive letter.");
        }

        if (operation.Label is { } label)
        {
            ValidateLabel(label, operation.FileSystem ?? (kind == DiskOperationKind.SetLabel ? null : "NTFS"));
            if (kind == DiskOperationKind.NewVolume && operation.FileSystem is null && label.Length > 0)
            {
                throw new ArgumentException("A label needs a file system: an unformatted volume has none.");
            }
        }

        if (operation.DriveLetter is { } letter && !IsAssignableLetter(letter))
        {
            throw new ArgumentException($"'{letter}' is not a drive letter you can assign (C to Z).");
        }

        var options = kind switch
        {
            DiskOperationKind.InitializeDisk => PartitionStyles,
            DiskOperationKind.CheckVolume => CheckModes,
            DiskOperationKind.OptimizeVolume => OptimizeModes,
            DiskOperationKind.SetDiskState => DiskStates,
            _ => null,
        };
        if (options is not null && (operation.Option is null || !options.Contains(operation.Option, StringComparer.OrdinalIgnoreCase)))
        {
            throw new ArgumentException($"{kind} needs one of: {string.Join(", ", options)}.");
        }
    }

    public static bool IsAssignableLetter(char letter) => char.ToUpperInvariant(letter) is >= 'C' and <= 'Z' && char.IsAsciiLetter(letter);

    public static void ValidateLabel(string label, string? fileSystem)
    {
        var max = MaxLabelLength(fileSystem);
        if (label.Length > max)
        {
            throw new ArgumentException($"A {fileSystem ?? "volume"} label has at most {max} characters.");
        }

        if (label.Any(c => char.IsControl(c) || ForbiddenLabelCharacters.Contains(c, StringComparison.Ordinal)))
        {
            throw new ArgumentException($"A label cannot contain control characters or any of {ForbiddenLabelCharacters}");
        }
    }

    /// <summary>
    /// Throws <see cref="ArgumentException"/> with the reason when <paramref name="operation"/> is not safe to run against
    /// <paramref name="disks"/>: missing disks and partitions, boot and system protection, wrong states, no room, letters in use.
    /// </summary>
    public static void CheckAgainst(DiskOperation operation, IReadOnlyList<PhysicalDisk> disks)
    {
        Validate(operation);
        var disk = disks.FirstOrDefault(d => d.Number == operation.DiskNumber)
            ?? throw new ArgumentException($"Disk {operation.DiskNumber} does not exist.");
        var name = $"disk {disk.Number}";
        var kind = operation.Kind;

        if (kind == DiskOperationKind.SetDiskState)
        {
            CheckDiskState(operation, disk, name);
            return;
        }

        if (disk.IsOffline)
        {
            throw new ArgumentException($"Bring {name} online first.");
        }

        if (kind == DiskOperationKind.InitializeDisk)
        {
            Require(disk.IsRaw, $"{name} is already initialized ({disk.PartitionStyle}).");
            Require(!disk.IsReadOnly, $"{name} is read-only.");
            return;
        }

        if (kind == DiskOperationKind.CleanDisk)
        {
            Require(!disk.IsBoot && !disk.IsSystem, $"{name} holds Windows or the boot files; it is never wiped from here.");
            Require(!disk.IsReadOnly, $"{name} is read-only.");
            Require(!disk.IsRaw && (disk.Partitions.Count > 0 || disk.AllocatedSize > 0), $"{name} has nothing on it to wipe.");
            return;
        }

        Require(!disk.IsRaw, $"Initialize {name} first.");
        Require(!disk.IsReadOnly, $"{name} is read-only.");

        if (kind == DiskOperationKind.NewVolume)
        {
            var wanted = operation.SizeBytes ?? MinimumVolumeBytes;
            Require(disk.UnallocatedBytes >= wanted, $"{name} has {FormatBytes(disk.UnallocatedBytes)} unallocated; {(operation.SizeBytes is null ? "a volume needs at least " + FormatBytes(MinimumVolumeBytes) : FormatBytes(wanted) + " were asked for")}.");
            RequireLetterFree(operation.DriveLetter, disks, null);
            return;
        }

        var partition = disk.Partitions.FirstOrDefault(p => p.PartitionNumber == operation.PartitionNumber)
            ?? throw new ArgumentException($"{name} has no partition {operation.PartitionNumber}.");
        var label = $"partition {partition.PartitionNumber} of {name}";
        var data = partition.Type is "Basic" or "IFS";
        var protectedPartition = partition.IsBoot || partition.IsSystem;

        switch (kind)
        {
            case DiskOperationKind.FormatVolume or DiskOperationKind.DeletePartition or DiskOperationKind.SetDriveLetter:
                Require(!protectedPartition, $"{label} holds Windows or the boot files; it is never changed from here.");
                Require(data, $"{label} is a {partition.Type} partition, not a data partition.");
                if (kind == DiskOperationKind.SetDriveLetter)
                {
                    RequireLetterFree(operation.DriveLetter, disks, partition);
                }

                break;

            case DiskOperationKind.ResizePartition:
                Require(data, $"{label} is a {partition.Type} partition, not a data partition.");
                Require(operation.SizeBytes <= partition.Size + disk.UnallocatedBytes, $"{name} has {FormatBytes(disk.UnallocatedBytes)} unallocated; {label} can grow to {FormatBytes(partition.Size + disk.UnallocatedBytes)} at most.");
                break;

            default:
                Require(partition.FileSystem is not null, $"{label} has no file system (it is not formatted).");
                break;
        }
    }

    /// <summary>What the layout looks like once <paramref name="operation"/> has been applied; planned volumes get numbers from <see cref="PlannedPartitionBase"/>.</summary>
    public static IReadOnlyList<PhysicalDisk> Project(DiskOperation operation, IReadOnlyList<PhysicalDisk> disks)
    {
        CheckAgainst(operation, disks);
        return [.. disks.Select(d => d.Number == operation.DiskNumber ? ProjectDisk(operation, d, disks) : d)];
    }

    /// <summary>The first letter from D that no partition uses, or null.</summary>
    public static char? NextFreeLetter(IReadOnlyList<PhysicalDisk> disks)
    {
        var used = disks.SelectMany(d => d.Partitions).Where(p => p.DriveLetter is not null).Select(p => char.ToUpperInvariant(p.DriveLetter!.Value)).ToHashSet();
        for (var letter = 'D'; letter <= 'Z'; letter++)
        {
            if (!used.Contains(letter))
            {
                return letter;
            }
        }

        return null;
    }

    public static string Normalize(string? option) => option?.ToLowerInvariant() ?? string.Empty;

    /// <summary>"Initialize disk 1 as GPT", "Format partition 2 of disk 1 as NTFS (erases it)": one line for the review list.</summary>
    public static string Describe(DiskOperation operation)
    {
        var disk = $"disk {operation.DiskNumber}";
        var partition = $"partition {operation.PartitionNumber} of {disk}";
        return operation.Kind switch
        {
            DiskOperationKind.InitializeDisk => $"Initialize {disk} as {operation.Option!.ToUpperInvariant()}",
            DiskOperationKind.NewVolume => $"Create {(operation.SizeBytes is { } size ? FormatBytes(size) + " volume" : "a volume using all the free space")} on {disk}"
                + (operation.FileSystem is null ? ", unformatted" : $", {operation.FileSystem}")
                + (string.IsNullOrEmpty(operation.Label) ? string.Empty : $" \"{operation.Label}\"")
                + (operation.DriveLetter is { } letter ? $", drive {char.ToUpperInvariant(letter)}:" : ", next free drive letter"),
            DiskOperationKind.FormatVolume => $"Format {partition} as {operation.FileSystem} (erases it)" + (string.IsNullOrEmpty(operation.Label) ? string.Empty : $", label \"{operation.Label}\""),
            DiskOperationKind.ResizePartition => $"Resize {partition} to {FormatBytes(operation.SizeBytes!.Value)}",
            DiskOperationKind.DeletePartition => $"Delete {partition} (erases it)",
            DiskOperationKind.SetDriveLetter => $"Give {partition} the letter {char.ToUpperInvariant(operation.DriveLetter!.Value)}:",
            DiskOperationKind.SetLabel => string.IsNullOrEmpty(operation.Label) ? $"Clear the label of {partition}" : $"Label {partition} \"{operation.Label}\"",
            DiskOperationKind.CheckVolume => operation.Option!.ToLowerInvariant() switch
            {
                "scan" => $"Scan {partition} for errors (online, changes nothing)",
                "spotfix" => $"Spot-fix {partition} (quick offline repair)",
                _ => $"Check and repair {partition} (may take the volume offline)",
            },
            DiskOperationKind.OptimizeVolume => operation.Option!.ToLowerInvariant() switch
            {
                "retrim" => $"Retrim {partition} (SSD)",
                "defrag" => $"Defragment {partition}",
                _ => $"Analyze fragmentation of {partition}",
            },
            DiskOperationKind.SetDiskState => operation.Option!.ToLowerInvariant() switch
            {
                "online" => $"Bring {disk} online",
                "offline" => $"Take {disk} offline",
                "readonly" => $"Make {disk} read-only",
                _ => $"Make {disk} writable",
            },
            DiskOperationKind.CleanDisk => $"Wipe {disk}: every partition and all data (erases it)",
            _ => operation.Kind.ToString(),
        };
    }

    /// <summary>The Storage-module PowerShell that does the same, for the review list and for running it yourself in an administrator shell.</summary>
    public static string ToCommand(DiskOperation operation)
    {
        var disk = operation.DiskNumber.ToString(CultureInfo.InvariantCulture);
        var select = $"-DiskNumber {disk} -PartitionNumber {operation.PartitionNumber?.ToString(CultureInfo.InvariantCulture)}";
        string Partition() => $"Get-Partition {select}";
        string Volume() => $"{Partition()} | Get-Volume";
        string Quote(string value) => PowerShellQuote.Single(value);
        return operation.Kind switch
        {
            DiskOperationKind.InitializeDisk => $"Initialize-Disk -Number {disk} -PartitionStyle {operation.Option!.ToUpperInvariant()} -Confirm:$false",
            DiskOperationKind.NewVolume => $"New-Partition -DiskNumber {disk} "
                + (operation.SizeBytes is { } s ? $"-Size {SizeLiteral(s)}" : "-UseMaximumSize")
                + (operation.DriveLetter is { } l ? $" -DriveLetter {char.ToUpperInvariant(l)}" : " -AssignDriveLetter")
                + (operation.FileSystem is { } fs ? $" | Format-Volume -FileSystem {fs} -Confirm:$false" + (string.IsNullOrEmpty(operation.Label) ? string.Empty : $" -NewFileSystemLabel {Quote(operation.Label)}") : string.Empty),
            DiskOperationKind.FormatVolume => $"{Partition()} | Format-Volume -FileSystem {operation.FileSystem} -Confirm:$false" + (string.IsNullOrEmpty(operation.Label) ? string.Empty : $" -NewFileSystemLabel {Quote(operation.Label)}"),
            DiskOperationKind.ResizePartition => $"Resize-Partition {select} -Size {SizeLiteral(operation.SizeBytes!.Value)}",
            DiskOperationKind.DeletePartition => $"Remove-Partition {select} -Confirm:$false",
            DiskOperationKind.SetDriveLetter => $"Set-Partition {select} -NewDriveLetter {char.ToUpperInvariant(operation.DriveLetter!.Value)}",
            DiskOperationKind.SetLabel => $"{Volume()} | Set-Volume -NewFileSystemLabel {Quote(operation.Label ?? string.Empty)}",
            DiskOperationKind.CheckVolume => $"{Volume()} | Repair-Volume " + operation.Option!.ToLowerInvariant() switch { "scan" => "-Scan", "spotfix" => "-SpotFix", _ => "-OfflineScanAndFix" },
            DiskOperationKind.OptimizeVolume => $"{Volume()} | Optimize-Volume " + operation.Option!.ToLowerInvariant() switch { "retrim" => "-ReTrim", "defrag" => "-Defrag", _ => "-Analyze" },
            DiskOperationKind.SetDiskState => operation.Option!.ToLowerInvariant() switch
            {
                "online" => $"Set-Disk -Number {disk} -IsOffline $false",
                "offline" => $"Set-Disk -Number {disk} -IsOffline $true",
                "readonly" => $"Set-Disk -Number {disk} -IsReadOnly $true",
                _ => $"Set-Disk -Number {disk} -IsReadOnly $false",
            },
            DiskOperationKind.CleanDisk => $"Clear-Disk -Number {disk} -RemoveData -RemoveOEM -Confirm:$false",
            _ => throw new ArgumentException($"{operation.Kind} has no command."),
        };
    }

    /// <summary><c>200GB</c>, <c>1.5 TB</c>, <c>512MB</c> or plain bytes; the units are binary (as in Windows).</summary>
    public static bool TryParseSize(string? text, out long bytes)
    {
        bytes = 0;
        var value = text?.Trim().Replace(" ", string.Empty, StringComparison.Ordinal).ToUpperInvariant();
        if (string.IsNullOrEmpty(value))
        {
            return false;
        }

        var multiplier = 1L;
        foreach (var (suffix, factor) in new[] { ("TB", 1L << 40), ("GB", 1L << 30), ("MB", 1L << 20), ("KB", 1L << 10), ("T", 1L << 40), ("G", 1L << 30), ("M", 1L << 20), ("K", 1L << 10), ("B", 1L) })
        {
            if (value.EndsWith(suffix, StringComparison.Ordinal))
            {
                multiplier = factor;
                value = value[..^suffix.Length];
                break;
            }
        }

        if (!double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var number) || number <= 0 || double.IsInfinity(number))
        {
            return false;
        }

        var result = number * multiplier;
        if (result > (1L << 62))
        {
            return false;
        }

        bytes = (long)result;
        return true;
    }

    /// <summary>"1.5 TB", "200 GB", "512 MB" in binary units.</summary>
    public static string FormatBytes(long bytes) => bytes switch
    {
        >= 1L << 40 => (bytes / (double)(1L << 40)).ToString("0.##", CultureInfo.InvariantCulture) + " TB",
        >= 1L << 30 => (bytes / (double)(1L << 30)).ToString("0.##", CultureInfo.InvariantCulture) + " GB",
        >= 1L << 20 => (bytes / (double)(1L << 20)).ToString("0.##", CultureInfo.InvariantCulture) + " MB",
        >= 1L << 10 => (bytes / (double)(1L << 10)).ToString("0.##", CultureInfo.InvariantCulture) + " KB",
        _ => bytes.ToString(CultureInfo.InvariantCulture) + " B",
    };

    private static string SizeLiteral(long bytes) => bytes switch
    {
        _ when bytes % (1L << 30) == 0 => (bytes >> 30).ToString(CultureInfo.InvariantCulture) + "GB",
        _ when bytes % (1L << 20) == 0 => (bytes >> 20).ToString(CultureInfo.InvariantCulture) + "MB",
        _ => bytes.ToString(CultureInfo.InvariantCulture),
    };

    private static void Forbid(DiskOperation operation, bool allowed, bool present, string what)
    {
        if (present && !allowed)
        {
            throw new ArgumentException($"{operation.Kind} does not take {what}.");
        }
    }

    private static void Require(bool condition, string message)
    {
        if (!condition)
        {
            throw new ArgumentException(message);
        }
    }

    private static void CheckDiskState(DiskOperation operation, PhysicalDisk disk, string name)
    {
        var state = Normalize(operation.Option);
        switch (state)
        {
            case "offline" or "readonly":
                Require(!disk.IsBoot && !disk.IsSystem, $"{name} holds Windows or the boot files; it is never {(state == "offline" ? "taken offline" : "made read-only")} from here.");
                break;
        }

        Require(
            state switch { "online" => disk.IsOffline, "offline" => !disk.IsOffline, "readonly" => !disk.IsReadOnly, _ => disk.IsReadOnly },
            state switch { "online" => $"{name} is already online.", "offline" => $"{name} is already offline.", "readonly" => $"{name} is already read-only.", _ => $"{name} is already writable." });
    }

    private static void RequireLetterFree(char? letter, IReadOnlyList<PhysicalDisk> disks, DiskPartition? except)
    {
        if (letter is not { } wanted)
        {
            return;
        }

        var holder = disks.SelectMany(d => d.Partitions).FirstOrDefault(p => p.DriveLetter is { } l && char.ToUpperInvariant(l) == char.ToUpperInvariant(wanted) && !(except is not null && p.DiskNumber == except.DiskNumber && p.PartitionNumber == except.PartitionNumber));
        Require(holder is null, $"{char.ToUpperInvariant(wanted)}: is already used by partition {holder?.PartitionNumber} of disk {holder?.DiskNumber}.");
    }

    private static PhysicalDisk ProjectDisk(DiskOperation operation, PhysicalDisk disk, IReadOnlyList<PhysicalDisk> all)
    {
        DiskPartition Replace(DiskPartition p, Func<DiskPartition, DiskPartition> change) => p.PartitionNumber == operation.PartitionNumber ? change(p) : p;
        switch (operation.Kind)
        {
            case DiskOperationKind.InitializeDisk:
                return disk with { PartitionStyle = operation.Option!.ToUpperInvariant() };

            case DiskOperationKind.NewVolume:
                var size = operation.SizeBytes ?? disk.UnallocatedBytes;
                var number = Math.Max(PlannedPartitionBase, disk.Partitions.Select(p => p.PartitionNumber).DefaultIfEmpty(0).Max() + 1);
                var letter = operation.DriveLetter is { } given ? char.ToUpperInvariant(given) : NextFreeLetter(all);
                var created = new DiskPartition(disk.Number, number, letter, size, "Basic", false, false, operation.FileSystem is { } fs ? Canonical(fs) : null, string.IsNullOrEmpty(operation.Label) ? null : operation.Label, operation.FileSystem is null ? null : size);
                return disk with { Partitions = [.. disk.Partitions, created], AllocatedSize = disk.AllocatedSize + size };

            case DiskOperationKind.FormatVolume:
                return disk with { Partitions = [.. disk.Partitions.Select(p => Replace(p, x => x with { FileSystem = Canonical(operation.FileSystem!), Label = string.IsNullOrEmpty(operation.Label) ? null : operation.Label, FreeBytes = x.Size }))] };

            case DiskOperationKind.ResizePartition:
                var current = disk.Partitions.First(p => p.PartitionNumber == operation.PartitionNumber);
                var delta = operation.SizeBytes!.Value - current.Size;
                return disk with
                {
                    Partitions = [.. disk.Partitions.Select(p => Replace(p, x => x with { Size = operation.SizeBytes.Value, FreeBytes = x.FreeBytes is { } f ? Math.Max(0, f + delta) : null }))],
                    AllocatedSize = disk.AllocatedSize + delta,
                };

            case DiskOperationKind.DeletePartition:
                var removed = disk.Partitions.First(p => p.PartitionNumber == operation.PartitionNumber);
                return disk with { Partitions = [.. disk.Partitions.Where(p => p.PartitionNumber != operation.PartitionNumber)], AllocatedSize = Math.Max(0, disk.AllocatedSize - removed.Size) };

            case DiskOperationKind.SetDriveLetter:
                return disk with { Partitions = [.. disk.Partitions.Select(p => Replace(p, x => x with { DriveLetter = char.ToUpperInvariant(operation.DriveLetter!.Value) }))] };

            case DiskOperationKind.SetLabel:
                return disk with { Partitions = [.. disk.Partitions.Select(p => Replace(p, x => x with { Label = string.IsNullOrEmpty(operation.Label) ? null : operation.Label }))] };

            case DiskOperationKind.SetDiskState:
                return Normalize(operation.Option) switch
                {
                    "online" => disk with { IsOffline = false },
                    "offline" => disk with { IsOffline = true },
                    "readonly" => disk with { IsReadOnly = true },
                    _ => disk with { IsReadOnly = false },
                };

            case DiskOperationKind.CleanDisk:
                return disk with { PartitionStyle = "RAW", Partitions = [], AllocatedSize = 0 };

            default:
                return disk;
        }
    }

    private static string Canonical(string fileSystem) => FileSystems.First(f => f.Equals(fileSystem, StringComparison.OrdinalIgnoreCase));
}
