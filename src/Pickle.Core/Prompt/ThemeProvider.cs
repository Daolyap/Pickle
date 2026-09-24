using System.Reflection;
using System.Text.Json;
using Pickle.Abstractions;

namespace Pickle.Core.Prompt;

/// <summary>Loads themes from the user's themes folder first, then the embedded built-ins (themes/*.json).</summary>
public sealed class ThemeProvider : IThemeProvider
{
    private const string ResourcePrefix = "Pickle.Themes.";
    private readonly PicklePaths _paths;
    private readonly IConfigStore _config;
    private readonly IPickleLogger _log;
    private Theme _current;

    public ThemeProvider(PicklePaths paths, IConfigStore config, IPickleLogger log)
    {
        _paths = paths;
        _config = config;
        _log = log;
        _current = Load(config.Current.Theme) ?? Load("pickle") ?? new Theme { Name = "pickle" };
        config.Changed += (_, e) =>
        {
            if (e.Path is null || e.Path.Equals("theme", StringComparison.OrdinalIgnoreCase))
            {
                var name = e.Config.Theme;
                if (!string.Equals(name, _current.Name, StringComparison.OrdinalIgnoreCase) && Load(name) is { } theme)
                {
                    _current = theme;
                    ThemeChanged?.Invoke(this, theme);
                }
            }
        };
    }

    public Theme Current => _current;

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
        var theme = Load(name) ?? throw new ArgumentException($"Theme '{name}' not found. Available: {string.Join(", ", Available)}");
        _current = theme;
        _config.Update(c => c.Theme = theme.Name);
        ThemeChanged?.Invoke(this, theme);
    }

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
