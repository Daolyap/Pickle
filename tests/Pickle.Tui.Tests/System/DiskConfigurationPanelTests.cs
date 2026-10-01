using Pickle.Abstractions;
using Pickle.Abstractions.Services;
using Pickle.Testing;
using Pickle.Testing.Fakes;
using Pickle.Tui.Panels.SystemMonitoring;
using static Pickle.Tui.Tests.Windows.PanelRunner;

namespace Pickle.Tui.Tests.SystemMonitoring;

public sealed class DiskConfigurationPanelTests : IDisposable
{
    private readonly TestPickle _t = TestPickle.Create();
    private readonly FakeDiskConfigurationService _service = new();
    private readonly List<(string Title, string Message)> _messages = [];
    private readonly List<string> _picked = [];

    public DiskConfigurationPanelTests() => _t.Runtime.ServiceRegistry.Add<IDiskConfigurationService>(_service);

    public void Dispose() => _t.Dispose();

    private DiskConfigurationPanel Panel(Func<string, IReadOnlyList<string>, string?>? pick = null, Func<string, string, string?>? prompt = null, Func<string, string, bool>? confirm = null) =>
        new(new PanelContext { Pickle = _t.Runtime })
        {
            MessageHook = (title, message) => _messages.Add((title, message)),
            ConfirmHook = confirm ?? ((_, _) => true),
            PickHook = (title, options) =>
            {
                var choice = pick is null ? options.FirstOrDefault() : pick(title, options);
                _picked.Add(choice ?? "<cancel>");
                return choice;
            },
            PromptHook = prompt ?? ((_, label) => label.StartsWith("Size", StringComparison.Ordinal) ? "max" : string.Empty),
        };

    private static (Func<bool>, Action) Loaded(DiskConfigurationPanel panel) => When(() => panel.Rows?.TotalCount > 0, () => { });

    private static void Select(DiskConfigurationPanel panel, Func<LayoutRow, bool> match) => panel.Rows!.Select(match);

    private static bool IsFree(LayoutRow row, int disk) => row.Free && row.Disk.Number == disk;

    private static bool IsPartition(LayoutRow row, int disk, int partition) => row.Partition is { } p && p.DiskNumber == disk && p.PartitionNumber == partition;

    private static bool IsDisk(LayoutRow row, int disk) => row.IsDisk && row.Disk.Number == disk;

    // ───────────── what it shows ─────────────

    [Fact]
    public void ShowsEveryDiskAsAStripAndRowsWithFreeSpace()
    {
        using var panel = Panel();

        Run(panel, Loaded(panel));

        Assert.Equal(
            ["Disk 0  Samsung SSD 980", "    partition 1", "    partition 2", "    C: partition 3", "Disk 1  WD Elements", "    E: partition 1", "    unallocated", "Disk 2  Blank SSD"],
            panel.Rows!.Rows.Select(DiskLayoutRows.Name).Select(n => n.Replace("       partition", "    partition", StringComparison.Ordinal)));
        Assert.Contains("Disk 1  WD Elements  ·  1.95 TB  ·  GPT", panel.MapText, StringComparison.Ordinal);
        Assert.Contains("█", panel.MapText, StringComparison.Ordinal);
        Assert.Contains("░", panel.MapText, StringComparison.Ordinal);
        Assert.Contains("not initialized", panel.MapText, StringComparison.Ordinal);
        Assert.Contains("Nothing planned", panel.PendingText, StringComparison.Ordinal);
        Assert.Contains("administrator approval", panel.PendingText, StringComparison.Ordinal);
    }

    [Fact]
    public void WithoutTheServiceItSaysWhatItNeeds()
    {
        _t.Runtime.ServiceRegistry.Add<IDiskConfigurationService>(new FakeDiskConfigurationService { Supported = false });
        using var panel = Panel();

        Assert.Null(panel.Rows);
    }

    // ───────────── planning ─────────────

    [Fact]
    public void ANewVolumeOnFreeSpaceAppearsAtOnceAsAPlannedVolume()
    {
        using var panel = Panel(
            pick: (_, options) => options.Contains("New volume…") ? "New volume…" : "NTFS",
            prompt: (_, label) => label.StartsWith("Size", StringComparison.Ordinal) ? "200GB" : label.StartsWith("Label", StringComparison.Ordinal) ? "Scratch" : "g");

        Run(
            panel,
            Loaded(panel),
            When(() => true, () =>
            {
                Select(panel, r => IsFree(r, 1));
                panel.ChooseAction();
            }));

        var operation = Assert.Single(panel.Pending);
        Assert.Equal(new DiskOperation(DiskOperationKind.NewVolume, 1) { SizeBytes = 200L << 30, FileSystem = "NTFS", Label = "Scratch", DriveLetter = 'G' }, operation);
        Assert.Contains(panel.Rows!.Rows, r => DiskLayoutRows.Name(r) == "    G: new volume");
        Assert.Contains("1. Create 200 GB volume on disk 1, NTFS \"Scratch\", drive G:", panel.PendingText, StringComparison.Ordinal);
        Assert.Empty(_service.Batches);
    }

    [Fact]
    public void ABlankDiskGoesOnlineIsInitializedAndGetsAVolumeInOnePass()
    {
        string? Pick(string title, IReadOnlyList<string> options) =>
            options.FirstOrDefault(o => o.StartsWith("Bring online", StringComparison.Ordinal) || o.StartsWith("Initialize", StringComparison.Ordinal) || o.StartsWith("New volume", StringComparison.Ordinal) || o.StartsWith("GPT", StringComparison.Ordinal) || o == "exFAT");
        using var panel = Panel(pick: Pick, prompt: (_, label) => label.StartsWith("Size", StringComparison.Ordinal) ? "max" : label.StartsWith("Drive", StringComparison.Ordinal) ? "auto" : "Media");

        Run(
            panel,
            Loaded(panel),
            When(() => true, () =>
            {
                Select(panel, r => IsDisk(r, 2));
                panel.ChooseAction();
                Select(panel, r => IsDisk(r, 2));
                panel.ChooseAction();
                Select(panel, r => IsDisk(r, 2));
                panel.ChooseAction();
            }));

        Assert.Equal([DiskOperationKind.SetDiskState, DiskOperationKind.InitializeDisk, DiskOperationKind.NewVolume], panel.Pending.Select(o => o.Kind));
        Assert.Equal(("GPT", "exFAT", "Media", null), (panel.Pending[1].Option, panel.Pending[2].FileSystem, panel.Pending[2].Label, panel.Pending[2].SizeBytes));
        Assert.Contains(panel.Projected.Single(d => d.Number == 2).Partitions, p => p.FileSystem == "exFAT");
    }

    [Fact]
    public void WindowsAndTheBootFilesAreNeverInTheMenu()
    {
        IReadOnlyList<string>? system = null;
        IReadOnlyList<string>? boot = null;
        IReadOnlyList<string>? data = null;
        using var panel = Panel(pick: (title, options) =>
        {
            if (title.StartsWith("Disk 0", StringComparison.Ordinal))
            {
                system = options;
            }
            else if (title.StartsWith("C:", StringComparison.Ordinal))
            {
                boot = options;
            }
            else
            {
                data = options;
            }

            return null;
        });

        Run(
            panel,
            Loaded(panel),
            When(() => true, () =>
            {
                Select(panel, r => IsDisk(r, 0));
                panel.ChooseAction();
                Select(panel, r => IsPartition(r, 0, 3));
                panel.ChooseAction();
                Select(panel, r => IsPartition(r, 1, 1));
                panel.ChooseAction();
            }));

        Assert.Empty(system ?? []);
        Assert.Equal(["Resize…", "Label…", "Check for errors…", "Optimize…"], boot);
        Assert.Equal(["Format…", "Resize…", "Change the drive letter…", "Label…", "Check for errors…", "Optimize…", "Delete…"], data);
        Assert.Contains(_messages, m => m.Title == "Nothing to plan");
    }

    [Fact]
    public void AnActionThatCannotWorkExplainsWhy()
    {
        using var panel = Panel();

        Run(
            panel,
            Loaded(panel),
            When(() => true, () =>
            {
                Select(panel, r => IsPartition(r, 0, 1));
                panel.ChooseAction();
            }));

        // the EFI partition has a file system, so labels and checks are offered, but never format or delete
        Assert.DoesNotContain(_picked, p => p is "Format…" or "Delete…");
    }

    [Fact]
    public void FormattingAsksForTheFileSystemAndLabelAndMarksTheRow()
    {
        using var panel = Panel(
            pick: (_, options) => options.Contains("Format…") ? "Format…" : "NTFS",
            prompt: (_, label) => "Fresh");

        Run(
            panel,
            Loaded(panel),
            When(() => true, () =>
            {
                Select(panel, r => IsPartition(r, 1, 1));
                panel.ChooseAction();
            }));

        Assert.Equal(new DiskOperation(DiskOperationKind.FormatVolume, 1) { PartitionNumber = 1, FileSystem = "NTFS", Label = "Fresh" }, Assert.Single(panel.Pending));
        Assert.Contains("← erases data", string.Join('\n', DiskConfigurationPanel.ReviewLines(panel.Pending)), StringComparison.Ordinal);
    }

    [Fact]
    public void ResizeShowsTheRangeWindowsAllowsAndRefusesOutsideIt()
    {
        _service.Range = new DiskResizeRange(1L << 30, 800L << 30);
        var labels = new List<string>();
        var answers = new Queue<string>(["900GB", "300GB"]);
        using var panel = Panel(
            pick: (_, options) => options.Contains("Resize…") ? "Resize…" : null,
            prompt: (_, label) =>
            {
                labels.Add(label);
                return answers.Dequeue();
            });

        Run(
            panel,
            Loaded(panel),
            When(() => true, () =>
            {
                Select(panel, r => IsPartition(r, 1, 1));
                panel.ChooseAction();
            }),
            When(() => _messages.Count > 0, () =>
            {
                Select(panel, r => IsPartition(r, 1, 1));
                panel.ChooseAction();
            }),
            When(() => panel.Pending.Count == 1, () => { }));

        Assert.Contains("[Windows allows 1 GB to 800 GB]", labels[0], StringComparison.Ordinal);
        Assert.Contains(_messages, m => m.Message.Contains("Windows allows 1 GB to 800 GB", StringComparison.Ordinal));
        Assert.Equal(300L << 30, panel.Pending.Single().SizeBytes);
    }

    [Fact]
    public void MaxMeansTheLargestSizeWindowsAllows()
    {
        _service.Range = new DiskResizeRange(1L << 30, 800L << 30);
        using var panel = Panel(pick: (_, options) => options.Contains("Resize…") ? "Resize…" : null, prompt: (_, _) => "max");

        Run(
            panel,
            Loaded(panel),
            When(() => true, () =>
            {
                Select(panel, r => IsPartition(r, 1, 1));
                panel.ChooseAction();
            }),
            When(() => panel.Pending.Count == 1, () => { }));

        Assert.Equal(800L << 30, panel.Pending.Single().SizeBytes);
    }

    [Fact]
    public void BadAnswersAreReportedAndPlanNothing()
    {
        using var panel = Panel(pick: (_, options) => options.Contains("New volume…") ? "New volume…" : "NTFS", prompt: (_, label) => label.StartsWith("Size", StringComparison.Ordinal) ? "lots" : string.Empty);

        Run(
            panel,
            Loaded(panel),
            When(() => true, () =>
            {
                Select(panel, r => IsFree(r, 1));
                panel.ChooseAction();
            }));

        Assert.Empty(panel.Pending);
        Assert.Contains(_messages, m => m.Message.Contains("'lots' is not a size", StringComparison.Ordinal));
    }

    [Fact]
    public void CancellingAnyQuestionPlansNothing()
    {
        using var panel = Panel(pick: (_, options) => options.Contains("New volume…") ? "New volume…" : null);

        Run(
            panel,
            Loaded(panel),
            When(() => true, () =>
            {
                Select(panel, r => IsFree(r, 1));
                panel.ChooseAction();
            }));

        Assert.Empty(panel.Pending);
    }

    [Fact]
    public void OperationsThatBreakTheRulesAreRefusedWhenPlanned()
    {
        using var panel = Panel();

        Run(panel, Loaded(panel), When(() => true, () =>
        {
            Assert.False(panel.Add(new DiskOperation(DiskOperationKind.CleanDisk, 0)));
            Assert.False(panel.Add(new DiskOperation(DiskOperationKind.DeletePartition, 0) { PartitionNumber = 3 }));
        }));

        Assert.Empty(panel.Pending);
        Assert.Equal(2, _messages.Count(m => m.Message.Contains("Windows or the boot files", StringComparison.Ordinal)));
    }

    [Fact]
    public void UndoRemovesTheLastAndDiscardAsksFirst()
    {
        var asked = 0;
        using var panel = Panel(confirm: (_, _) =>
        {
            asked++;
            return true;
        });

        Run(panel, Loaded(panel), When(() => true, () =>
        {
            panel.Add(new DiskOperation(DiskOperationKind.SetLabel, 1) { PartitionNumber = 1, Label = "One" });
            panel.Add(new DiskOperation(DiskOperationKind.SetLabel, 1) { PartitionNumber = 1, Label = "Two" });
            panel.Undo();
            Assert.Equal(["One"], panel.Pending.Select(o => o.Label));
            panel.Discard();
        }));

        Assert.Empty(panel.Pending);
        Assert.Equal(1, asked);
        Assert.Empty(_service.Batches);
    }

    [Fact]
    public void ReviewListsEveryChangeWithItsPowerShell()
    {
        using var panel = Panel();

        Run(panel, Loaded(panel), When(() => true, () =>
        {
            panel.Add(new DiskOperation(DiskOperationKind.SetLabel, 1) { PartitionNumber = 1, Label = "One" });
            panel.Add(new DiskOperation(DiskOperationKind.DeletePartition, 1) { PartitionNumber = 1 });
            panel.Review();
        }));

        var (title, text) = Assert.Single(_messages);
        Assert.Contains("PowerShell", title, StringComparison.Ordinal);
        Assert.Contains("1. Label partition 1 of disk 1 \"One\"", text, StringComparison.Ordinal);
        Assert.Contains("Get-Partition -DiskNumber 1 -PartitionNumber 1 | Get-Volume | Set-Volume -NewFileSystemLabel 'One'", text, StringComparison.Ordinal);
        Assert.Contains("2. Delete partition 1 of disk 1 (erases it)   ← erases data", text, StringComparison.Ordinal);
    }

    [Fact]
    public void ToShellPutsTheCommandsOnTheInputLineChainedSoAFailureStopsThem()
    {
        using var panel = Panel();

        Run(panel, Loaded(panel), When(() => true, () =>
        {
            panel.Add(new DiskOperation(DiskOperationKind.SetLabel, 1) { PartitionNumber = 1, Label = "One" });
            panel.Add(new DiskOperation(DiskOperationKind.SetDiskState, 1) { Option = "readonly" });
            panel.ToShell();
        }));

        var result = panel.Context.Result!;
        Assert.Equal(PanelResultKind.ReplaceInput, result.Kind);
        Assert.Equal("Get-Partition -DiskNumber 1 -PartitionNumber 1 | Get-Volume | Set-Volume -NewFileSystemLabel 'One' && Set-Disk -Number 1 -IsReadOnly $true", result.Text);
    }

    // ───────────── applying ─────────────

    [Fact]
    public void ApplyingAsksOnceSendsOneBatchAndRefreshesTheDisks()
    {
        string? question = null;
        using var panel = Panel(confirm: (_, message) =>
        {
            question = message;
            return true;
        });

        Run(
            panel,
            Loaded(panel),
            When(() => true, () =>
            {
                panel.Add(new DiskOperation(DiskOperationKind.SetLabel, 1) { PartitionNumber = 1, Label = "Renamed" });
                panel.Add(new DiskOperation(DiskOperationKind.NewVolume, 1) { SizeBytes = 100L << 30, FileSystem = "NTFS" });
                panel.Apply();
            }),
            When(() => _service.Batches.Count == 1 && panel.Pending.Count == 0 && _messages.Any(m => m.Title == "Done"), () => { }));

        Assert.Contains("administrator approval", question, StringComparison.Ordinal);
        Assert.Contains("2. Create 100 GB volume on disk 1, NTFS, next free drive letter", question, StringComparison.Ordinal);
        Assert.Equal(2, Assert.Single(_service.Batches).Count);
        Assert.Equal("Renamed", _service.Disks[1].Partitions[0].Label);
        Assert.Equal(2, _service.Disks[1].Partitions.Count);
        Assert.Contains("✓ Label partition 1 of disk 1", _messages.Single(m => m.Title == "Done").Message, StringComparison.Ordinal);
        Assert.True(_service.Reads >= 2);
    }

    [Fact]
    public void NothingIsSentWhenYouSayNo()
    {
        using var panel = Panel(confirm: (_, _) => false);

        Run(panel, Loaded(panel), When(() => true, () =>
        {
            panel.Add(new DiskOperation(DiskOperationKind.SetLabel, 1) { PartitionNumber = 1, Label = "x" });
            panel.Apply();
        }));

        Assert.Empty(_service.Batches);
        Assert.Single(panel.Pending);
    }

    [Fact]
    public void ErasingNeedsTheWordEraseTypedOut()
    {
        var typed = new Queue<string>(["yes", "ERASE"]);
        using var panel = Panel(prompt: (_, label) => label.Contains("ERASE", StringComparison.Ordinal) ? typed.Dequeue() : string.Empty);

        Run(
            panel,
            Loaded(panel),
            When(() => true, () =>
            {
                panel.Add(new DiskOperation(DiskOperationKind.DeletePartition, 1) { PartitionNumber = 1 });
                panel.Apply();
            }),
            When(() => _messages.Any(m => m.Title == "Cancelled"), () => panel.Apply()),
            When(() => _service.Batches.Count == 1 && _messages.Any(m => m.Title == "Done"), () => { }));

        Assert.Empty(_service.Disks[1].Partitions);
        Assert.Single(_service.Batches);
    }

    [Fact]
    public void AFailureStopsTheRunAndKeepsTheRestPlanned()
    {
        _service.FailAt = 1;
        using var panel = Panel();

        Run(
            panel,
            Loaded(panel),
            When(() => true, () =>
            {
                panel.Add(new DiskOperation(DiskOperationKind.SetLabel, 1) { PartitionNumber = 1, Label = "One" });
                panel.Add(new DiskOperation(DiskOperationKind.SetLabel, 1) { PartitionNumber = 1, Label = "Two" });
                panel.Add(new DiskOperation(DiskOperationKind.SetLabel, 1) { PartitionNumber = 1, Label = "Three" });
                panel.Apply();
            }),
            When(() => _messages.Any(m => m.Title == "Stopped"), () => { }),
            When(() => panel.Pending.Count == 2 && _service.Reads >= 2, () => { }));

        var message = _messages.Single(m => m.Title == "Stopped").Message;
        Assert.Contains("✓ Label partition 1 of disk 1 \"One\"", message, StringComparison.Ordinal);
        Assert.Contains("✗ Label partition 1 of disk 1 \"Two\": The disk is in use.", message, StringComparison.Ordinal);
        Assert.Contains("2 change(s) were not applied and stay planned", message, StringComparison.Ordinal);
        Assert.Equal(["Two", "Three"], panel.Pending.Select(o => o.Label));
    }

    [Fact]
    public void ADeclinedAdministratorPromptChangesNothingAndKeepsThePlan()
    {
        _service.ThrowOnApply = new OperationCanceledException("declined");
        using var panel = Panel();

        Run(
            panel,
            Loaded(panel),
            When(() => true, () =>
            {
                panel.Add(new DiskOperation(DiskOperationKind.SetLabel, 1) { PartitionNumber = 1, Label = "One" });
                panel.Apply();
            }),
            When(() => _messages.Any(m => m.Title == "Cancelled"), () => { }));

        Assert.Contains("declined", _messages.Single(m => m.Title == "Cancelled").Message, StringComparison.Ordinal);
        Assert.Single(panel.Pending);
    }

    [Fact]
    public void PlannedChangesThatNoLongerFitAreDroppedWithAnExplanation()
    {
        using var panel = Panel();

        Run(
            panel,
            Loaded(panel),
            When(() => true, () =>
            {
                panel.Add(new DiskOperation(DiskOperationKind.SetLabel, 1) { PartitionNumber = 1, Label = "One" });
                _service.Disks[1] = _service.Disks[1] with { Partitions = [] };
                panel.Refresh();
            }),
            When(() => _messages.Any(m => m.Title == "Planned changes dropped"), () => { }));

        Assert.Empty(panel.Pending);
        Assert.Contains("no partition 1", _messages.Single(m => m.Title == "Planned changes dropped").Message, StringComparison.Ordinal);
    }

    [Fact]
    public void OpeningWithADiskNumberSelectsThatDisk()
    {
        using var panel = new DiskConfigurationPanel(new PanelContext { Pickle = _t.Runtime, Argument = "1" });

        Run(panel, When(() => panel.Rows!.Selected is { IsDisk: true, Disk.Number: 1 }, () => { }));
    }

    // ───────────── the pieces ─────────────

    [Fact]
    public void TheMapSizesEveryBlockInProportionAndFillsTheWidth()
    {
        var lines = DiskMap.Render(SampleDisks.All, 80);

        Assert.Equal(9, lines.Count);
        Assert.All(new[] { 1, 4, 7 }.Select(i => lines[i].Text), bar => Assert.Equal(79, bar.Length));
        Assert.StartsWith(" ▕", lines[1].Text, StringComparison.Ordinal);
        Assert.EndsWith("▏", lines[1].Text, StringComparison.Ordinal);
        Assert.Equal(76, DiskMap.Widths([1L << 30, 1, 1, 1L << 40], 76).Sum());
        Assert.All(DiskMap.Widths([1L << 30, 1, 1, 1L << 40], 76), w => Assert.True(w >= 1));
        Assert.Equal([38, 38], DiskMap.Widths([1L << 30, 1L << 30], 76));
    }

    [Fact]
    public void ThePartitionsOfWindowsShowAsSystemBlocksAndFreeSpaceAsLightBlocks()
    {
        var lines = DiskMap.Render([SampleDisks.System, SampleDisks.Data], 80);

        Assert.Contains("▒", lines[1].Text, StringComparison.Ordinal);
        Assert.DoesNotContain("░", lines[1].Text, StringComparison.Ordinal);
        Assert.Contains("░", lines[4].Text, StringComparison.Ordinal);
        Assert.Contains("C: 999 GB", lines[2].Text, StringComparison.Ordinal);
        Assert.Contains("free 1.46 TB", lines[5].Text, StringComparison.Ordinal);
    }

    [Fact]
    public void MenusFollowTheStateOfTheItem()
    {
        var online = SampleDisks.Blank with { IsOffline = false };
        var disks = new[] { SampleDisks.System, SampleDisks.Data, online };
        string[] Labels(Func<LayoutRow, bool> match) => [.. DiskActions.For(DiskLayoutRows.Build(disks).Single(match), disks).Select(a => a.Label)];

        Assert.Equal(["Bring online"], DiskActions.For(DiskLayoutRows.Build(SampleDisks.All).Single(r => IsDisk(r, 2)), SampleDisks.All).Select(a => a.Label));
        Assert.Equal(["Initialize…", "Take offline", "Make read-only"], Labels(r => IsDisk(r, 2)));
        Assert.Equal(["New volume…", "Take offline", "Make read-only", "Wipe the disk…"], Labels(r => IsDisk(r, 1)));
        Assert.Equal(["New volume…"], Labels(r => IsFree(r, 1)));
        Assert.DoesNotContain(Labels(r => IsDisk(r, 0)), l => l.StartsWith("Wipe", StringComparison.Ordinal) || l.StartsWith("Take", StringComparison.Ordinal));
    }
}
