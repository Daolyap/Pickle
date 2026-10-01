using Pickle.Abstractions;
using Pickle.Abstractions.Services;
using Pickle.Testing;
using Pickle.Testing.Fakes;
using Pickle.Tui.Panels.SystemMonitoring;
using static Pickle.Tui.Tests.Windows.PanelRunner;

namespace Pickle.Tui.Tests.SystemMonitoring;

public sealed class PartitionsTabTests : IDisposable
{
    private static readonly PhysicalDisk SystemDisk = new(
        0, "Samsung SSD 980", "GPT", 1000L << 30, 1000L << 30, "Online", false, false, true, true, "NVMe",
        [
            new DiskPartition(0, 1, null, 100L << 20, "System", false, true, "FAT32", null, 60L << 20),
            new DiskPartition(0, 2, null, 16L << 20, "Reserved", false, false, null, null, null),
            new DiskPartition(0, 3, 'C', 999L << 30, "Basic", true, false, "NTFS", "Windows", 400L << 30),
        ]);

    private static readonly PhysicalDisk DataDisk = new(
        1, "WD Elements", "GPT", 2000L << 30, 500L << 30, "Online", false, false, false, false, "USB",
        [new DiskPartition(1, 1, 'E', 500L << 30, "Basic", false, false, "exFAT", "Backup", 100L << 30)]);

    private static readonly PhysicalDisk NewDisk = new(2, "Blank SSD", "RAW", 500L << 30, 0, "Offline", true, false, false, false, "SATA", []);

    private readonly TestPickle _t = TestPickle.Create();

    public void Dispose() => _t.Dispose();

    [Fact]
    public void RowsFollowTheDiskLayoutWithFreeSpace()
    {
        var rows = DiskLayoutRows.Build([DataDisk, SystemDisk, NewDisk]);

        Assert.Equal(
            ["Disk 0  Samsung SSD 980", "    partition 1", "    partition 2", "    C: partition 3", "Disk 1  WD Elements", "    E: partition 1", "    unallocated", "Disk 2  Blank SSD"],
            rows.Select(DiskLayoutRows.Name).Select(n => n.Replace("       partition", "    partition", StringComparison.Ordinal)));
        Assert.Equal(1500L << 30, DiskLayoutRows.Size(rows[6]));
        Assert.Contains("RAW (not initialized)", DiskLayoutRows.Kind(rows[7]), StringComparison.Ordinal);
        Assert.Contains("400 GB free", DiskLayoutRows.Status(rows[3]), StringComparison.Ordinal);
    }

    [Fact]
    public void PlannedVolumesAreMarkedInTheRows()
    {
        var planned = DataDisk with
        {
            AllocatedSize = 1000L << 30,
            Partitions = [.. DataDisk.Partitions, new DiskPartition(1, DiskOperationRules.PlannedPartitionBase, 'F', 500L << 30, "Basic", false, false, "NTFS", "Scratch", 500L << 30)],
        };

        var rows = DiskLayoutRows.Build([planned]);

        Assert.Equal("    F: new volume", DiskLayoutRows.Name(rows[2]));
        Assert.StartsWith("planned", DiskLayoutRows.Status(rows[2]), StringComparison.Ordinal);
    }

    [Fact]
    public void EnterOpensTheDiskConfigurationPanelOrSaysItCannot()
    {
        _t.Runtime.ServiceRegistry.Add<IDiskMonitor>(new FakeDiskMonitor());
        _t.Runtime.ServiceRegistry.Add<IDiskLayoutService>(new FakeLayout([SystemDisk, DataDisk]));
        string? message = null;
        using var panel = new DisksPanel(new PanelContext { Pickle = _t.Runtime }) { MessageHook = (_, text) => message = text };

        Run(
            panel,
            When(() => true, () => panel.ShowTab(panel.PartitionsTab)),
            When(() => panel.Partitions!.TotalCount == 7, () =>
            {
                panel.Partitions!.Select(r => r.Partition?.DriveLetter == 'E');
                panel.ConfigureSelected();
            }));

        Assert.Contains("not available here", message, StringComparison.Ordinal);
    }

    private sealed class FakeLayout(IReadOnlyList<PhysicalDisk> disks) : IDiskLayoutService
    {
        public Task<IReadOnlyList<PhysicalDisk>> GetDisksAsync(CancellationToken cancellationToken = default) => Task.FromResult(disks);
    }
}
