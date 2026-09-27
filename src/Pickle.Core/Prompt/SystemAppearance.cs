using System.Globalization;
using System.Runtime.Versioning;
using Microsoft.Win32;

namespace Pickle.Core.Prompt;

/// <summary>Whether the desktop is in light mode, for the "auto" theme. Null when it can't be told.</summary>
public static class SystemAppearance
{
    public static bool? PrefersLight()
    {
        if (OperatingSystem.IsWindows())
        {
            return WindowsAppsUseLightTheme();
        }

        return FromColorFgBg(Environment.GetEnvironmentVariable("COLORFGBG"));
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
