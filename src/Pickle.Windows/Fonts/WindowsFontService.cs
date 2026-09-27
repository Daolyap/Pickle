using System.Runtime.Versioning;
using Pickle.Abstractions.Services;
using Pickle.Windows.Terminal;

namespace Pickle.Windows.Fonts;

/// <summary>
/// <see cref="IFontService"/> over the registered fonts, Windows Terminal's settings (for the font of the running
/// profile) and the console window's font. Every system access is a delegate so tests run on any OS.
/// </summary>
public sealed class WindowsFontService : IFontService, IDisposable
{
    private readonly Func<IReadOnlyList<string>> _registeredNames;
    private readonly WindowsTerminalLocations? _terminal;
    private readonly Func<string, string?> _environment;
    private readonly Func<string?> _consoleFace;
    private readonly Func<IReadOnlyList<(string Source, FontFileSpec Spec)>, IReadOnlyList<string>> _install;
    private readonly HttpClient _http;
    private readonly bool _ownsHttp;
    private FontCatalog? _catalog;

    public WindowsFontService(
        Func<IReadOnlyList<string>> registeredNames,
        WindowsTerminalLocations? terminal,
        Func<string, string?> environment,
        Func<string?> consoleFace,
        Func<IReadOnlyList<(string Source, FontFileSpec Spec)>, IReadOnlyList<string>> install,
        HttpClient? http = null)
    {
        _registeredNames = registeredNames;
        _terminal = terminal;
        _environment = environment;
        _consoleFace = consoleFace;
        _install = install;
        _ownsHttp = http is null;
        _http = http ?? CreateHttpClient();
    }

    [SupportedOSPlatform("windows")]
    public static WindowsFontService ForCurrentSystem() => new(
        InstalledFonts.RegisteredNames,
        WindowsTerminalLocations.ForCurrentUser(),
        Environment.GetEnvironmentVariable,
        InstalledFonts.ConsoleFontFace,
        InstalledFonts.InstallForCurrentUser);

    public event EventHandler? FontsChanged;

    public string RecommendedFont => CascadiaCodeNerdFont.Family;

    private FontCatalog Catalog => _catalog ??= new FontCatalog(_registeredNames());

    public bool IsInstalled(string family) =>
        FontCatalog.TerminalBundled.Contains(family.Trim(), StringComparer.OrdinalIgnoreCase) || Catalog.Contains(family);

    public IReadOnlyList<string> InstalledNerdFonts() => Catalog.NerdFontFamilies();

    public string? TerminalFont()
    {
        if (_terminal is not null && TerminalFontProbe.WindowsTerminalFace(_terminal, _environment) is { } face)
        {
            return face;
        }

        // Other terminals (VS Code, WezTerm, …) announce themselves; the console font API would describe a hidden
        // pseudo-console there, not what is on screen.
        return _environment("TERM_PROGRAM") is { Length: > 0 } ? null : _consoleFace();
    }

    public bool? TerminalHasNerdFont()
    {
        if (TerminalFont() is { } face)
        {
            return face.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Any(f => NerdFonts.IsNerdFontName(f) && IsInstalled(f));
        }

        // WezTerm falls back to its built-in Nerd Font symbols whatever the configured font.
        return string.Equals(_environment("TERM_PROGRAM"), "WezTerm", StringComparison.OrdinalIgnoreCase) ? true : null;
    }

    public async Task<FontInstallResult> InstallRecommendedAsync(IProgress<string>? progress, CancellationToken cancellationToken)
    {
        var work = Path.Combine(Path.GetTempPath(), "pickle-font-" + Guid.NewGuid().ToString("N"));
        try
        {
            var files = await FontDownload.ExtractAsync(_http, CascadiaCodeNerdFont.Archive, CascadiaCodeNerdFont.Files, work, progress, cancellationToken)
                .ConfigureAwait(false);
            progress?.Report("Installing for the current user");
            var installed = _install([.. files.Zip(CascadiaCodeNerdFont.Files)]);
            _catalog = null;
            FontsChanged?.Invoke(this, EventArgs.Empty);
            return new FontInstallResult(true, $"Installed {RecommendedFont} {CascadiaCodeNerdFont.Version} for this user ({installed.Count} files).");
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or InvalidDataException or UnauthorizedAccessException or TaskCanceledException)
        {
            return new FontInstallResult(false, $"Could not install {RecommendedFont}: {ex.Message}");
        }
        finally
        {
            try
            {
                if (Directory.Exists(work))
                {
                    Directory.Delete(work, recursive: true);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }
    }

    public void Dispose()
    {
        if (_ownsHttp)
        {
            _http.Dispose();
        }
    }

    private static HttpClient CreateHttpClient()
    {
        var http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("Pickle");
        return http;
    }
}
