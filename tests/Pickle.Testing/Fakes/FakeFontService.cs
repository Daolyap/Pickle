using Pickle.Abstractions.Services;

namespace Pickle.Testing.Fakes;

/// <summary>An <see cref="IFontService"/> with a settable font list and terminal; installs add the recommended font.</summary>
public sealed class FakeFontService : IFontService
{
    public event EventHandler? FontsChanged;

    public List<string> Installed { get; } = [];

    public string? CurrentTerminalFont { get; set; }

    public bool? HasNerdFont { get; set; }

    public FontInstallResult? InstallResult { get; set; }

    public int InstallCalls { get; private set; }

    public string RecommendedFont => "Cascadia Code NF";

    public bool IsInstalled(string family) => Installed.Contains(family.Trim(), StringComparer.OrdinalIgnoreCase);

    public IReadOnlyList<string> InstalledNerdFonts() => [.. Installed.Where(NerdFonts.IsNerdFontName)];

    public string? TerminalFont() => CurrentTerminalFont;

    public bool? TerminalHasNerdFont() => HasNerdFont;

    public Task<FontInstallResult> InstallRecommendedAsync(IProgress<string>? progress, CancellationToken cancellationToken)
    {
        InstallCalls++;
        progress?.Report("Downloading");
        var result = InstallResult ?? new FontInstallResult(true, $"Installed {RecommendedFont}.");
        if (result.Success)
        {
            Installed.Add(RecommendedFont);
            FontsChanged?.Invoke(this, EventArgs.Empty);
        }

        return Task.FromResult(result);
    }
}
