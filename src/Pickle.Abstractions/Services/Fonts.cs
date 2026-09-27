namespace Pickle.Abstractions.Services;

public sealed record FontInstallResult(bool Success, string Message);

/// <summary>
/// Installed fonts and whether the terminal Pickle runs in can draw Nerd Font glyphs (the private-use icons and
/// Powerline separators themes use). Windows only: the service is absent on other systems.
/// </summary>
public interface IFontService
{
    /// <summary>Raised after fonts were installed through this service.</summary>
    event EventHandler? FontsChanged;

    /// <summary>The Nerd Font Pickle installs and names in its Windows Terminal profile.</summary>
    string RecommendedFont { get; }

    /// <summary>A font family is installed for this user or machine (Terminal's bundled Cascadia fonts count).</summary>
    bool IsInstalled(string family);

    /// <summary>Installed families whose names mark them as Nerd Fonts ("… NF", "… Nerd Font").</summary>
    IReadOnlyList<string> InstalledNerdFonts();

    /// <summary>The font the current terminal draws with, when it can be found out; null otherwise.</summary>
    string? TerminalFont();

    /// <summary>Whether the current terminal draws Nerd Font glyphs; null when that cannot be told.</summary>
    bool? TerminalHasNerdFont();

    /// <summary>Downloads <see cref="RecommendedFont"/> from its official release and installs it for this user.</summary>
    Task<FontInstallResult> InstallRecommendedAsync(IProgress<string>? progress, CancellationToken cancellationToken);
}

public static class NerdFonts
{
    /// <summary>"Cascadia Code NF", "JetBrainsMono NFM", "FiraCode Nerd Font Mono", "CaskaydiaCove NF"…</summary>
    public static bool IsNerdFontName(string? family)
    {
        if (string.IsNullOrWhiteSpace(family))
        {
            return false;
        }

        if (family.Contains("Nerd Font", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        foreach (var word in family.Split([' ', '-', '_'], StringSplitOptions.RemoveEmptyEntries))
        {
            if (word is "NF" or "NFM" or "NFP")
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Nerd Font icons live in the Unicode private use areas (the BMP one and planes 15–16), where fonts without
    /// them draw a replacement box or "�".
    /// </summary>
    public static bool IsPrivateUse(int codePoint) =>
        codePoint is (>= 0xE000 and <= 0xF8FF) or (>= 0xF0000 and <= 0x10FFFD);
}
