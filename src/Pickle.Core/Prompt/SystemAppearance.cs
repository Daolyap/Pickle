using System.Globalization;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32;

namespace Pickle.Core.Prompt;

/// <summary>
/// What the desktop asks for: light or dark mode (the "auto" theme) and whether animations are wanted
/// (<c>prompt.animation</c> "auto"). Null when it can't be told.
/// </summary>
public static partial class SystemAppearance
{
    private const uint SpiGetClientAreaAnimation = 0x1042;
    private static (long At, bool? Value) _animations = (long.MinValue, null);

    public static bool? PrefersLight()
    {
        if (OperatingSystem.IsWindows())
        {
            return WindowsAppsUseLightTheme();
        }

        return FromColorFgBg(Environment.GetEnvironmentVariable("COLORFGBG"));
    }

    /// <summary>
    /// Windows' "Show animations in Windows" (Accessibility → Visual effects), read at most every few seconds because
    /// the line editor asks on every idle tick. Null elsewhere.
    /// </summary>
    public static bool? AnimationsEnabled()
    {
        if (!OperatingSystem.IsWindows())
        {
            return null;
        }

        var now = Environment.TickCount64;
        var cached = _animations;
        if (now - cached.At < 5000)
        {
            return cached.Value;
        }

        bool? value = SystemParametersInfo(SpiGetClientAreaAnimation, 0, out var enabled, 0) ? enabled != 0 : null;
        _animations = (now, value);
        return value;
    }

    /// <summary>
    /// COLORFGBG ("15;0", "default;default;0") is set by rxvt, Konsole and others: the last field is the background's
    /// ANSI index, and white (7) or bright white (15) means a light terminal.
    /// </summary>
    public static bool? FromColorFgBg(string? value) =>
        value?.Split(';').LastOrDefault() is { } background
        && int.TryParse(background, NumberStyles.Integer, CultureInfo.InvariantCulture, out var index)
            ? index is 7 or 15
            : null;

    [LibraryImport("user32.dll", EntryPoint = "SystemParametersInfoW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    [SupportedOSPlatform("windows")]
    private static partial bool SystemParametersInfo(uint action, uint param, out int value, uint winIni);

    [SupportedOSPlatform("windows")]
    private static bool? WindowsAppsUseLightTheme()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("AppsUseLightTheme") is int value ? value != 0 : null;
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            return null;
        }
    }
}
