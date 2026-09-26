using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Pickle.Abstractions;
using Pickle.Windows.Terminal;

namespace Pickle.Windows.Uninstall;

/// <summary>
/// Deletes everything Pickle keeps for the current user: settings, history, themes, plugins and aliases (config
/// folder), logs, caches and sandbox files (data folder), and the per-user Windows Terminal profile (with
/// <c>defaultProfile</c> pointed back away from Pickle). Installed fonts stay: other programs may use them.
/// </summary>
public static class UserDataRemover
{
    /// <summary>What <see cref="Remove"/> would delete that exists now.</summary>
    public static IReadOnlyList<string> Targets(PicklePaths paths, WindowsTerminalLocations? terminal) =>
    [
        .. new[] { paths.ConfigDir, paths.DataDir, terminal?.FragmentDirectory }
            .OfType<string>()
            .Where(p => Directory.Exists(p) && IsSafeToDelete(p))
            .Distinct(StringComparer.OrdinalIgnoreCase),
    ];

    public static int Remove(PicklePaths paths, WindowsTerminalLocations? terminal, TextWriter output)
    {
        var failed = false;
        if (terminal is not null)
        {
            foreach (var result in new WindowsTerminalManager(terminal).RestoreDefaultProfile())
            {
                output.WriteLine(result.Error is null
                    ? $"Windows Terminal's default profile no longer points at Pickle ({result.SettingsFile})."
                    : $"Could not change the default profile in {result.SettingsFile}: {result.Error}");
            }
        }

        foreach (var target in Targets(paths, terminal))
        {
            try
            {
                Directory.Delete(target, recursive: true);
                output.WriteLine("Deleted " + target);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                failed = true;
                output.WriteLine($"Could not delete {target}: {ex.Message}");
            }
        }

        return failed ? 1 : 0;
    }

    /// <summary>Never a drive root or one of the user's own top-level folders, whatever PICKLE_HOME says.</summary>
    public static bool IsSafeToDelete(string path)
    {
        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        if (full.Length <= 3 || string.Equals(full, Path.TrimEndingDirectorySeparator(Path.GetPathRoot(full) ?? string.Empty), StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var protectedFolders = new[]
        {
            Environment.SpecialFolder.UserProfile, Environment.SpecialFolder.ApplicationData, Environment.SpecialFolder.LocalApplicationData,
            Environment.SpecialFolder.CommonApplicationData, Environment.SpecialFolder.MyDocuments, Environment.SpecialFolder.Desktop,
            Environment.SpecialFolder.ProgramFiles, Environment.SpecialFolder.Windows,
        };
        return !protectedFolders
            .Select(Environment.GetFolderPath)
            .Where(f => f.Length > 0)
            .Any(f => string.Equals(Path.TrimEndingDirectorySeparator(f), full, StringComparison.OrdinalIgnoreCase));
    }
}

/// <summary>The yes/no question an uninstall without the installer's own dialogs asks (Settings → Apps → Uninstall).</summary>
[SupportedOSPlatform("windows")]
public static partial class UninstallPrompt
{
    private const uint MbYesNo = 0x04;
    private const uint MbIconQuestion = 0x20;
    private const uint MbDefButton2 = 0x100;
    private const uint MbSetForeground = 0x10000;
    private const uint MbTopmost = 0x40000;
    private const int IdYes = 6;

    public static int Run(PicklePaths paths, WindowsTerminalLocations? terminal, TextWriter output)
    {
        var targets = UserDataRemover.Targets(paths, terminal);
        if (targets.Count == 0)
        {
            return 0;
        }

        var text = "Also delete your Pickle settings, history, themes, plugins and logs?\n\n"
            + string.Join("\n", targets)
            + "\n\nChoose No to keep them for a later install.";
        return MessageBox(0, text, "Uninstall Pickle", MbYesNo | MbIconQuestion | MbDefButton2 | MbSetForeground | MbTopmost) == IdYes
            ? UserDataRemover.Remove(paths, terminal, output)
            : 0;
    }

    [LibraryImport("user32.dll", EntryPoint = "MessageBoxW", StringMarshalling = StringMarshalling.Utf16)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial int MessageBox(nint owner, string text, string caption, uint type);
}
