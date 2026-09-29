using System.Globalization;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32;
using Pickle.Abstractions.Services;

namespace Pickle.Core.SystemMonitoring;

/// <summary>
/// <see cref="ISystemInfo"/> from the registry and Win32 on Windows, and from /proc, /sys and /etc/os-release on Linux.
/// The CPU model and OS name are read once; the rest on every call.
/// </summary>
public sealed class SystemInfoProvider : ISystemInfo
{
    private string? _operatingSystem;
    private string? _cpuModel;
    private bool _staticRead;

    public Task<SystemSummary> GetSummaryAsync(CancellationToken cancellationToken = default) => Task.Run(Read, cancellationToken);

    private SystemSummary Read()
    {
        if (!_staticRead)
        {
            (_operatingSystem, _cpuModel) = OperatingSystem.IsWindows() ? WindowsStatic() : (UnixOperatingSystem(), LinuxCpuModel());
            _staticRead = true;
        }

        var (swapUsed, swapTotal) = Swap();
        return new SystemSummary(
            Environment.MachineName,
            Environment.UserName,
            _operatingSystem ?? RuntimeInformation.OSDescription,
            _cpuModel,
            Environment.ProcessorCount,
            TimeSpan.FromMilliseconds(Environment.TickCount64),
            swapUsed,
            swapTotal,
            OperatingSystem.IsLinux() ? ParseLoadAverage(ReadText("/proc/loadavg")) : null,
            Battery(),
            RestartPending());
    }

    /// <summary>"0.52 0.58 0.59 1/467 12345" → [0.52, 0.58, 0.59].</summary>
    public static IReadOnlyList<double>? ParseLoadAverage(string? text)
    {
        var parts = text?.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts is not { Length: >= 3 })
        {
            return null;
        }

        var values = new List<double>(3);
        foreach (var part in parts.Take(3))
        {
            if (!double.TryParse(part, NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
            {
                return null;
            }

            values.Add(value);
        }

        return values;
    }

    /// <summary>The first "model name" in /proc/cpuinfo (ARM boards may only have "Model" or "Hardware").</summary>
    public static string? ParseCpuModel(string? cpuinfo)
    {
        foreach (var key in new[] { "model name", "Model", "Hardware" })
        {
            foreach (var line in (cpuinfo ?? string.Empty).Split('\n'))
            {
                var colon = line.IndexOf(':', StringComparison.Ordinal);
                if (colon > 0 && line[..colon].Trim() == key && line[(colon + 1)..].Trim() is { Length: > 0 } value)
                {
                    return Squeeze(value);
                }
            }
        }

        return null;
    }

    /// <summary>Swap used and total bytes from /proc/meminfo; null when there is none.</summary>
    public static (long Used, long Total)? ParseSwap(string? meminfo)
    {
        long? total = null, free = null;
        foreach (var line in (meminfo ?? string.Empty).Split('\n'))
        {
            var colon = line.IndexOf(':', StringComparison.Ordinal);
            if (colon <= 0)
            {
                continue;
            }

            var parts = line[(colon + 1)..].Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 0 || !long.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var kb))
            {
                continue;
            }

            switch (line[..colon])
            {
                case "SwapTotal":
                    total = kb * 1024;
                    break;
                case "SwapFree":
                    free = kb * 1024;
                    break;
            }
        }

        return total is > 0 and { } t && free is { } f ? (Math.Max(0, t - f), t) : null;
    }

    /// <summary>PRETTY_NAME from /etc/os-release, e.g. "Fedora Linux 42 (Workstation Edition)".</summary>
    public static string? ParseOsRelease(string? text)
    {
        foreach (var line in (text ?? string.Empty).Split('\n'))
        {
            if (line.StartsWith("PRETTY_NAME=", StringComparison.Ordinal))
            {
                return line["PRETTY_NAME=".Length..].Trim().Trim('"') is { Length: > 0 } name ? name : null;
            }
        }

        return null;
    }

    /// <summary>
    /// "Windows 11 Pro 24H2 (build 26100)". Windows 11 still reports "Windows 10" as its ProductName, so builds from
    /// 22000 on are renamed.
    /// </summary>
    public static string WindowsName(string? productName, string? displayVersion, int? build)
    {
        var name = string.IsNullOrWhiteSpace(productName) ? "Windows" : productName.Trim();
        if (build >= 22000 && name.StartsWith("Windows 10", StringComparison.Ordinal))
        {
            name = "Windows 11" + name["Windows 10".Length..];
        }

        if (!string.IsNullOrWhiteSpace(displayVersion))
        {
            name += " " + displayVersion.Trim();
        }

        return build is { } b ? $"{name} (build {b.ToString(CultureInfo.InvariantCulture)})" : name;
    }

    /// <summary>Battery state from /sys/class/power_supply (the first BAT* and whether any AC supply is online).</summary>
    public static BatteryStatus? ParseLinuxPowerSupply(string root)
    {
        if (!Directory.Exists(root))
        {
            return null;
        }

        int? percent = null;
        bool? charging = null;
        var hasBattery = false;
        var onAc = false;
        foreach (var supply in Directory.EnumerateDirectories(root).Order(StringComparer.Ordinal))
        {
            var type = ReadText(Path.Combine(supply, "type"))?.Trim();
            if (type == "Mains" && ReadText(Path.Combine(supply, "online"))?.Trim() == "1")
            {
                onAc = true;
            }
            else if (type == "Battery" && !hasBattery)
            {
                hasBattery = true;
                if (int.TryParse(ReadText(Path.Combine(supply, "capacity"))?.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var capacity))
                {
                    percent = Math.Clamp(capacity, 0, 100);
                }

                charging = ReadText(Path.Combine(supply, "status"))?.Trim() is { } status ? status is "Charging" or "Full" : null;
            }
        }

        return hasBattery ? new BatteryStatus(percent, charging, onAc) : null;
    }

    private static (long? Used, long? Total) Swap()
    {
        if (OperatingSystem.IsWindows())
        {
            return WindowsSystemNative.Commit() is { } commit ? (commit.Used, commit.Limit) : (null, null);
        }

        return OperatingSystem.IsLinux() && ParseSwap(ReadText("/proc/meminfo")) is { } swap ? (swap.Used, swap.Total) : (null, null);
    }

    /// <summary>The battery right now (null without one): Win32 on Windows, /sys/class/power_supply on Linux.</summary>
    public static BatteryStatus? Battery()
    {
        if (OperatingSystem.IsWindows())
        {
            return WindowsSystemNative.PowerStatus() is { Percent: not null } p ? new BatteryStatus(p.Percent, p.Charging, p.OnAc) : null;
        }

        return OperatingSystem.IsLinux() ? ParseLinuxPowerSupply("/sys/class/power_supply") : null;
    }

    private static string? RestartPending()
    {
        if (OperatingSystem.IsWindows())
        {
            return WindowsRestartPending();
        }

        return File.Exists("/var/run/reboot-required") ? "updated packages" : null;
    }

    [SupportedOSPlatform("windows")]
    private static string? WindowsRestartPending()
    {
        (string Key, string Reason)[] markers =
        [
            (@"SOFTWARE\Microsoft\Windows\CurrentVersion\WindowsUpdate\Auto Update\RebootRequired", "Windows Update"),
            (@"SOFTWARE\Microsoft\Windows\CurrentVersion\Component Based Servicing\RebootPending", "a Windows component update"),
        ];
        foreach (var (key, reason) in markers)
        {
            try
            {
                using var found = Registry.LocalMachine.OpenSubKey(key);
                if (found is not null)
                {
                    return reason;
                }
            }
            catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
            {
            }
        }

        return null;
    }

    private static (string? OperatingSystem, string? CpuModel) WindowsStatic()
    {
        if (!OperatingSystem.IsWindows())
        {
            return (null, null);
        }

        try
        {
            using var version = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion");
            using var cpu = Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\CentralProcessor\0");
            var build = int.TryParse(version?.GetValue("CurrentBuildNumber") as string, NumberStyles.Integer, CultureInfo.InvariantCulture, out var b) ? b : (int?)null;
            return (
                WindowsName(version?.GetValue("ProductName") as string, version?.GetValue("DisplayVersion") as string, build),
                cpu?.GetValue("ProcessorNameString") is string model ? Squeeze(model) : null);
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            return (null, null);
        }
    }

    private static string? UnixOperatingSystem() => ParseOsRelease(ReadText("/etc/os-release"));

    private static string? LinuxCpuModel() => OperatingSystem.IsLinux() ? ParseCpuModel(ReadText("/proc/cpuinfo")) : null;

    /// <summary>Collapses spaces and drops trademark marks: "Intel(R) Core(TM) i7-1165G7" → "Intel Core i7-1165G7".</summary>
    private static string Squeeze(string text) =>
        string.Join(' ', text.Replace("(R)", string.Empty, StringComparison.OrdinalIgnoreCase)
            .Replace("(TM)", string.Empty, StringComparison.OrdinalIgnoreCase)
            .Split(' ', StringSplitOptions.RemoveEmptyEntries));

    private static string? ReadText(string path)
    {
        try
        {
            return File.Exists(path) ? File.ReadAllText(path) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
