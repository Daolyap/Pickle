using System.ComponentModel;
using System.Diagnostics;
using Pickle.Abstractions.Services;
using Pickle.Core.Commands;

namespace Pickle.Core.SystemMonitoring;

/// <summary>
/// <see cref="IDiskMonitor"/>: volumes from <see cref="DriveInfo"/>, block-device throughput from <c>/proc/diskstats</c>
/// (Linux only; Windows would need performance counters), the <see cref="DiskUsageScanner"/>, and on Windows the
/// Disk Cleanup and Disk Management tools from System32.
/// </summary>
public sealed class DiskMonitor : IDiskMonitor
{
    // Kernel and virtual file systems that aren't storage: listing them would bury the real volumes.
    private static readonly HashSet<string> PseudoFileSystems = new(StringComparer.OrdinalIgnoreCase)
    {
        "proc", "sysfs", "devpts", "devtmpfs", "tmpfs", "cgroup", "cgroup2", "cgroupfs", "mqueue", "debugfs", "tracefs",
        "securityfs", "pstore", "bpf", "configfs", "fusectl", "hugetlbfs", "autofs", "binfmt_misc", "nsfs", "rpc_pipefs",
        "efivarfs", "selinuxfs", "squashfs", "ramfs", "overlayfs_internal", "nfsd", "fuse.gvfsd-fuse", "fuse.portal",
    };

    private readonly RateTracker _rates = new();
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly object _gate = new();
    private readonly string _proc;
    private readonly string _sysBlock;

    public DiskMonitor()
        : this("/proc", "/sys/block")
    {
    }

    internal DiskMonitor(string proc, string sysBlock)
    {
        _proc = proc;
        _sysBlock = sysBlock;
    }

    public bool SupportsIoRates => OperatingSystem.IsLinux() && File.Exists(Path.Join(_proc, "diskstats"));

    public bool SupportsSystemTools => OperatingSystem.IsWindows();

    public Task<IReadOnlyList<DiskVolume>> GetVolumesAsync(CancellationToken cancellationToken = default) =>
        Task.Run<IReadOnlyList<DiskVolume>>(ReadVolumes, cancellationToken);

    public Task<IReadOnlyList<DiskIoSample>> SampleIoAsync(CancellationToken cancellationToken = default) =>
        Task.Run(() => SampleIo(_clock.Elapsed), cancellationToken);

    public Task<DiskUsageNode> AnalyzeAsync(string path, IProgress<DiskScanProgress>? progress = null, CancellationToken cancellationToken = default) =>
        Task.Run(() => new DiskUsageScanner(OperatingSystem.IsWindows() ? [] : MountPoints()).Scan(path, progress, cancellationToken), cancellationToken);

    public Task<DiskToolResult> OpenDiskCleanupAsync(string? volume, CancellationToken cancellationToken = default)
    {
        var drive = volume is { Length: >= 2 } v && char.IsAsciiLetter(v[0]) && v[1] == ':' ? v[..1] : null;
        return Task.FromResult(Launch("Disk Cleanup", "cleanmgr.exe", drive is null ? [] : ["/d", drive]));
    }

    public Task<DiskToolResult> OpenDiskManagementAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(Launch("Disk Management", "mmc.exe", [ExecutableLocator.SystemProgram("diskmgmt.msc")]));

    internal IReadOnlyList<DiskIoSample> SampleIo(TimeSpan now)
    {
        if (!SupportsIoRates || ReadFile(Path.Join(_proc, "diskstats")) is not { } text)
        {
            return [];
        }

        var wholeDisks = Directory.Exists(_sysBlock)
            ? new HashSet<string>(Directory.EnumerateFileSystemEntries(_sysBlock).Select(Path.GetFileName).OfType<string>(), StringComparer.Ordinal)
            : null;
        lock (_gate)
        {
            var keys = new HashSet<string>(StringComparer.Ordinal);
            var result = new List<DiskIoSample>();
            foreach (var stat in ProcFs.ParseDiskStats(text))
            {
                if ((wholeDisks is not null && !wholeDisks.Contains(stat.Device)) ||
                    stat.Device.StartsWith("loop", StringComparison.Ordinal) ||
                    stat.Device.StartsWith("ram", StringComparison.Ordinal) ||
                    stat.Device.StartsWith("zram", StringComparison.Ordinal) ||
                    stat.SectorsRead + stat.SectorsWritten == 0)
                {
                    continue;
                }

                string read = stat.Device + "/r", written = stat.Device + "/w";
                keys.Add(read);
                keys.Add(written);
                var bytesRead = stat.SectorsRead * ProcFs.SectorSize;
                var bytesWritten = stat.SectorsWritten * ProcFs.SectorSize;
                result.Add(new DiskIoSample(stat.Device, bytesRead, bytesWritten, _rates.Update(read, bytesRead, now), _rates.Update(written, bytesWritten, now)));
            }

            _rates.Retain(keys);
            return result;
        }
    }

    internal static bool IsStorage(string name, string? fileSystem, long totalBytes) =>
        totalBytes > 0 && (fileSystem is null || !PseudoFileSystems.Contains(fileSystem)) && Directory.Exists(name);

    private static List<DiskVolume> ReadVolumes()
    {
        var result = new List<DiskVolume>();
        foreach (var drive in DriveInfo.GetDrives())
        {
            try
            {
                if (!drive.IsReady)
                {
                    continue;
                }

                var fileSystem = drive.DriveFormat;
                var total = drive.TotalSize;
                if (!OperatingSystem.IsWindows() && !IsStorage(drive.Name, fileSystem, total))
                {
                    continue;
                }

                var label = OperatingSystem.IsWindows() ? drive.VolumeLabel : null;
                result.Add(new DiskVolume(drive.Name, string.IsNullOrWhiteSpace(label) ? null : label, fileSystem, drive.DriveType.ToString(), total, drive.AvailableFreeSpace));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }

        return result;
    }

    private static List<string> MountPoints()
    {
        try
        {
            return [.. DriveInfo.GetDrives().Select(d => d.Name)];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    private static DiskToolResult Launch(string title, string program, IReadOnlyList<string> arguments)
    {
        if (!OperatingSystem.IsWindows())
        {
            return new DiskToolResult(false, $"{title} is only available on Windows.");
        }

        var path = ExecutableLocator.SystemProgram(program);
        if (!File.Exists(path))
        {
            return new DiskToolResult(false, $"{title} was not found ({path}).");
        }

        // Shell execute so Windows can show its own UAC prompt when the tool asks for it (mmc.exe requests the
        // highest available rights); Pickle itself never elevates.
        var start = new ProcessStartInfo(path) { UseShellExecute = true, WorkingDirectory = Environment.SystemDirectory };
        foreach (var argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        try
        {
            using var process = Process.Start(start);
            return new DiskToolResult(true, $"Opened {title}.");
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            return new DiskToolResult(false, $"{title} was cancelled.");
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
        {
            return new DiskToolResult(false, $"Could not open {title}: {ex.Message}");
        }
    }

    private static string? ReadFile(string path)
    {
        try
        {
            return File.ReadAllText(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
