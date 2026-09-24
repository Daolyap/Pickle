using System.Text.Json;
using System.Text.Json.Serialization;

namespace Pickle.Abstractions;

/// <summary>
/// Root of <c>config.json</c>. Property names serialize camelCase. Every section has safe defaults so a
/// missing or partial file is valid. Add new settings here (with defaults) and to Config/Schemas/config.schema.json.
/// </summary>
public sealed class PickleConfig
{
    public string Theme { get; set; } = "pickle";
    public EditorSettings Editor { get; set; } = new();
    public HistorySettings History { get; set; } = new();
    public PromptSettings Prompt { get; set; } = new();

    /// <summary>Chord (e.g. "Ctrl+R", "Alt+G", "F1") → action name. Merged over the built-in defaults.</summary>
    public Dictionary<string, string> KeyBindings { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public TranslationSettings Translation { get; set; } = new();
    public ShellSettings Shell { get; set; } = new();
    public PluginSettings Plugins { get; set; } = new();
    public SyncSettings Sync { get; set; } = new();
    public TerminalSettings Terminal { get; set; } = new();
    public WingetSettings Winget { get; set; } = new();

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
    public bool ShowStartupBanner { get; set; } = true;

    /// <summary>"animated" (art with a short shine; any key skips it), "art" or "line".</summary>
    public string BannerStyle { get; set; } = "animated";

    public bool FirstRunCompleted { get; set; } = false;
}

public sealed class PluginSettings
{
    public bool AutoLoad { get; set; } = true;
    public List<string> Disabled { get; set; } = [];

    /// <summary>SHA-256 hashes of .NET plugin assemblies the user has trusted.</summary>
    public List<string> TrustedAssemblies { get; set; } = [];
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
}

public sealed class WingetSettings
{
    public bool AutoInstallClientModule { get; set; } = false;
    public bool IncludeWindowsUpdatesInUpgrade { get; set; } = true;
    public bool IncludeUnknownVersions { get; set; } = false;
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
