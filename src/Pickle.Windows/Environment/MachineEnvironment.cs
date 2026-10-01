using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32;

namespace Pickle.Windows;

/// <summary>
/// The persistent environment in the registry: HKLM for all users, HKCU for the current one. Values holding <c>%VAR%</c>
/// references stay REG_EXPAND_SZ (<c>Environment.SetEnvironmentVariable</c> would flatten them to REG_SZ), and
/// WM_SETTINGCHANGE tells Explorer and new programs to re-read.
/// </summary>
[SupportedOSPlatform("windows")]
internal static partial class MachineEnvironment
{
    public const string MachineKey = @"SYSTEM\CurrentControlSet\Control\Session Manager\Environment";
    public const string UserKey = "Environment";

    public static IReadOnlyList<(string Name, string Value, bool Expandable)> Read(bool machine)
    {
        using var root = RegistryKey.OpenBaseKey(machine ? RegistryHive.LocalMachine : RegistryHive.CurrentUser, RegistryView.Registry64);
        using var key = root.OpenSubKey(machine ? MachineKey : UserKey);
        if (key is null)
        {
            return [];
        }

        var list = new List<(string, string, bool)>();
        foreach (var name in key.GetValueNames().Where(n => n.Length > 0).Order(StringComparer.OrdinalIgnoreCase))
        {
            var kind = key.GetValueKind(name);
            if (kind is RegistryValueKind.String or RegistryValueKind.ExpandString
                && key.GetValue(name, string.Empty, RegistryValueOptions.DoNotExpandEnvironmentNames) is string value)
            {
                list.Add((name, value, kind == RegistryValueKind.ExpandString));
            }
        }

        return list;
    }

    public static void Set(string name, string? value, bool machine = true)
    {
        using var root = RegistryKey.OpenBaseKey(machine ? RegistryHive.LocalMachine : RegistryHive.CurrentUser, RegistryView.Registry64);
        using var key = root.OpenSubKey(machine ? MachineKey : UserKey, writable: true)
            ?? throw new IOException("The environment key could not be opened for writing.");
        if (value is null)
        {
            key.DeleteValue(name, throwOnMissingValue: false);
        }
        else
        {
            var kind = value.Contains('%', StringComparison.Ordinal) || string.Equals(name, "Path", StringComparison.OrdinalIgnoreCase)
                ? RegistryValueKind.ExpandString
                : RegistryValueKind.String;
            key.SetValue(name, value, kind);
        }

        Broadcast();
    }

    private static void Broadcast()
    {
        const uint HwndBroadcast = 0xFFFF;
        const uint WmSettingChange = 0x001A;
        const uint SmtoAbortIfHung = 0x0002;
        _ = SendMessageTimeout(new IntPtr(HwndBroadcast), WmSettingChange, UIntPtr.Zero, "Environment", SmtoAbortIfHung, 5000, out _);
    }

    [LibraryImport("user32.dll", EntryPoint = "SendMessageTimeoutW", StringMarshalling = StringMarshalling.Utf16)]
    private static partial IntPtr SendMessageTimeout(IntPtr hWnd, uint msg, UIntPtr wParam, string lParam, uint flags, uint timeout, out UIntPtr result);
}
