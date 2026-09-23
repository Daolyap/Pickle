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

    public Theme? Load(string name)
    {
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
    }

    public void Apply(string name)
    {
        var theme = Load(name) ?? throw new ArgumentException($"Theme '{name}' not found. Available: {string.Join(", ", Available)}");
        _current = theme;
        _config.Update(c => c.Theme = theme.Name);
        ThemeChanged?.Invoke(this, theme);
    }

    private static Theme? Normalize(Theme? theme, string name)
    {
        if (theme is not null && string.IsNullOrWhiteSpace(theme.Name))
        {
            theme.Name = name;
        }

        return theme;
    }

    internal static IEnumerable<string> EmbeddedResourceNames(Assembly assembly) =>
        assembly.GetManifestResourceNames().Where(n => n.StartsWith(ResourcePrefix, StringComparison.Ordinal));
}
