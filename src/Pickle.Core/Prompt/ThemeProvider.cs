using System.Reflection;
using System.Text.Json;
using Pickle.Abstractions;

namespace Pickle.Core.Prompt;

/// <summary>
/// Loads themes from the user's themes folder first, then the embedded built-ins (themes/*.json). An elevated session
/// shows <c>shell.adminTheme</c> instead of the configured theme, without saving it, until a theme is applied. The theme
/// "auto" shows <c>lightTheme</c> or <c>darkTheme</c> by the system's appearance, checked again at every prompt.
/// </summary>
public sealed class ThemeProvider : IThemeProvider
{
    public const string Auto = "auto";
    private const string ResourcePrefix = "Pickle.Themes.";
    private readonly PicklePaths _paths;
    private readonly IConfigStore _config;
    private readonly IPickleLogger _log;
    private readonly bool _elevated;
    private Theme _current;
    private bool _adminOverride;

    public ThemeProvider(PicklePaths paths, IConfigStore config, IPickleLogger log, bool elevated = false, Func<bool?>? prefersLight = null)
    {
        _paths = paths;
        _config = config;
        _log = log;
        _elevated = elevated;
        PrefersLight = prefersLight ?? SystemAppearance.PrefersLight;
        var admin = AdminTheme(config.Current);
        _adminOverride = admin is not null;
        _current = admin ?? Configured(config.Current) ?? Load("pickle") ?? new Theme { Name = "pickle" };
        config.Changed += (_, e) =>
        {
            if (_elevated && (e.Path is null || e.Path.Equals("shell.adminTheme", StringComparison.OrdinalIgnoreCase)))
            {
                var next = AdminTheme(e.Config);
                if (next is not null && !string.Equals(next.Name, _current.Name, StringComparison.OrdinalIgnoreCase))
                {
                    (_current, _adminOverride) = (next, true);
                    ThemeChanged?.Invoke(this, next);
                    return;
                }

                _adminOverride &= next is not null;
            }

            // Choosing a theme ends the admin override; a plain reload keeps it.
            if (e.Path is not null && e.Path.Equals("theme", StringComparison.OrdinalIgnoreCase))
            {
                _adminOverride = false;
            }

            if (_adminOverride)
            {
                return;
            }

            if (e.Path is null || e.Path.Equals("theme", StringComparison.OrdinalIgnoreCase)
                || e.Path.Equals("lightTheme", StringComparison.OrdinalIgnoreCase) || e.Path.Equals("darkTheme", StringComparison.OrdinalIgnoreCase))
            {
                Switch(Configured(e.Config));
            }
        };
    }

    public Theme Current => _current;

    /// <summary>True when the configured theme is "auto".</summary>
    public bool FollowsSystem => IsAuto(_config.Current.Theme);

    /// <summary>What "auto" sees now: true for light mode, false for dark, null when the system doesn't say.</summary>
    public bool? SystemPrefersLight => PrefersLight();

    /// <summary>The light/dark check (replaceable in tests).</summary>
    internal Func<bool?> PrefersLight { get; set; }

    /// <summary>True while an elevated session shows <c>shell.adminTheme</c> in place of the configured theme.</summary>
    public bool AdminThemeActive => _adminOverride;

    public event EventHandler<Theme>? ThemeChanged;

    public IReadOnlyList<string> Available
    {
        get
        {
            var names = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var res in typeof(ThemeProvider).Assembly.GetManifestResourceNames())
            {
                if (res.StartsWith(ResourcePrefix, StringComparison.Ordinal) && res.EndsWith(".json", StringComparison.Ordinal))
                {
                    names.Add(res[ResourcePrefix.Length..^".json".Length]);
                }
            }

            if (Directory.Exists(_paths.ThemesDir))
            {
                foreach (var file in Directory.EnumerateFiles(_paths.ThemesDir, "*.json"))
                {
                    names.Add(Path.GetFileNameWithoutExtension(file));
                }
            }

            return [.. names];
        }
    }

    /// <summary>Theme names are file names; anything else (paths, "..") is rejected.</summary>
    public static bool IsValidName(string? name) =>
        !string.IsNullOrWhiteSpace(name) && name.Length <= 64 && name != "." && name != ".."
        && name.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.');

    /// <summary>Path of the user's override for <paramref name="name"/>, or null when the theme is built-in only.</summary>
    public string? UserThemeFile(string name)
    {
        if (!IsValidName(name))
        {
            return null;
        }

        var file = Path.Combine(_paths.ThemesDir, name + ".json");
        return File.Exists(file) ? file : null;
    }

    public Theme? Load(string name)
    {
        if (!IsValidName(name))
        {
            return null;
        }

        try
        {
            var userFile = Path.Combine(_paths.ThemesDir, name + ".json");
            if (File.Exists(userFile))
            {
                return Normalize(JsonSerializer.Deserialize<Theme>(File.ReadAllText(userFile), PickleJson.Options), name);
            }

            using var stream = typeof(ThemeProvider).Assembly.GetManifestResourceStream(ResourcePrefix + name + ".json");
            return stream is null ? null : Normalize(JsonSerializer.Deserialize<Theme>(stream, PickleJson.Options), name);
        }
        catch (JsonException ex)
        {
            _log.Error("theme", $"Theme '{name}' is invalid: {ex.Message}", ex);
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log.Error("theme", $"Theme '{name}' could not be read: {ex.Message}", ex);
            return null;
        }
    }

    public void Apply(string name)
    {
        var auto = IsAuto(name);
        var theme = (auto ? AutoTheme(_config.Current) : Load(name))
            ?? throw new ArgumentException($"Theme '{name}' not found. Available: {string.Join(", ", Available)}, or auto");
        _current = theme;
        _adminOverride = false;
        _config.Update(c => c.Theme = auto ? Auto : theme.Name);
        ThemeChanged?.Invoke(this, theme);
    }

    /// <summary>With theme "auto", switches when the system's light/dark mode changed since the last check.</summary>
    public void RefreshAppearance()
    {
        if (!_adminOverride && FollowsSystem)
        {
            Switch(AutoTheme(_config.Current));
        }
    }

    public static bool IsAuto(string? name) => string.Equals(name?.Trim(), Auto, StringComparison.OrdinalIgnoreCase);

    private Theme? Configured(PickleConfig config) => IsAuto(config.Theme) ? AutoTheme(config) : Load(config.Theme);

    private Theme? AutoTheme(PickleConfig config) => PrefersLight() == true
        ? Load(config.LightTheme) ?? Load("solarized-light")
        : Load(config.DarkTheme) ?? Load("pickle");

    private void Switch(Theme? theme)
    {
        if (theme is not null && !string.Equals(theme.Name, _current.Name, StringComparison.OrdinalIgnoreCase))
        {
            _current = theme;
            ThemeChanged?.Invoke(this, theme);
        }
    }

    private Theme? AdminTheme(PickleConfig config) =>
        _elevated && config.Shell.AdminTheme?.Trim() is { Length: > 0 } name && !name.Equals("none", StringComparison.OrdinalIgnoreCase)
            ? Load(name)
            : null;

    // The file name is the theme's identity: Apply persists Name to config and Load finds it by file name again.
    private static Theme? Normalize(Theme? theme, string name)
    {
        if (theme is not null)
        {
            theme.Name = name;
        }

        return theme;
    }

    internal static IEnumerable<string> EmbeddedResourceNames(Assembly assembly) =>
        assembly.GetManifestResourceNames().Where(n => n.StartsWith(ResourcePrefix, StringComparison.Ordinal));
}
