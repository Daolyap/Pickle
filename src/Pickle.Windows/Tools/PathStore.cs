using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32;

namespace Pickle.Windows.Tools;

/// <summary>The process PATH plus the persistent (registry) PATH values. Tests substitute an in-memory store.</summary>
public interface IPathStore
{
    string ProcessPath { get; set; }

    /// <summary>Machine then user PATH from the registry, variables expanded.</summary>
    IReadOnlyList<string> PersistentEntries();

    /// <summary>Appends a folder to the current user's persistent PATH.</summary>
    void AppendToUserPath(string directory);

    string Expand(string text);
}

/// <summary>
/// The real PATH on Windows. The user PATH is edited through the registry directly, keeping it REG_EXPAND_SZ:
/// <see cref="Environment.SetEnvironmentVariable(string, string, EnvironmentVariableTarget)"/> would write REG_SZ and
/// freeze entries like <c>%USERPROFILE%\bin</c>.
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed partial class RegistryPathStore : IPathStore
{
    private const string UserKey = "Environment";
    private const string MachineKey = @"SYSTEM\CurrentControlSet\Control\Session Manager\Environment";

    public string ProcessPath
    {
        get => Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        set => Environment.SetEnvironmentVariable("PATH", value);
    }

    public IReadOnlyList<string> PersistentEntries() =>
        [.. Split(Read(Registry.LocalMachine, MachineKey)), .. Split(Read(Registry.CurrentUser, UserKey))];

    public void AppendToUserPath(string directory)
    {
        using var key = Registry.CurrentUser.CreateSubKey(UserKey, writable: true);
        var current = key.GetValue("Path", string.Empty, RegistryValueOptions.DoNotExpandEnvironmentNames) as string ?? string.Empty;
        var kind = key.GetValueNames().Contains("Path", StringComparer.OrdinalIgnoreCase) ? key.GetValueKind("Path") : RegistryValueKind.ExpandString;
        var value = current.Length == 0 ? directory : current.TrimEnd(';') + ";" + directory;
        key.SetValue("Path", value, kind == RegistryValueKind.String ? RegistryValueKind.String : RegistryValueKind.ExpandString);
        NativeMethods.BroadcastEnvironmentChange();
    }

    public string Expand(string text) => Environment.ExpandEnvironmentVariables(text);

    private static string Read(RegistryKey root, string path)
    {
        using var key = root.OpenSubKey(path);
        return key?.GetValue("Path", string.Empty) as string ?? string.Empty;
    }

    private static IEnumerable<string> Split(string value) =>
        value.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static partial class NativeMethods
    {
        private const nint HwndBroadcast = 0xffff;
        private const uint WmSettingChange = 0x001A;
        private const uint SmtoAbortIfHung = 0x0002;

        /// <summary>Lets Explorer (and so newly started programs) see the new PATH without signing out.</summary>
        public static void BroadcastEnvironmentChange() =>
            _ = SendMessageTimeout(HwndBroadcast, WmSettingChange, 0, "Environment", SmtoAbortIfHung, 2000, out _);

        [LibraryImport("user32.dll", EntryPoint = "SendMessageTimeoutW", StringMarshalling = StringMarshalling.Utf16)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        private static partial nint SendMessageTimeout(nint hWnd, uint msg, nint wParam, string lParam, uint flags, uint timeout, out nint result);
    }
}
