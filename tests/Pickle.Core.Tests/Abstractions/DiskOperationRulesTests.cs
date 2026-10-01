using Pickle.Abstractions.Services;
using Pickle.Testing.Fakes;
using static Pickle.Abstractions.Services.DiskOperationKind;

namespace Pickle.Core.Tests.Abstractions;

public class DiskOperationRulesTests
{
    private static readonly IReadOnlyList<PhysicalDisk> Disks = SampleDisks.All;

    private static DiskOperation Op(DiskOperationKind kind, int disk, int? partition = null) => new(kind, disk) { PartitionNumber = partition };

    private static string Why(DiskOperation operation, IReadOnlyList<PhysicalDisk>? disks = null) =>
        Assert.Throws<ArgumentException>(() => DiskOperationRules.CheckAgainst(operation, disks ?? Disks)).Message;

    // ───────────── shape ─────────────

    public static TheoryData<DiskOperation> ValidOperations() => new()
    {
        new DiskOperation(InitializeDisk, 2) { Option = "GPT" },
        new DiskOperation(InitializeDisk, 2) { Option = "mbr" },
        new DiskOperation(NewVolume, 1),
        new DiskOperation(NewVolume, 1) { SizeBytes = 100L << 30, FileSystem = "NTFS", Label = "Data", DriveLetter = 'f' },
        new DiskOperation(NewVolume, 1) { SizeBytes = 100L << 30 },
        new DiskOperation(FormatVolume, 1) { PartitionNumber = 1, FileSystem = "exFAT", Label = "Backup" },
        new DiskOperation(ResizePartition, 1) { PartitionNumber = 1, SizeBytes = 600L << 30 },
        new DiskOperation(DeletePartition, 1) { PartitionNumber = 1 },
        new DiskOperation(SetDriveLetter, 1) { PartitionNumber = 1, DriveLetter = 'Z' },
        new DiskOperation(SetLabel, 1) { PartitionNumber = 1, Label = string.Empty },
        new DiskOperation(CheckVolume, 1) { PartitionNumber = 1, Option = "scan" },
        new DiskOperation(OptimizeVolume, 1) { PartitionNumber = 1, Option = "retrim" },
        new DiskOperation(SetDiskState, 2) { Option = "online" },
        new DiskOperation(CleanDisk, 1),
    };

    [Theory]
    [MemberData(nameof(ValidOperations))]
    public void WellFormedOperationsPass(DiskOperation operation) => DiskOperationRules.Validate(operation);

    public static TheoryData<DiskOperation, string> BadOperations() => new()
    {
        { new DiskOperation((DiskOperationKind)99, 0), "not allowed" },
        { new DiskOperation(CleanDisk, -1), "disk number" },
        { new DiskOperation(CleanDisk, 5000), "disk number" },
        { new DiskOperation(InitializeDisk, 2), "one of" },
        { new DiskOperation(InitializeDisk, 2) { Option = "APM" }, "one of" },
        { new DiskOperation(InitializeDisk, 2) { Option = "GPT", PartitionNumber = 1 }, "partition number" },
        { new DiskOperation(CleanDisk, 1) { Option = "x" }, "option" },
        { new DiskOperation(CleanDisk, 1) { SizeBytes = 10 << 20 }, "size" },
        { new DiskOperation(DeletePartition, 1), "partition number" },
        { new DiskOperation(DeletePartition, 1) { PartitionNumber = 0 }, "partition number" },
        { new DiskOperation(DeletePartition, 1) { PartitionNumber = DiskOperationRules.PlannedPartitionBase }, "partition number" },
        { new DiskOperation(DeletePartition, 1) { PartitionNumber = 1, Label = "x" }, "label" },
        { new DiskOperation(FormatVolume, 1) { PartitionNumber = 1 }, "file system" },
        { new DiskOperation(FormatVolume, 1) { PartitionNumber = 1, FileSystem = "ext4" }, "not a file system" },
        { new DiskOperation(FormatVolume, 1) { PartitionNumber = 1, FileSystem = "NTFS; rm" }, "not a file system" },
        { new DiskOperation(FormatVolume, 1) { PartitionNumber = 1, FileSystem = "FAT32", Label = "TWELVE CHARS" }, "at most 11" },
        { new DiskOperation(FormatVolume, 1) { PartitionNumber = 1, FileSystem = "NTFS", Label = "a\"b" }, "cannot contain" },
        { new DiskOperation(FormatVolume, 1) { PartitionNumber = 1, FileSystem = "NTFS", Label = "a\nb" }, "cannot contain" },
        { new DiskOperation(FormatVolume, 1) { PartitionNumber = 1, FileSystem = "NTFS", DriveLetter = 'E' }, "drive letter" },
        { new DiskOperation(ResizePartition, 1) { PartitionNumber = 1 }, "needs a size" },
        { new DiskOperation(ResizePartition, 1) { PartitionNumber = 1, SizeBytes = 1024 }, "between" },
        { new DiskOperation(NewVolume, 1) { SizeBytes = -5 }, "between" },
        { new DiskOperation(NewVolume, 1) { Label = "Data" }, "needs a file system" },
        { new DiskOperation(NewVolume, 1) { DriveLetter = 'A' }, "C to Z" },
        { new DiskOperation(NewVolume, 1) { DriveLetter = '1' }, "C to Z" },
        { new DiskOperation(SetDriveLetter, 1) { PartitionNumber = 1 }, "needs a drive letter" },
        { new DiskOperation(SetLabel, 1) { PartitionNumber = 1 }, "needs a label" },
        { new DiskOperation(CheckVolume, 1) { PartitionNumber = 1, Option = "format" }, "one of" },
        { new DiskOperation(OptimizeVolume, 1) { PartitionNumber = 1 }, "one of" },
        { new DiskOperation(SetDiskState, 1) { Option = "delete" }, "one of" },
    };

    [Theory]
    [MemberData(nameof(BadOperations))]
    public void MalformedOperationsAreRejected(DiskOperation operation, string reason) =>
        Assert.Contains(reason, Assert.Throws<ArgumentException>(() => DiskOperationRules.Validate(operation)).Message, StringComparison.OrdinalIgnoreCase);

    // ───────────── the JSON the helper receives ─────────────

    [Fact]
    public void OperationsRoundTripThroughTheCodec()
    {
        var operations = new[]
        {
            new DiskOperation(InitializeDisk, 2) { Option = "GPT" },
            new DiskOperation(NewVolume, 2) { SizeBytes = 100L << 30, FileSystem = "NTFS", Label = "Data é", DriveLetter = 'F' },
        };

        var json = DiskOperationCodec.Serialize(operations);
        var back = DiskOperationCodec.Deserialize(json);

        Assert.Contains("\"kind\":\"initializeDisk\"", json, StringComparison.Ordinal);
        Assert.Equal(operations, back);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("{}")]
    [InlineData("[]")]
    [InlineData("[{\"diskNumber\":1}]")]
    [InlineData("[{\"kind\":\"cleanDisk\"}]")]
    [InlineData("[{\"kind\":\"cleanDisk\",\"diskNumber\":1,\"sudo\":true}]")]
    [InlineData("[{\"kind\":\"formatEverything\",\"diskNumber\":1}]")]
    [InlineData("[{\"kind\":\"deletePartition\",\"diskNumber\":1}]")]
    [InlineData("[null]")]
    public void TheCodecRejectsAnythingUnexpected(string json) => Assert.Throws<ArgumentException>(() => DiskOperationCodec.Deserialize(json));

    [Fact]
    public void TooManyOperationsAreRejected()
    {
        var operations = Enumerable.Range(0, DiskOperationRules.MaxOperations + 1).Select(_ => new DiskOperation(CleanDisk, 1)).ToList();

        Assert.Throws<ArgumentException>(() => DiskOperationCodec.Deserialize(DiskOperationCodec.Serialize(operations)));
    }

    // ───────────── the live state ─────────────

    [Fact]
    public void WindowsAndTheBootFilesAreNeverTouched()
    {
        Assert.Contains("Windows or the boot files", Why(new DiskOperation(CleanDisk, 0)), StringComparison.Ordinal);
        Assert.Contains("Windows or the boot files", Why(Op(DeletePartition, 0, 3)), StringComparison.Ordinal);
        Assert.Contains("Windows or the boot files", Why(new DiskOperation(FormatVolume, 0) { PartitionNumber = 3, FileSystem = "NTFS" }), StringComparison.Ordinal);
        Assert.Contains("Windows or the boot files", Why(new DiskOperation(SetDriveLetter, 0) { PartitionNumber = 3, DriveLetter = 'Z' }), StringComparison.Ordinal);
        Assert.Contains("Windows or the boot files", Why(Op(DeletePartition, 0, 1)), StringComparison.Ordinal);
        Assert.Contains("never taken offline", Why(new DiskOperation(SetDiskState, 0) { Option = "offline" }), StringComparison.Ordinal);
        Assert.Contains("never made read-only", Why(new DiskOperation(SetDiskState, 0) { Option = "readonly" }), StringComparison.Ordinal);
    }

    [Fact]
    public void ReservedAndRecoveryPartitionsAreNotDataPartitions()
    {
        var recovery = SampleDisks.Data with { Partitions = [.. SampleDisks.Data.Partitions, new DiskPartition(1, 2, null, 1L << 30, "Recovery", false, false, "NTFS", null, null)] };

        Assert.Contains("not a data partition", Why(Op(DeletePartition, 1, 2), [recovery]), StringComparison.Ordinal);
        Assert.Contains("not a data partition", Why(Op(DeletePartition, 0, 2)), StringComparison.Ordinal);
    }

    [Fact]
    public void ThingsThatDoNotExistAreRefused()
    {
        Assert.Contains("Disk 7 does not exist", Why(new DiskOperation(CleanDisk, 7)), StringComparison.Ordinal);
        Assert.Contains("no partition 9", Why(Op(DeletePartition, 1, 9)), StringComparison.Ordinal);
    }

    [Fact]
    public void InitializingNeedsARawOnlineDisk()
    {
        Assert.Contains("Bring disk 2 online first", Why(new DiskOperation(InitializeDisk, 2) { Option = "GPT" }), StringComparison.Ordinal);
        Assert.Contains("already initialized", Why(new DiskOperation(InitializeDisk, 1) { Option = "GPT" }), StringComparison.Ordinal);
        DiskOperationRules.CheckAgainst(new DiskOperation(InitializeDisk, 2) { Option = "GPT" }, [SampleDisks.Blank with { IsOffline = false }]);
    }

    [Fact]
    public void NewVolumesNeedInitializedWritableDisksWithRoomAndFreeLetters()
    {
        Assert.Contains("Initialize disk 2 first", Why(new DiskOperation(NewVolume, 2), [SampleDisks.Blank with { IsOffline = false }]), StringComparison.Ordinal);
        Assert.Contains("1.46 TB unallocated", Why(new DiskOperation(NewVolume, 1) { SizeBytes = 2000L << 30 }), StringComparison.Ordinal);
        Assert.Contains("E: is already used by partition 1 of disk 1", Why(new DiskOperation(NewVolume, 1) { DriveLetter = 'e' }), StringComparison.Ordinal);
        Assert.Contains("read-only", Why(new DiskOperation(NewVolume, 1), [SampleDisks.Data with { IsReadOnly = true }]), StringComparison.Ordinal);
        Assert.Contains("a volume needs at least", Why(new DiskOperation(NewVolume, 0)), StringComparison.Ordinal);
        DiskOperationRules.CheckAgainst(new DiskOperation(NewVolume, 1) { SizeBytes = 1000L << 30, FileSystem = "NTFS", DriveLetter = 'F' }, Disks);
    }

    [Fact]
    public void ResizingMayNotGrowPastTheFreeSpace()
    {
        Assert.Contains("can grow to 1.95 TB", Why(new DiskOperation(ResizePartition, 1) { PartitionNumber = 1, SizeBytes = 3000L << 30 }), StringComparison.Ordinal);
        DiskOperationRules.CheckAgainst(new DiskOperation(ResizePartition, 1) { PartitionNumber = 1, SizeBytes = 100L << 30 }, Disks);
        DiskOperationRules.CheckAgainst(new DiskOperation(ResizePartition, 0) { PartitionNumber = 3, SizeBytes = 900L << 30 }, Disks);
    }

    [Fact]
    public void LabelsCheckAndOptimizeNeedAFileSystemButMayTouchTheBootVolume()
    {
        DiskOperationRules.CheckAgainst(new DiskOperation(SetLabel, 0) { PartitionNumber = 3, Label = "OS" }, Disks);
        DiskOperationRules.CheckAgainst(new DiskOperation(CheckVolume, 0) { PartitionNumber = 3, Option = "scan" }, Disks);
        DiskOperationRules.CheckAgainst(new DiskOperation(OptimizeVolume, 0) { PartitionNumber = 3, Option = "retrim" }, Disks);
        Assert.Contains("no file system", Why(new DiskOperation(CheckVolume, 0) { PartitionNumber = 2, Option = "scan" }), StringComparison.Ordinal);
    }

    [Fact]
    public void DiskStatesMustActuallyChange()
    {
        DiskOperationRules.CheckAgainst(new DiskOperation(SetDiskState, 2) { Option = "online" }, Disks);
        Assert.Contains("already online", Why(new DiskOperation(SetDiskState, 1) { Option = "online" }), StringComparison.Ordinal);
        Assert.Contains("already offline", Why(new DiskOperation(SetDiskState, 2) { Option = "offline" }), StringComparison.Ordinal);
        DiskOperationRules.CheckAgainst(new DiskOperation(SetDiskState, 1) { Option = "readonly" }, Disks);
        Assert.Contains("already writable", Why(new DiskOperation(SetDiskState, 1) { Option = "readwrite" }), StringComparison.Ordinal);
    }

    [Fact]
    public void OfflineDisksAreLeftAloneUntilTheyAreOnline()
    {
        Assert.Contains("Bring disk 1 online first", Why(Op(DeletePartition, 1, 1), [SampleDisks.Data with { IsOffline = true }]), StringComparison.Ordinal);
        Assert.Contains("nothing on it to wipe", Why(new DiskOperation(CleanDisk, 2), [SampleDisks.Blank with { IsOffline = false }]), StringComparison.Ordinal);
    }

    // ───────────── what the layout becomes ─────────────

    [Fact]
    public void ProjectionChainsInitializeThenCreateThenChangeTheDataDisk()
    {
        var blank = SampleDisks.Blank with { IsOffline = false };
        IReadOnlyList<PhysicalDisk> layout = [SampleDisks.System, SampleDisks.Data, blank];

        layout = DiskOperationRules.Project(new DiskOperation(InitializeDisk, 2) { Option = "GPT" }, layout);
        layout = DiskOperationRules.Project(new DiskOperation(NewVolume, 2) { SizeBytes = 100L << 30, FileSystem = "ntfs", Label = "Scratch" }, layout);
        layout = DiskOperationRules.Project(new DiskOperation(NewVolume, 2) { FileSystem = "exFAT", DriveLetter = 'x' }, layout);

        var disk = layout.Single(d => d.Number == 2);
        Assert.Equal("GPT", disk.PartitionStyle);
        Assert.Equal([DiskOperationRules.PlannedPartitionBase, DiskOperationRules.PlannedPartitionBase + 1], disk.Partitions.Select(p => p.PartitionNumber));
        Assert.Equal(['D', 'X'], disk.Partitions.Select(p => p.DriveLetter));
        Assert.Equal(["NTFS", "exFAT"], disk.Partitions.Select(p => p.FileSystem));
        Assert.Equal(500L << 30, disk.AllocatedSize);
        Assert.Equal(0, disk.UnallocatedBytes);
        Assert.All(disk.Partitions, p => Assert.True(DiskOperationRules.IsPlanned(p)));
    }

    [Fact]
    public void APlannedVolumeCannotBeChangedBeforeItExists()
    {
        var blank = SampleDisks.Blank with { IsOffline = false, PartitionStyle = "GPT" };
        var layout = DiskOperationRules.Project(new DiskOperation(NewVolume, 2) { FileSystem = "NTFS" }, [blank]);
        var planned = layout[0].Partitions.Single().PartitionNumber;

        Assert.Throws<ArgumentException>(() => DiskOperationRules.Validate(Op(DeletePartition, 2, planned)));
    }

    [Fact]
    public void ProjectionAppliesEachKindToTheRightPartition()
    {
        var layout = DiskOperationRules.Project(new DiskOperation(FormatVolume, 1) { PartitionNumber = 1, FileSystem = "NTFS", Label = "Fresh" }, Disks);
        layout = DiskOperationRules.Project(new DiskOperation(ResizePartition, 1) { PartitionNumber = 1, SizeBytes = 800L << 30 }, layout);
        layout = DiskOperationRules.Project(new DiskOperation(SetDriveLetter, 1) { PartitionNumber = 1, DriveLetter = 'g' }, layout);

        var partition = layout[1].Partitions.Single();
        Assert.Equal(("NTFS", "Fresh", 'G', 800L << 30), (partition.FileSystem, partition.Label, partition.DriveLetter, partition.Size));
        Assert.Equal(800L << 30, layout[1].AllocatedSize);

        layout = DiskOperationRules.Project(new DiskOperation(SetLabel, 1) { PartitionNumber = 1, Label = string.Empty }, layout);
        Assert.Null(layout[1].Partitions.Single().Label);

        layout = DiskOperationRules.Project(Op(DeletePartition, 1, 1), layout);
        Assert.Empty(layout[1].Partitions);
        Assert.Equal(0, layout[1].AllocatedSize);

        layout = DiskOperationRules.Project(new DiskOperation(SetDiskState, 1) { Option = "offline" }, layout);
        Assert.True(layout[1].IsOffline);
        Assert.Equal(Disks[0], layout[0]);
    }

    [Fact]
    public void CleaningTurnsADiskBackIntoRawSpace()
    {
        var layout = DiskOperationRules.Project(new DiskOperation(CleanDisk, 1), Disks);

        Assert.True(layout[1].IsRaw);
        Assert.Empty(layout[1].Partitions);
        Assert.Equal(0, layout[1].AllocatedSize);
    }

    [Fact]
    public void NextFreeLetterSkipsLettersInUse()
    {
        Assert.Equal('D', DiskOperationRules.NextFreeLetter(Disks));
        Assert.Equal('F', DiskOperationRules.NextFreeLetter([SampleDisks.Data with { Partitions = [SampleDisks.Data.Partitions[0] with { DriveLetter = 'D' }, SampleDisks.Data.Partitions[0] with { PartitionNumber = 2, DriveLetter = 'e' }] }]));
    }

    // ───────────── words and commands ─────────────

    [Theory]
    [InlineData("200GB", 200L << 30)]
    [InlineData("200 gb", 200L << 30)]
    [InlineData("1.5TB", 3L << 39)]
    [InlineData("512M", 512L << 20)]
    [InlineData("64k", 64L << 10)]
    [InlineData("1048576", 1048576)]
    public void SizesParseInBinaryUnits(string text, long expected)
    {
        Assert.True(DiskOperationRules.TryParseSize(text, out var bytes));
        Assert.Equal(expected, bytes);
    }

    [Theory]
    [InlineData("")]
    [InlineData("max")]
    [InlineData("-5GB")]
    [InlineData("0")]
    [InlineData("GB")]
    [InlineData("1e30GB")]
    [InlineData("Infinity")]
    public void NonSizesDoNotParse(string text) => Assert.False(DiskOperationRules.TryParseSize(text, out _));

    [Theory]
    [InlineData(500, "500 B")]
    [InlineData(1536, "1.5 KB")]
    [InlineData(100L << 20, "100 MB")]
    [InlineData(1500L << 30, "1.46 TB")]
    public void BytesAreShownInBinaryUnits(long bytes, string expected) => Assert.Equal(expected, DiskOperationRules.FormatBytes(bytes));

    [Fact]
    public void EveryOperationHasADescriptionAndAPowerShellCommand()
    {
        Assert.Equal("Initialize disk 2 as GPT", DiskOperationRules.Describe(new DiskOperation(InitializeDisk, 2) { Option = "gpt" }));
        Assert.Equal("Initialize-Disk -Number 2 -PartitionStyle GPT -Confirm:$false", DiskOperationRules.ToCommand(new DiskOperation(InitializeDisk, 2) { Option = "gpt" }));

        var create = new DiskOperation(NewVolume, 1) { SizeBytes = 200L << 30, FileSystem = "NTFS", Label = "it's mine", DriveLetter = 'f' };
        Assert.Equal("Create 200 GB volume on disk 1, NTFS \"it's mine\", drive F:", DiskOperationRules.Describe(create));
        Assert.Equal("New-Partition -DiskNumber 1 -Size 200GB -DriveLetter F | Format-Volume -FileSystem NTFS -Confirm:$false -NewFileSystemLabel 'it''s mine'", DiskOperationRules.ToCommand(create));
        Assert.Equal("New-Partition -DiskNumber 1 -UseMaximumSize -AssignDriveLetter", DiskOperationRules.ToCommand(new DiskOperation(NewVolume, 1)));
        Assert.Equal("Create a volume using all the free space on disk 1, unformatted, next free drive letter", DiskOperationRules.Describe(new DiskOperation(NewVolume, 1)));

        Assert.Equal("Get-Partition -DiskNumber 1 -PartitionNumber 1 | Format-Volume -FileSystem exFAT -Confirm:$false", DiskOperationRules.ToCommand(new DiskOperation(FormatVolume, 1) { PartitionNumber = 1, FileSystem = "exFAT" }));
        Assert.Equal("Resize-Partition -DiskNumber 1 -PartitionNumber 1 -Size 1500MB", DiskOperationRules.ToCommand(new DiskOperation(ResizePartition, 1) { PartitionNumber = 1, SizeBytes = 1500L << 20 }));
        Assert.Equal("Remove-Partition -DiskNumber 1 -PartitionNumber 1 -Confirm:$false", DiskOperationRules.ToCommand(Op(DeletePartition, 1, 1)));
        Assert.Equal("Set-Partition -DiskNumber 1 -PartitionNumber 1 -NewDriveLetter Q", DiskOperationRules.ToCommand(new DiskOperation(SetDriveLetter, 1) { PartitionNumber = 1, DriveLetter = 'q' }));
        Assert.Equal("Get-Partition -DiskNumber 1 -PartitionNumber 1 | Get-Volume | Repair-Volume -OfflineScanAndFix", DiskOperationRules.ToCommand(new DiskOperation(CheckVolume, 1) { PartitionNumber = 1, Option = "fix" }));
        Assert.Equal("Get-Partition -DiskNumber 1 -PartitionNumber 1 | Get-Volume | Optimize-Volume -Defrag", DiskOperationRules.ToCommand(new DiskOperation(OptimizeVolume, 1) { PartitionNumber = 1, Option = "defrag" }));
        Assert.Equal("Set-Disk -Number 2 -IsOffline $false", DiskOperationRules.ToCommand(new DiskOperation(SetDiskState, 2) { Option = "online" }));
        Assert.Equal("Clear-Disk -Number 1 -RemoveData -RemoveOEM -Confirm:$false", DiskOperationRules.ToCommand(new DiskOperation(CleanDisk, 1)));
        Assert.Contains("erases it", DiskOperationRules.Describe(Op(DeletePartition, 1, 1)), StringComparison.Ordinal);
    }

    [Fact]
    public void DescriptionsAndCommandsExistForEveryKind()
    {
        foreach (var operation in ValidOperations().Select(row => row.Data))
        {
            Assert.False(string.IsNullOrWhiteSpace(DiskOperationRules.Describe(operation)), operation.Kind.ToString());
            Assert.False(string.IsNullOrWhiteSpace(DiskOperationRules.ToCommand(operation)), operation.Kind.ToString());
        }

        Assert.Equal([FormatVolume, DeletePartition, CleanDisk], Enum.GetValues<DiskOperationKind>().Where(k => DiskOperationRules.IsDestructive(new DiskOperation(k, 0))));
    }
}
