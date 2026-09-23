namespace Pickle.Abstractions;

/// <summary>
/// Well-known file locations. Config (synced) lives under <see cref="ConfigDir"/>; machine-local data
/// (logs, caches) under <see cref="DataDir"/>. <c>PICKLE_HOME</c> overrides both (used by tests).
/// </summary>
public sealed class PicklePaths
{
    public PicklePaths(string configDir, string dataDir)
    {
        ConfigDir = configDir;
        DataDir = dataDir;
    }

    public string ConfigDir { get; }
    public string DataDir { get; }

    public string ConfigFile => Path.Combine(ConfigDir, "config.json");
    public string LocalConfigFile => Path.Combine(ConfigDir, "config.local.json");
    public string AliasesFile => Path.Combine(ConfigDir, "aliases.json");
    public string HistoryFile => Path.Combine(ConfigDir, "history.jsonl");
    public string ProfileFile => Path.Combine(ConfigDir, "profile.ps1");
    public string ThemesDir => Path.Combine(ConfigDir, "themes");
    public string PluginsDir => Path.Combine(ConfigDir, "plugins");
    public string WizardsDir => Path.Combine(ConfigDir, "wizards");
    public string SyncStateFile => Path.Combine(DataDir, "sync-state.json");
    public string LogDir => Path.Combine(DataDir, "logs");
    public string CacheDir => Path.Combine(DataDir, "cache");

    public static PicklePaths Resolve() => Resolve(Environment.GetEnvironmentVariable);

    public static PicklePaths Resolve(Func<string, string?> getEnv)
    {
        var home = getEnv("PICKLE_HOME");
        if (!string.IsNullOrWhiteSpace(home))
        {
            return new PicklePaths(Path.Combine(home, "config"), Path.Combine(home, "data"));
        }

        if (OperatingSystem.IsWindows())
        {
            var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            return new PicklePaths(Path.Combine(appData, "Pickle"), Path.Combine(localAppData, "Pickle"));
        }

        var userHome = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var xdgConfig = getEnv("XDG_CONFIG_HOME");
        var xdgData = getEnv("XDG_DATA_HOME");
        return new PicklePaths(
            Path.Combine(string.IsNullOrWhiteSpace(xdgConfig) ? Path.Combine(userHome, ".config") : xdgConfig, "pickle"),
            Path.Combine(string.IsNullOrWhiteSpace(xdgData) ? Path.Combine(userHome, ".local", "share") : xdgData, "pickle"));
    }

    public void EnsureCreated()
    {
        foreach (var dir in new[] { ConfigDir, DataDir, ThemesDir, PluginsDir, WizardsDir, LogDir, CacheDir })
        {
            Directory.CreateDirectory(dir);
        }
    }
}
