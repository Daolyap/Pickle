using Pickle.Abstractions;
using Pickle.Abstractions.Services;
using Pickle.Testing;
using Pickle.Testing.Fakes;
using Pickle.Tui.Panels.SystemMonitoring;
using Pickle.Wizards;
using static Pickle.Tui.Tests.Windows.PanelRunner;

namespace Pickle.Tui.Tests.SystemMonitoring;

public sealed class PartitionsTabTests : IDisposable
{
    private static readonly IReadOnlyList<WizardDefinition> Wizards = WizardLoader.LoadEmbedded((name, ex) => throw new InvalidOperationException(name, ex));

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
    public void SystemDisksAndPartitionsAreNeverOfferedForDeletion()
    {
        var rows = DiskLayoutRows.Build([SystemDisk, DataDisk, NewDisk]);
        var labels = rows.ToDictionary(DiskLayoutRows.Name, r => DiskLayoutRows.Actions(r).Select(a => a.Label).ToList());

        Assert.DoesNotContain("Wipe the disk (clean)…", labels["Disk 0  Samsung SSD 980"]);
        Assert.DoesNotContain("Delete partition…", labels["    C: partition 3"]);
        Assert.Contains("Delete partition…", labels["    E: partition 1"]);
        Assert.Contains("Wipe the disk (clean)…", labels["Disk 1  WD Elements"]);
        Assert.Equal(["Initialize (GPT)…", "Bring online…"], labels["Disk 2  Blank SSD"]);
        Assert.Equal(["New partition in this space"], labels["    unallocated"]);
    }

    [Fact]
    public void EveryActionParsesBackIntoItsWizard()
    {
        foreach (var row in DiskLayoutRows.Build([SystemDisk, DataDisk, NewDisk]))
        {
            foreach (var action in DiskLayoutRows.Actions(row))
            {
                var wizard = Assert.Single(Wizards, w => w.Id == action.WizardId);
                var parsed = WizardEngine.Parse(wizard, action.Command);
                Assert.True(parsed.Matched, action.Command);
                Assert.Empty(parsed.UnknownTokens);
            }
        }
    }

    [Fact]
    public void EnterOffersActionsAndOpensThePrefilledWizard()
    {
        _t.Runtime.ServiceRegistry.Add<IDiskMonitor>(new FakeDiskMonitor());
        _t.Runtime.ServiceRegistry.Add<IDiskLayoutService>(new FakeLayout([SystemDisk, DataDisk]));
        var opened = new List<(string Wizard, string Command)>();
        IReadOnlyList<string>? offered = null;
        using var panel = new DisksPanel(new PanelContext { Pickle = _t.Runtime })
        {
            PickHook = (_, options) =>
            {
                offered = options;
                return "Format…";
            },
            WizardHook = (wizard, command) => opened.Add((wizard, command)),
        };

        Run(
            panel,
            When(() => true, () => panel.ShowTab(panel.PartitionsTab)),
            When(() => panel.Partitions!.TotalCount == 7, () =>
            {
                panel.Partitions!.Select(r => r.Partition?.DriveLetter == 'E');
                panel.ChooseDiskAction();
            }));

        Assert.Contains("Delete partition…", offered!);
        Assert.Equal([("format-volume", "Format-Volume -DriveLetter E -FileSystem NTFS")], opened);
    }

    private sealed class FakeLayout(IReadOnlyList<PhysicalDisk> disks) : IDiskLayoutService
    {
        public Task<IReadOnlyList<PhysicalDisk>> GetDisksAsync(CancellationToken cancellationToken = default) => Task.FromResult(disks);
    }
}
