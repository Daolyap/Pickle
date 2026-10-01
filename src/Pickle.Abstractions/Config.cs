using System.Text.Json;
using System.Text.Json.Serialization;

namespace Pickle.Abstractions;

/// <summary>
/// Root of <c>config.json</c>. Property names serialize camelCase. Every section has safe defaults so a
/// missing or partial file is valid. Add new settings here (with defaults) and to Config/Schemas/config.schema.json.
/// </summary>
public sealed class PickleConfig
{
    /// <summary>A theme name, or "auto" to follow the system's light/dark mode with <see cref="LightTheme"/> and <see cref="DarkTheme"/>.</summary>
    public string Theme { get; set; } = "pickle";

    /// <summary>Theme used by "auto" while the system is in light mode.</summary>
    public string LightTheme { get; set; } = "solarized-light";

    /// <summary>Theme used by "auto" while the system is in dark mode (or when it can't be told).</summary>
    public string DarkTheme { get; set; } = "pickle";
    public EditorSettings Editor { get; set; } = new();
    public HistorySettings History { get; set; } = new();
    public PromptSettings Prompt { get; set; } = new();

    /// <summary>Chord (e.g. "Ctrl+R", "Alt+G", "F1") → action name. Merged over the built-in defaults.</summary>
    public Dictionary<string, string> KeyBindings { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public TranslationSettings Translation { get; set; } = new();
    public ShellSettings Shell { get; set; } = new();
    public PluginSettings Plugins { get; set; } = new();

    /// <summary>Which optional modules (Docker, Kubernetes, …) are on for this user, on top of what the installer selected.</summary>
    public ModuleSettings Modules { get; set; } = new();
    public SyncSettings Sync { get; set; } = new();
    public TerminalSettings Terminal { get; set; } = new();
    public WingetSettings Winget { get; set; } = new();

    /// <summary>The <c>weather</c> prompt segment: where, which units, how often. Off until enabled.</summary>
    public WeatherSettings Weather { get; set; } = new();

    /// <summary>The <c>music</c> prompt segment (what is playing). Off until enabled.</summary>
    public MusicSettings Music { get; set; } = new();

    /// <summary>The <c>battery</c> prompt segment's defaults (a theme's own segment options win).</summary>
    public BatterySettings Battery { get; set; } = new();

    /// <summary>Free-form settings owned by plugins, keyed by plugin id.</summary>
    public Dictionary<string, JsonElement> Extensions { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

public sealed class EditorSettings
{
    public bool SyntaxHighlighting { get; set; } = true;
    public bool Autosuggestions { get; set; } = true;
    public bool CompletionMenu { get; set; } = true;
    public int CompletionMenuMaxRows { get; set; } = 10;
    public bool ShowTranslatedCommand { get; set; } = true;

    /// <summary>"none", "audible" or "visual".</summary>
    public string BellStyle { get; set; } = "none";

    /// <summary>Inputs longer than this are highlighted with a debounce instead of on every key.</summary>
    public int HighlightDebounceThreshold { get; set; } = 4000;
}

public sealed class HistorySettings
{
    public int MaxEntries { get; set; } = 50_000;
    public bool IgnoreLeadingSpace { get; set; } = true;
    public bool FilterSecrets { get; set; } = true;
    public bool IgnoreDuplicates { get; set; } = true;
}

public sealed class PromptSettings
{
    public bool TransientPrompt { get; set; } = true;
    public bool NewlineBeforePrompt { get; set; } = false;

    /// <summary>Show the duration segment only when the last command took at least this long.</summary>
    public int DurationThresholdMs { get; set; } = 2000;

    public int GitTimeoutMs { get; set; } = 400;

    /// <summary>
    /// "auto" (Nerd Font icons when the terminal's font has them), "nerd" or "unicode" (no private-use glyphs, so no
    /// "�" boxes with ordinary fonts; Powerline separators become plain blocks).
    /// </summary>
    public string Icons { get; set; } = "auto";

    /// <summary>
    /// Animated themes: "auto" (animate, except over SSH, with NO_COLOR/TERM=dumb, or with Windows' "Show animations" off),
    /// "on" or "off" (the theme's own colors, still).
    /// Animation pauses after a few minutes without a key press.
    /// </summary>
    public string Animation { get; set; } = "auto";
}

public sealed class TranslationSettings
{
    public bool Enabled { get; set; } = true;

    /// <summary>If a real executable with the same name is on PATH (e.g. from Git for Windows), use it instead of the shim.</summary>
    public bool PreferNativeBinaries { get; set; } = false;

    /// <summary>Shim names to disable, e.g. ["ls", "cat"].</summary>
    public List<string> Disabled { get; set; } = [];
}

public sealed class ShellSettings
{
    public bool LoadPwshProfile { get; set; } = false;

    /// <summary>Report <c>$Host.Name</c> as "ConsoleHost" for modules that check it.</summary>
    public bool HostCompatibility { get; set; } = false;

    public bool CommandNotFoundSuggestions { get; set; } = true;
    public bool OfferWingetInstallForMissingTools { get; set; } = true;

    /// <summary>Before running a line whose program is missing but installable (7z, nmap, …), ask to install it first.</summary>
    public bool AskToInstallMissingTools { get; set; } = true;

    /// <summary>Default of the "add to PATH" choice when installing a missing tool.</summary>
    public bool AddInstalledToolsToPath { get; set; } = true;
    public bool ShowStartupBanner { get; set; } = true;

    /// <summary>"animated" (art with a short shine; any key skips it), "art" or "line".</summary>
    public string BannerStyle { get; set; } = "animated";

    /// <summary>Theme for sessions running as administrator (not saved as <see cref="PickleConfig.Theme"/>); "none" keeps the normal one.</summary>
    public string AdminTheme { get; set; } = "admin";

    /// <summary>When a command fails because it needs administrator rights, offer to run it again elevated (Windows).</summary>
    public bool OfferElevation { get; set; } = true;

    /// <summary>Look for a newer Pickle release once a day (in the background; the banner says when there is one).</summary>
    public bool CheckForUpdates { get; set; } = true;

    /// <summary>Variables Pickle sets for every session it starts (edit them with <c>pk env</c> or the environment panel); <c>%NAME%</c> and <c>$env:NAME</c> are expanded.</summary>
    public Dictionary<string, string> Environment { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Folders Pickle puts at the front of PATH for every session it starts (the PATH editor's "Pickle" scope).</summary>
    public List<string> PathPrepend { get; set; } = [];

    public bool FirstRunCompleted { get; set; } = false;

    /// <summary>The newest setup whose questions were asked (see <c>pk setup</c>); newer offers are asked once.</summary>
    public int SetupVersion { get; set; } = 0;
}

public sealed class PluginSettings
{
    public bool AutoLoad { get; set; } = true;
    public List<string> Disabled { get; set; } = [];

    /// <summary>SHA-256 hashes of .NET plugin assemblies the user has trusted.</summary>
    public List<string> TrustedAssemblies { get; set; } = [];
}

/// <summary>
/// The user's choice of optional modules. The installer's selection (MSI features, RPM sub-packages, <c>modules.d</c>
/// markers) is the starting point: <see cref="Enabled"/> adds to it and <see cref="Disabled"/> removes from it.
/// </summary>
public sealed class ModuleSettings
{
    /// <summary>Module ids turned on for this user (<c>pk module enable docker</c>).</summary>
    public List<string> Enabled { get; set; } = [];

    /// <summary>Module ids turned off for this user even when the installer selected them.</summary>
    public List<string> Disabled { get; set; } = [];

    /// <summary>Ask which optional modules to turn on at the first start and in <c>pk setup</c>.</summary>
    public bool AskAtSetup { get; set; } = true;
}

public sealed class SyncSettings
{
    /// <summary>"none", "folder" or "git".</summary>
    public string Backend { get; set; } = "none";

    /// <summary>Folder path (folder backend) or remote URL (git backend).</summary>
    public string? Target { get; set; }

    public bool AutoSyncOnStart { get; set; } = false;
    public bool AutoSyncOnExit { get; set; } = false;
    public bool SyncHistory { get; set; } = true;
}

public sealed class TerminalSettings
{
    public string? FontFace { get; set; } = "Cascadia Code NF";
    public double? FontSize { get; set; } = 12;
    public int? Opacity { get; set; } = 95;
    public bool UseAcrylic { get; set; } = false;

    /// <summary>"bar", "vintage", "underscore", "filledBox", "emptyBox", "doubleUnderscore".</summary>
    public string CursorShape { get; set; } = "bar";

    public string? BackgroundImage { get; set; }
    public double? BackgroundImageOpacity { get; set; }
    public string? Padding { get; set; } = "8";

    /// <summary>
    /// Progress ring on the terminal tab and taskbar (OSC 9;4) while a command runs, with Write-Progress percentages and
    /// red after a failure: "auto" (Windows Terminal and ConEmu), "on" or "off".
    /// </summary>
    public string TabProgress { get; set; } = "auto";

    /// <summary>Ring the bell when a command that ran at least this many seconds finishes (0: never). Windows Terminal flashes its taskbar button.</summary>
    public int BellAfterSeconds { get; set; } = 0;
}

public sealed class WingetSettings
{
    public bool AutoInstallClientModule { get; set; } = false;
    public bool IncludeWindowsUpdatesInUpgrade { get; set; } = true;
    public bool IncludeUnknownVersions { get; set; } = false;
}

/// <summary>
/// Weather for the <c>weather</c> prompt segment, from Open-Meteo (no account or key). Turning it on sends the place name
/// or coordinates below to open-meteo.com (and, for "auto", your IP address to ipwho.is) every <see cref="RefreshMinutes"/>.
/// </summary>
public sealed class WeatherSettings
{
    public bool Enabled { get; set; } = false;

    /// <summary>A place name ("Paris", "Portland, Oregon"), coordinates ("48.85,2.35"), or "auto" to look it up from your IP address.</summary>
    public string Location { get; set; } = string.Empty;

    /// <summary>"auto" (follows the system region), "metric" (°C, km/h) or "imperial" (°F, mph).</summary>
    public string Units { get; set; } = "auto";

    public int RefreshMinutes { get; set; } = 30;

    /// <summary>Placeholders: {icon} {temp} {unit} {feels} {condition} {wind} {city}.</summary>
    public string Format { get; set; } = "{icon} {temp}{unit}";
}

/// <summary>What is playing, for the <c>music</c> prompt segment: Windows media sessions, MPRIS (playerctl) on Linux, Music and Spotify on macOS.</summary>
public sealed class MusicSettings
{
    public bool Enabled { get; set; } = false;

    /// <summary>"auto", or a player name (<c>spotify</c>, <c>vlc</c>, <c>music</c>) to follow only that one.</summary>
    public string Player { get; set; } = "auto";

    public int MaxLength { get; set; } = 40;

    public bool HideWhenPaused { get; set; } = true;

    /// <summary>How often the player is asked (seconds; background jobs tick every 15 s, so lower values behave like 15).</summary>
    public int PollSeconds { get; set; } = 15;

    /// <summary>Placeholders: {artist} {title} {player} {state}.</summary>
    public string Format { get; set; } = "♪ {artist} – {title}";
}

public sealed class BatterySettings
{
    /// <summary>Hide the segment while on AC power at or above this charge (100: always show it on a laptop).</summary>
    public int HideAbove { get; set; } = 95;

    /// <summary>At or below this charge (not charging) the segment turns red.</summary>
    public int Low { get; set; } = 15;

    /// <summary>Shown after the percentage while charging.</summary>
    public string ChargingMarker { get; set; } = "↑";
}

public sealed class ConfigChangedEventArgs(PickleConfig config, string? path) : EventArgs
{
    public PickleConfig Config { get; } = config;

    /// <summary>Dotted path that changed (camelCase), or null for a full reload.</summary>
    public string? Path { get; } = path;
}

public interface IConfigStore
{
    PickleConfig Current { get; }

    event EventHandler<ConfigChangedEventArgs>? Changed;

    /// <summary>Mutate and persist.</summary>
    void Update(Action<PickleConfig> mutate);

    /// <summary>Read a value by dotted camelCase path, e.g. <c>editor.autosuggestions</c>.</summary>
    JsonElement? GetValue(string path);

    /// <summary>Set a value by dotted path; <paramref name="value"/> is parsed as JSON, falling back to a string.</summary>
    void SetValue(string path, string value);

    void Reload();

    void Save();
}

public static class PickleJson
{
    public static JsonSerializerOptions Options { get; } = Create(indented: true);

    public static JsonSerializerOptions Compact { get; } = Create(indented: false);

    private static JsonSerializerOptions Create(bool indented) => new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DictionaryKeyPolicy = null,
        WriteIndented = indented,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };
}
