using System.Text.RegularExpressions;

namespace Pickle.Core.Modules;

/// <summary>Where the installer's module selection comes from; replaceable in tests.</summary>
public sealed record MachineModuleSources(
    string? ExecutableDirectory,
    string? EtcDirectory,
    string? Environment,
    Func<IEnumerable<string>> RegistryModules)
{
    public static MachineModuleSources System { get; } = new(
        Path.GetDirectoryName(global::System.Environment.ProcessPath),
        OperatingSystem.IsWindows() ? null : "/etc/pickle",
        global::System.Environment.GetEnvironmentVariable("PICKLE_MODULES"),
        ReadRegistry);

    private static IEnumerable<string> ReadRegistry() => OperatingSystem.IsWindows() ? ReadWindowsRegistry() : [];

    [global::System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static IEnumerable<string> ReadWindowsRegistry()
    {
        try
        {
            using var hklm = Microsoft.Win32.RegistryKey.OpenBaseKey(Microsoft.Win32.RegistryHive.LocalMachine, Microsoft.Win32.RegistryView.Registry64);
            using var key = hklm.OpenSubKey(@"Software\Pickle\Modules");
            if (key is null)
            {
                return [];
            }

            var enabled = new List<string>();
            foreach (var name in key.GetValueNames())
            {
                if (key.GetValue(name) is int and not 0)
                {
                    enabled.Add(name);
                }
            }

            return enabled;
        }
        catch (Exception ex) when (ex is global::System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            return [];
        }
    }
}

/// <summary>
/// The modules the installer selected for every user of the machine: MSI features write <c>HKLM\Software\Pickle\Modules</c>
/// values, RPM sub-packages drop marker files into <c>/etc/pickle/modules.d</c>, portable installs can put markers in a
/// <c>modules.d</c> folder next to the executable, and <c>PICKLE_MODULES=docker,nmap</c> works anywhere (containers, CI).
/// </summary>
public static partial class MachineModules
{
    public const string MarkerFolder = "modules.d";

    public static IReadOnlySet<string> Read(MachineModuleSources? sources = null)
    {
        sources ??= MachineModuleSources.System;
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        Add(ids, sources.RegistryModules());
        if (sources.Environment is { Length: > 0 } list)
        {
            Add(ids, list.Split([',', ';', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        }

        foreach (var root in new[] { sources.ExecutableDirectory, sources.EtcDirectory })
        {
            if (root is not null)
            {
                Add(ids, Markers(Path.Combine(root, MarkerFolder)));
            }
        }

        return ids;
    }

    public static bool IsValidId(string id) => ModuleId().IsMatch(id);

    private static void Add(HashSet<string> ids, IEnumerable<string> candidates)
    {
        foreach (var candidate in candidates)
        {
            if (IsValidId(candidate))
            {
                ids.Add(candidate.ToLowerInvariant());
            }
        }
    }

    private static IEnumerable<string> Markers(string directory)
    {
        try
        {
            return Directory.Exists(directory) ? [.. Directory.EnumerateFiles(directory).Select(f => Path.GetFileNameWithoutExtension(f))] : [];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    [GeneratedRegex("^[A-Za-z][A-Za-z0-9-]{0,39}$")]
    private static partial Regex ModuleId();
}
