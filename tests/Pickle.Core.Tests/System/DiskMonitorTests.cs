using Pickle.Abstractions.Services;
using Pickle.Core.SystemMonitoring;

namespace Pickle.Core.Tests.SystemMonitoring;

public sealed class DiskMonitorTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "pickle-disk-tests", Guid.NewGuid().ToString("N")[..10]);

    public DiskMonitorTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        foreach (var dir in Directory.EnumerateDirectories(_root, "*", SearchOption.AllDirectories))
        {
            TryChmod(dir, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        Directory.Delete(_root, recursive: true);
    }

    [Fact]
    public void AnalyzerAggregatesSizesLargestFirst()
    {
        Write("big/a.bin", 5000);
        Write("big/nested/b.bin", 3000);
        Write("small/c.txt", 100);
        Write("top.txt", 10);

        var root = new DiskUsageScanner().Scan(_root);

        Assert.Equal(8110, root.Size);
        Assert.Equal(4, root.FileCount);
        Assert.Equal(3, root.DirectoryCount);
        Assert.Equal(["big", "small", "top.txt"], root.Children.Select(c => c.Name));
        var big = root.Children[0];
        Assert.True(big.IsDirectory);
        Assert.Equal(8000, big.Size);
        Assert.Equal(Path.Combine(_root, "big"), big.FullPath);
        Assert.Equal(["a.bin", "nested"], big.Children.Select(c => c.Name));
        Assert.Equal(Path.Combine(_root, "big", "nested", "b.bin"), big.Children[1].Children[0].FullPath);
        Assert.Same(root, big.Parent);
    }

    [Fact]
    public void AnalyzerNeverFollowsSymlinks()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "creating symlinks needs developer mode on Windows");
        var outside = Path.Combine(Path.GetTempPath(), "pickle-disk-tests", Guid.NewGuid().ToString("N")[..10]);
        Directory.CreateDirectory(outside);
        try
        {
            File.WriteAllBytes(Path.Combine(outside, "huge.bin"), new byte[100_000]);
            Write("real.bin", 1000);
            Directory.CreateSymbolicLink(Path.Combine(_root, "linked-dir"), outside);
            File.CreateSymbolicLink(Path.Combine(_root, "linked-file.bin"), Path.Combine(outside, "huge.bin"));
            Directory.CreateSymbolicLink(Path.Combine(_root, "loop"), _root);

            var root = new DiskUsageScanner().Scan(_root);

            Assert.Equal(1000, root.Size);
            Assert.Equal(3, root.Skipped);
            Assert.Equal(["real.bin"], root.Children.Select(c => c.Name));
        }
        finally
        {
            Directory.Delete(outside, recursive: true);
        }
    }

    [Fact]
    public void AnalyzerSkipsOtherFileSystemsAndUnreadableDirectories()
    {
        Write("mnt/usb/photo.jpg", 4000);
        Write("locked/secret.bin", 2000);
        Write("ok.bin", 10);
        var locked = Path.Combine(_root, "locked");
        TryChmod(locked, UnixFileMode.None);

        var root = new DiskUsageScanner(mountPoints: [Path.Combine(_root, "mnt", "usb") + "/", "/"]).Scan(_root);

        var mnt = root.Children.Single(c => c.Name == "mnt");
        Assert.Equal(0, mnt.Size);
        Assert.Equal(1, mnt.Skipped);
        if (!OperatingSystem.IsWindows() && !CanReadAsRoot(locked))
        {
            Assert.Equal(0, root.Children.Single(c => c.Name == "locked").Size);
            Assert.Equal(2, root.Skipped);
        }
    }

    [Fact]
    public void ManySmallFilesAreGrouped()
    {
        for (var i = 0; i < 5; i++)
        {
            Write($"f{i}.bin", (i + 1) * 100);
        }

        var root = new DiskUsageScanner(maxFiles: 2).Scan(_root);

        Assert.Equal(["f4.bin", "f3.bin", "(3 smaller files)"], root.Children.Select(c => c.Name));
        var group = root.Children[2];
        Assert.True(group.IsGroup);
        Assert.Equal(600, group.Size);
        Assert.Equal(3, group.FileCount);
        Assert.Equal(_root, group.FullPath);
        Assert.Equal(1500, root.Size);
        Assert.Equal(5, root.FileCount);
    }

    [Fact]
    public async Task AnalyzeReportsProgressAndCanBeCancelled()
    {
        Write("a/b/c.bin", 10);
        var monitor = new DiskMonitor();
        var reports = new List<DiskScanProgress>();

        var root = await monitor.AnalyzeAsync(_root, new SyncProgress(reports.Add));
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        Assert.Equal(10, root.Size);
        Assert.Equal(3, reports[^1].Directories);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => monitor.AnalyzeAsync(_root, null, cancelled.Token));
        await Assert.ThrowsAsync<DirectoryNotFoundException>(() => monitor.AnalyzeAsync(Path.Combine(_root, "missing")));
    }

    [Fact]
    public void IoRatesComeFromDiskStatsDeltas()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), "/proc/diskstats is Linux only");
        var proc = Path.Combine(_root, "proc");
        var block = Path.Combine(_root, "block");
        Directory.CreateDirectory(proc);
        Directory.CreateDirectory(Path.Combine(block, "sda"));
        Directory.CreateDirectory(Path.Combine(block, "loop0"));
        var stats = Path.Combine(proc, "diskstats");
        File.WriteAllText(stats, "8 0 sda 1 0 1000 0 1 0 2000 0 0 0 0\n8 1 sda1 1 0 1000 0 1 0 2000 0 0 0 0\n7 0 loop0 1 0 50 0 0 0 0 0 0 0 0\n");
        var monitor = new DiskMonitor(proc, block);

        Assert.True(monitor.SupportsIoRates);
        var first = monitor.SampleIo(TimeSpan.FromSeconds(1));
        File.WriteAllText(stats, "8 0 sda 1 0 3000 0 1 0 2000 0 0 0 0\n");
        var second = monitor.SampleIo(TimeSpan.FromSeconds(3));

        Assert.Equal("sda", Assert.Single(first).Device);
        var sda = Assert.Single(second);
        Assert.Equal(3000L * 512, sda.BytesRead);
        Assert.Equal(2000.0 * 512 / 2, sda.ReadRate);
        Assert.Equal(0, sda.WriteRate);
    }

    [Fact]
    public async Task VolumesAreListedAndPseudoFileSystemsFiltered()
    {
        var volumes = await new DiskMonitor().GetVolumesAsync();

        Assert.All(volumes, v => Assert.True(v.TotalBytes > 0));
        Assert.False(DiskMonitor.IsStorage("/proc", "proc", 0));
        Assert.False(DiskMonitor.IsStorage(_root, "tmpfs", 1000));
        Assert.True(DiskMonitor.IsStorage(_root, "ext4", 1000));
        Assert.False(DiskMonitor.IsStorage(Path.Combine(_root, "missing"), "ext4", 1000));
    }

    [Fact]
    public async Task SystemToolsAreWindowsOnly()
    {
        var monitor = new DiskMonitor();
        Assert.Equal(OperatingSystem.IsWindows(), monitor.SupportsSystemTools);
        if (!OperatingSystem.IsWindows())
        {
            Assert.False((await monitor.OpenDiskCleanupAsync("C:\\")).Success);
            Assert.False((await monitor.OpenDiskManagementAsync()).Success);
        }
    }

    private static bool CanReadAsRoot(string path)
    {
        try
        {
            _ = Directory.EnumerateFileSystemEntries(path).Any();
            return true;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static void TryChmod(string path, UnixFileMode mode)
    {
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(path, mode);
        }
    }

    private void Write(string relative, int size)
    {
        var path = Path.Combine(_root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, new byte[size]);
    }

    private sealed class SyncProgress(Action<DiskScanProgress> report) : IProgress<DiskScanProgress>
    {
        public void Report(DiskScanProgress value) => report(value);
    }
}
