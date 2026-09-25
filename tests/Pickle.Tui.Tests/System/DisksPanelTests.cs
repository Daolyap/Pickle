using Pickle.Abstractions;
using Pickle.Abstractions.Services;
using Pickle.Core.SystemMonitoring;
using Pickle.Testing;
using Pickle.Testing.Fakes;
using Pickle.Tui.Panels.SystemMonitoring;
using Terminal.Gui.Input;
using Terminal.Gui.Testing;
using Terminal.Gui.Views;
using static Pickle.Tui.Tests.Windows.PanelRunner;

namespace Pickle.Tui.Tests.SystemMonitoring;

public sealed class DisksPanelTests : IDisposable
{
    private static readonly string Root = OperatingSystem.IsWindows() ? @"C:\" : "/";

    private readonly TestPickle _t = TestPickle.Create();
    private readonly FakeDiskMonitor _monitor = new();
    private readonly List<(string Title, string Message)> _told = [];

    public DisksPanelTests()
    {
        _t.Runtime.ServiceRegistry.Add<IDiskMonitor>(_monitor);
        _monitor.Volumes.Add(new DiskVolume(Root, "System", "ext4", "Fixed", 100L << 30, 10L << 30));
        _monitor.Volumes.Add(new DiskVolume(Path.Combine(Root, "mnt", "usb"), null, "vfat", "Removable", 32L << 30, 30L << 30));
        _monitor.Io.Add(new DiskIoSample("sda", 1 << 30, 2 << 30, 5 << 20, 1 << 20));
    }

    public void Dispose() => _t.Dispose();

    private DisksPanel Panel(PanelContext? context = null) =>
        new(context ?? new PanelContext { Pickle = _t.Runtime }) { MessageHook = (title, message) => _told.Add((title, message)) };

    [Fact]
    public void VolumesShowUsageBarsAndIoRates()
    {
        using var panel = Panel();
        var screen = SystemPanelRunner.RunAndDraw(panel, Wait(() => panel.Volumes.TotalCount == 2 && panel.Io.Rows.Count == 2));

        Assert.Equal(Root, panel.Volumes.Rows[0].Name);
        Assert.Contains("90.0%", screen, StringComparison.Ordinal);
        Assert.Contains("███", screen, StringComparison.Ordinal);
        Assert.Contains("100 GB", screen, StringComparison.Ordinal);
        Assert.Equal(["sda read", "sda write"], panel.Io.Rows.Select(r => r.Label));
        Assert.Equal("5.00 MB/s", panel.Io.Rows[0].Value);
        Assert.Contains("F3  Folder…", screen, StringComparison.Ordinal);
        Assert.DoesNotContain("Disk Cleanup", screen, StringComparison.Ordinal);
    }

    [Fact]
    public void EnterAnalyzesAVolumeAndDrillsDownAndBack()
    {
        using var panel = Panel();
        Run(
            panel,
            When(() => panel.Volumes.TotalCount == 2, () => panel.App!.InjectKey(Key.Enter)),
            When(() => panel.Current is not null, () =>
            {
                Assert.Equal(panel.AnalyzeTab, panel.CurrentTab);
                Assert.Equal(["big", "notes.txt"], panel.Entries.Rows.Select(n => n.Name));
                Assert.StartsWith(Root, panel.LocationText, StringComparison.Ordinal);
                panel.App!.InjectKey(Key.Enter);
            }),
            When(() => panel.Current?.Name == "big", () =>
            {
                Assert.Equal(["movie.mkv", "nested"], panel.Entries.Rows.Select(n => n.Name));
                panel.Entries.Select(n => n.Name == "nested");
                panel.App!.InjectKey(Key.Enter);
            }),
            When(() => panel.Current?.Name == "nested", () => panel.App!.InjectKey(Key.Backspace)),
            When(() => panel.Current?.Name == "big", () =>
            {
                Assert.Equal("nested", panel.Entries.Selected?.Name);
                panel.App!.InjectKey(Key.CursorLeft);
            }),
            When(() => panel.Current?.Parent is null && panel.CurrentTab == panel.AnalyzeTab, () =>
            {
                Assert.Equal("big", panel.Entries.Selected?.Name);
                panel.App!.InjectKey(Key.CursorLeft);
            }),
            Wait(() => panel.CurrentTab == panel.VolumesTab));

        Assert.Equal(["analyze " + Root], _monitor.Calls);
    }

    [Fact]
    public void OpenInShellReturnsACdForTheSelectedFolder()
    {
        var context = new PanelContext { Pickle = _t.Runtime, Argument = Path.Combine(Root, "data") };
        using var panel = Panel(context);
        Run(
            panel,
            When(() => panel.Current is not null, () =>
            {
                panel.Entries.Select(n => n.Name == "big");
                panel.OpenInShell();
            }));

        Assert.Equal(new PanelResult(PanelResultKind.ChangeDirectory, Path.Combine(Root, "data", "big")), context.Result);
    }

    [Fact]
    public void AScanCanBeStopped()
    {
        _monitor.Analyzer = async (path, ct) =>
        {
            await Task.Delay(Timeout.Infinite, ct);
            return FakeDiskMonitor.SampleTree(path);
        };
        using var panel = Panel();
        Run(
            panel,
            Do(() => panel.Analyze(Root)),
            When(() => panel.Scanning, panel.StopScan),
            Wait(() => !panel.Scanning && panel.ScanStatusText == "Scan stopped."));

        Assert.Null(panel.Current);
    }

    [Fact]
    public void RealScanOfATempFolderSkipsSymlinks()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "creating symlinks needs developer mode on Windows");
        var dir = TuiHarness.TempDir();
        var outside = TuiHarness.TempDir();
        try
        {
            Directory.CreateDirectory(Path.Combine(dir, "photos"));
            File.WriteAllBytes(Path.Combine(dir, "photos", "a.jpg"), new byte[3000]);
            File.WriteAllBytes(Path.Combine(dir, "readme.md"), new byte[200]);
            File.WriteAllBytes(Path.Combine(outside, "huge.iso"), new byte[50_000]);
            Directory.CreateSymbolicLink(Path.Combine(dir, "link"), outside);
            _t.Runtime.ServiceRegistry.Add<IDiskMonitor>(new DiskMonitor());
            using var panel = Panel(new PanelContext { Pickle = _t.Runtime, Argument = dir });
            Run(panel, Wait(() => panel.Current is not null));

            Assert.Equal(3200, panel.Current!.Size);
            Assert.Equal(["photos", "readme.md"], panel.Entries.Rows.Select(n => n.Name));
            Assert.Contains("1 skipped", panel.ScanStatusText, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
            Directory.Delete(outside, recursive: true);
        }
    }

    [Fact]
    public void WindowsToolsAreOfferedWhenSupported()
    {
        _monitor.SupportsSystemTools = true;
        _monitor.SupportsIoRates = false;
        using var panel = Panel();
        var screen = SystemPanelRunner.RunAndDraw(panel, Wait(() => panel.Volumes.TotalCount == 2), Do(() => panel.Hints.SubViews.OfType<Shortcut>().Single(s => s.Title == "Disk Cleanup").Action!()));

        Assert.Contains("F7  Disk Cleanup", screen, StringComparison.Ordinal);
        Assert.Contains("F9  Disk Mgmt", screen, StringComparison.Ordinal);
        Assert.Empty(panel.Io.Rows);
        Assert.Equal(["cleanup " + Root], _monitor.Calls);
    }
}
