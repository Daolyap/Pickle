using System.Globalization;
using System.Text;
using Pickle.Abstractions;

namespace Pickle.Windows.Terminal;

/// <summary>Where Windows Terminal reads fragments and settings. Tests point this at a temp folder.</summary>
public sealed record WindowsTerminalLocations(string FragmentDirectory, IReadOnlyList<string> SettingsFiles)
{
    public const string FragmentFileName = "pickle.json";

    public string FragmentFile => Path.Combine(FragmentDirectory, FragmentFileName);

    /// <summary>Per-user locations under %LOCALAPPDATA%: stable and Preview Store packages, then unpackaged installs.</summary>
    public static WindowsTerminalLocations FromLocalAppData(string localAppData) => new(
        Path.Combine(localAppData, "Microsoft", "Windows Terminal", "Fragments", WindowsTerminalFragment.AppName),
        [
            Path.Combine(localAppData, "Packages", "Microsoft.WindowsTerminal_8wekyb3d8bbwe", "LocalState", "settings.json"),
            Path.Combine(localAppData, "Packages", "Microsoft.WindowsTerminalPreview_8wekyb3d8bbwe", "LocalState", "settings.json"),
            Path.Combine(localAppData, "Microsoft", "Windows Terminal", "settings.json"),
        ]);

    /// <summary>The current user's locations, or null when not on Windows.</summary>
    public static WindowsTerminalLocations? ForCurrentUser() =>
        OperatingSystem.IsWindows() ? FromLocalAppData(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData)) : null;
}

public sealed record DefaultProfileResult(string SettingsFile, bool Changed, string? BackupFile, string? Error);

/// <summary>Installs, updates and removes the fragment and edits <c>defaultProfile</c> for one set of locations.</summary>
public sealed class WindowsTerminalManager(WindowsTerminalLocations locations, Func<DateTimeOffset>? clock = null)
{
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);
    private readonly object _gate = new();

    public WindowsTerminalLocations Locations => locations;

    public bool IsInstalled => File.Exists(locations.FragmentFile);

    /// <summary>The executable the installed profile launches, or null.</summary>
    public string? InstalledExecutable => IsInstalled ? WindowsTerminalFragment.ReadExecutable(ReadText(locations.FragmentFile)) : null;

    /// <summary>Writes the fragment. Returns false when the file already had exactly this content.</summary>
    public bool Install(string executablePath, TerminalSettings settings, TerminalPalette palette)
    {
        lock (_gate)
        {
            Directory.CreateDirectory(locations.FragmentDirectory);
            return WriteIfChanged(locations.FragmentFile, WindowsTerminalFragment.Build(executablePath, settings, palette));
        }
    }

    /// <summary>Regenerates an installed fragment (keeping its executable). Does nothing when not installed.</summary>
    public bool Update(TerminalSettings settings, TerminalPalette palette)
    {
        lock (_gate)
        {
            if (!IsInstalled || InstalledExecutable is not { } exe)
            {
                return false;
            }

            return WriteIfChanged(locations.FragmentFile, WindowsTerminalFragment.Build(exe, settings, palette));
        }
    }

    public bool Uninstall()
    {
        lock (_gate)
        {
            if (!IsInstalled)
            {
                return false;
            }

            File.Delete(locations.FragmentFile);
            if (Directory.Exists(locations.FragmentDirectory) && !Directory.EnumerateFileSystemEntries(locations.FragmentDirectory).Any())
            {
                Directory.Delete(locations.FragmentDirectory);
            }

            return true;
        }
    }

    /// <summary>Settings files that exist, with whether Pickle is their default profile.</summary>
    public IReadOnlyList<(string Path, bool IsDefault)> SettingsStatus() =>
    [
        .. locations.SettingsFiles.Where(File.Exists).Select(path =>
        {
            var current = TryRead(path) is { } text ? SafeGetDefault(text) : null;
            return (path, string.Equals(current, WindowsTerminalFragment.ProfileGuidString, StringComparison.OrdinalIgnoreCase));
        }),
    ];

    /// <summary>Points <c>defaultProfile</c> of every existing settings.json at the Pickle profile, backing each up first.</summary>
    public IReadOnlyList<DefaultProfileResult> SetDefaultProfile()
    {
        var results = new List<DefaultProfileResult>();
        foreach (var path in locations.SettingsFiles.Where(File.Exists))
        {
            try
            {
                var bytes = File.ReadAllBytes(path);
                var hasBom = bytes.AsSpan().StartsWith(Encoding.UTF8.Preamble);
                var text = Utf8NoBom.GetString(hasBom ? bytes.AsSpan(3) : bytes);
                var current = JsoncEditor.GetRootString(text, "defaultProfile");
                if (string.Equals(current, WindowsTerminalFragment.ProfileGuidString, StringComparison.OrdinalIgnoreCase))
                {
                    results.Add(new DefaultProfileResult(path, false, null, null));
                    continue;
                }

                var updated = JsoncEditor.SetRootString(text, "defaultProfile", WindowsTerminalFragment.ProfileGuidString);
                var stamp = (clock?.Invoke() ?? DateTimeOffset.Now).ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
                var backup = path + ".pickle-" + stamp + ".bak";
                File.Copy(path, backup, overwrite: true);
                WriteAtomic(path, updated, hasBom ? new UTF8Encoding(encoderShouldEmitUTF8Identifier: true) : Utf8NoBom);
                results.Add(new DefaultProfileResult(path, true, backup, null));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or FormatException)
            {
                results.Add(new DefaultProfileResult(path, false, null, ex.Message));
            }
        }

        return results;
    }

    private static string? SafeGetDefault(string text)
    {
        try
        {
            return JsoncEditor.GetRootString(text, "defaultProfile");
        }
        catch (FormatException)
        {
            return null;
        }
    }

    private static string ReadText(string path) => File.ReadAllText(path, Encoding.UTF8);

    private static string? TryRead(string path)
    {
        try
        {
            return ReadText(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static bool WriteIfChanged(string path, string content)
    {
        if (File.Exists(path) && TryRead(path) == content)
        {
            return false;
        }

        WriteAtomic(path, content, Utf8NoBom);
        return true;
    }

    private static void WriteAtomic(string path, string content, Encoding encoding)
    {
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, content, encoding);
        File.Move(tmp, path, overwrite: true);
    }
}
