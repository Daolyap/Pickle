using System.Management.Automation;
using System.Text.Json;
using System.Text.Json.Nodes;
using Pickle.Abstractions;
using Pickle.Core.Commands;
using Pickle.Core.Config;
using Pickle.Core.Contracts;

namespace Pickle.Core.Plugins;

/// <summary>
/// Loads plugins: built-ins (passed in by the composition root), then PowerShell-module plugins (folders in the
/// plugins dir, modules on PSModulePath whose manifest has <c>PrivateData.Pickle</c>, and modules installed with
/// <c>pk plugin install</c>), then trusted .NET plugins (each in its own AssemblyLoadContext). Every plugin is
/// isolated: one failing never stops the others. Also registers the core `pk` commands.
/// </summary>
public sealed class PluginManager : IPluginManager, IRuntimeComponent, IDisposable
{
    private readonly PickleRuntime _runtime;
    private readonly object _gate = new();
    private readonly List<PluginInfo> _plugins = [];
    private readonly HashSet<string> _loadedAssemblies = new(StringComparer.OrdinalIgnoreCase);
    private volatile string? _loading;

    public PluginManager(PickleRuntime runtime) => _runtime = runtime;

    public IReadOnlyList<LoadedPlugin> Loaded =>
        [.. Plugins.Select(p => new LoadedPlugin(p.Id, p.DisplayName, p.Kind, p.Path, p.Status == PluginStatus.Loaded, p.Error))];

    public IReadOnlyList<PluginInfo> Plugins
    {
        get
        {
            lock (_gate)
            {
                return [.. _plugins];
            }
        }
    }

    /// <summary>Id of the plugin whose module/initializer is running right now (attribution for Register-Pickle*).</summary>
    public string? LoadingPluginId => _loading;

    /// <summary>Messages for the user collected while loading (untrusted .NET plugins).</summary>
    public List<string> Notices { get; } = [];

    public bool ThirdPartyEnabled => !_runtime.Options.NoPlugins && _runtime.Config.Current.Plugins.AutoLoad;

    // ───────────── Lifecycle ─────────────

    public void Initialize()
    {
        var commands = _runtime.CommandRegistry;
        commands.Register(new PluginCommand(_runtime, this));
        commands.Register(new ConfigCommand(_runtime));
        commands.Register(new DoctorCommand(_runtime));
        commands.Register(new ReloadCommand(_runtime));
        commands.Register(new PathsCommand(_runtime));
    }

    public void OnStarted()
    {
        if (_runtime.Shell.IsInteractive && _runtime.Options.Command is null && _runtime.Options.File is null)
        {
            _runtime.ConfigStore.StartWatching();
        }
    }

    public void Dispose() => _runtime.ConfigStore.StopWatching();

    public void LoadAll(IReadOnlyList<IPicklePlugin> builtIns)
    {
        foreach (var plugin in builtIns)
        {
            var info = new PluginInfo { Id = plugin.Id, DisplayName = plugin.DisplayName, Description = plugin.Description, Kind = PluginInfo.BuiltInKind };
            Add(info);
            try
            {
                InitializeTracked(plugin);
                info.Status = PluginStatus.Loaded;
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                _runtime.Log.Error("plugins", $"Built-in plugin {plugin.Id} failed to initialize", ex);
                info.Status = PluginStatus.Failed;
                info.Error = ex.Message;
            }
        }

        if (!ThirdPartyEnabled)
        {
            _runtime.Log.Info("plugins", "Third-party plugins are off (--no-plugins or plugins.autoLoad=false)");
            return;
        }

        LoadThirdParty();
        ShowNotices();
    }

    /// <summary>Discover and load plugins that are not loaded yet (startup, <c>pk reload</c>, after install/trust).</summary>
    public IReadOnlyList<PluginInfo> LoadThirdParty()
    {
        var results = new List<PluginInfo>();
        foreach (var candidate in DiscoverPowerShell())
        {
            if (Find(candidate.Id, PluginInfo.PowerShellKind) is { Status: PluginStatus.Loaded })
            {
                continue;
            }

            results.Add(LoadPowerShell(candidate));
        }

        foreach (var candidate in DiscoverDotnet())
        {
            if (_loadedAssemblies.Contains(candidate.AssemblyPath))
            {
                continue;
            }

            results.AddRange(LoadDotnet(candidate));
        }

        return results;
    }

    public void ShowNotices()
    {
        if (Notices.Count == 0 || _runtime.Options.Command is not null || _runtime.Options.File is not null || _runtime.Options.Headless)
        {
            return;
        }

        var theme = _runtime.Themes.Current;
        foreach (var notice in Notices)
        {
            _runtime.Terminal.Write(Ansi.Colorize("⚠ " + notice, theme.Ui.Warning) + "\n");
        }

        Notices.Clear();
    }

    // ───────────── Attribution ─────────────

    public PluginInfo? Find(string id, string? kind = null)
    {
        lock (_gate)
        {
            return _plugins.LastOrDefault(p => string.Equals(p.Id, id, StringComparison.OrdinalIgnoreCase) && (kind is null || p.Kind == kind));
        }
    }

    /// <summary>Remember what a plugin registered (shown by <c>Get-PicklePlugin</c>).</summary>
    public void RecordContribution(string? pluginId, string what)
    {
        var id = pluginId ?? LoadingPluginId;
        if (id is null)
        {
            return;
        }

        lock (_gate)
        {
            var info = _plugins.LastOrDefault(p => string.Equals(p.Id, id, StringComparison.OrdinalIgnoreCase));
            if (info is not null && !info.Contributions.Contains(what))
            {
                info.Contributions.Add(what);
            }
        }
    }

    /// <summary>Initialize a .NET/built-in plugin and record what it registered (commands, segments, panels, wizards).</summary>
    private void InitializeTracked(IPicklePlugin plugin)
    {
        HashSet<string> Snapshot() =>
        [
            .. _runtime.CommandRegistry.All.Select(c => "command: " + c.Name),
            .. _runtime.PromptSegmentRegistry.All.Select(s => "segment: " + s.Type),
            .. _runtime.PanelRegistry.All.Select(p => "panel: " + p.Id),
            .. _runtime.PanelRegistry.ListPanels.Select(p => "panel: " + p.Id),
            .. _runtime.WizardRegistry.All.Select(w => "wizard: " + w.Id),
        ];

        var before = Snapshot();
        using (Attribute(plugin.Id))
        {
            plugin.Initialize(_runtime);
        }

        foreach (var added in Snapshot().Except(before).Order(StringComparer.Ordinal))
        {
            RecordContribution(plugin.Id, added);
        }
    }

    private IDisposable Attribute(string id)
    {
        // A plain field, not AsyncLocal: modules import on PowerShell's pipeline thread, which doesn't flow ours.
        var previous = _loading;
        _loading = id;
        return new Scope(() => _loading = previous);
    }

    private void Add(PluginInfo info)
    {
        lock (_gate)
        {
            _plugins.RemoveAll(p => string.Equals(p.Id, info.Id, StringComparison.OrdinalIgnoreCase) && p.Kind == info.Kind && p.Status != PluginStatus.Loaded);
            _plugins.Add(info);
        }
    }

    internal void Forget(PluginInfo info)
    {
        lock (_gate)
        {
            _plugins.Remove(info);
        }
    }

    private bool IsDisabled(string id) =>
        _runtime.Config.Current.Plugins.Disabled.Contains(id, StringComparer.OrdinalIgnoreCase);

    // ───────────── PowerShell plugins ─────────────

    public IReadOnlyList<PowerShellPluginCandidate> DiscoverPowerShell()
    {
        var found = new Dictionary<string, PowerShellPluginCandidate>(StringComparer.OrdinalIgnoreCase);
        var pluginsDir = _runtime.Paths.PluginsDir;
        if (Directory.Exists(pluginsDir))
        {
            foreach (var dir in SafeDirectories(pluginsDir))
            {
                if (File.Exists(Path.Combine(dir, "plugin.json")))
                {
                    continue;
                }

                if (FindModuleFile(dir, Path.GetFileName(dir), manifestOnly: false) is { } module)
                {
                    var id = Path.GetFileNameWithoutExtension(module);
                    found.TryAdd(id, new PowerShellPluginCandidate(id, module, module, "plugins-dir"));
                }
            }
        }

        foreach (var installed in InstalledPlugins.Read(_runtime.Paths).Where(p => p.Source == "gallery"))
        {
            found.TryAdd(installed.Name, new PowerShellPluginCandidate(installed.Name, installed.Name, null, "installed"));
        }

        foreach (var candidate in DiscoverOnModulePath())
        {
            found.TryAdd(candidate.Id, candidate);
        }

        return [.. found.Values];
    }

    private IEnumerable<PowerShellPluginCandidate> DiscoverOnModulePath()
    {
        var modulePath = MainRunspace.Invoke(_runtime, "$env:PSModulePath").Output.FirstOrDefault()?.ToString()
            ?? Environment.GetEnvironmentVariable("PSModulePath")
            ?? string.Empty;
        var embedded = Path.Combine(_runtime.Paths.DataDir, "modules");
        var manifests = new List<string>();
        foreach (var root in modulePath.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!Directory.Exists(root) || IsUnder(root, embedded) || IsUnder(root, _runtime.Paths.PluginsDir))
            {
                continue;
            }

            foreach (var dir in SafeDirectories(root))
            {
                if (FindModuleFile(dir, Path.GetFileName(dir), manifestOnly: true) is { } manifest && MentionsPickle(manifest))
                {
                    manifests.Add(manifest);
                }
            }
        }

        if (manifests.Count == 0)
        {
            return [];
        }

        // The text pre-filter is cheap; PowerShell confirms PrivateData really has a Pickle key.
        var result = MainRunspace.Invoke(
            _runtime,
            """
            param($Paths)
            foreach ($p in $Paths) {
                Get-Module -ListAvailable -Name $p -ErrorAction SilentlyContinue |
                    Where-Object { $_.PrivateData -is [hashtable] -and $_.PrivateData.ContainsKey('Pickle') } |
                    Select-Object -First 1 |
                    ForEach-Object { [pscustomobject]@{ Name = $_.Name; Path = $_.Path } }
            }
            """,
            new Dictionary<string, object?> { ["Paths"] = manifests.ToArray() });
        return result.Output
            .Select(o => (Name: ScriptInvoker.PropertyString(o, "Name"), Path: ScriptInvoker.PropertyString(o, "Path")))
            .Where(m => m.Name is not null && m.Path is not null)
            .Select(m => new PowerShellPluginCandidate(m.Name!, m.Path!, m.Path, "module-path"))
            .ToList();
    }

    public PluginInfo LoadPowerShell(PowerShellPluginCandidate candidate)
    {
        var info = new PluginInfo { Id = candidate.Id, DisplayName = candidate.Id, Kind = PluginInfo.PowerShellKind, Path = candidate.Path };
        Add(info);
        if (IsDisabled(candidate.Id))
        {
            info.Status = PluginStatus.Disabled;
            return info;
        }

        ShellResult result;
        using (Attribute(candidate.Id))
        {
            result = MainRunspace.Invoke(
                _runtime,
                "param($Target) Import-Module -Name $Target -Global -PassThru -DisableNameChecking -ErrorAction Stop | Select-Object -First 1",
                new Dictionary<string, object?> { ["Target"] = candidate.ImportTarget });
        }

        if (result.HadErrors || result.Output.Count == 0)
        {
            info.Status = PluginStatus.Failed;
            info.Error = result.Errors.FirstOrDefault()?.ToString() ?? "Import-Module returned nothing";
            _runtime.Log.Error("plugins", $"PowerShell plugin {candidate.Id} failed to load: {info.Error}");
            return info;
        }

        var module = result.Output[0];
        info.Status = PluginStatus.Loaded;
        info.Version = ScriptInvoker.PropertyString(module, "Version");
        info.Description = ScriptInvoker.PropertyString(module, "Description");
        info.Path ??= ScriptInvoker.PropertyString(module, "Path");
        _runtime.Log.Info("plugins", $"Loaded PowerShell plugin {candidate.Id} {info.Version} from {info.Path}");
        return info;
    }

    /// <summary>Module file for a module folder: Name.psd1, Name/&lt;version&gt;/Name.psd1, Name.psm1, or a lone manifest.</summary>
    public static string? FindModuleFile(string dir, string name, bool manifestOnly)
    {
        var direct = Path.Combine(dir, name + ".psd1");
        if (File.Exists(direct))
        {
            return direct;
        }

        var versioned = SafeDirectories(dir)
            .Select(d => (Dir: d, Version: Version.TryParse(Path.GetFileName(d), out var v) ? v : null))
            .Where(d => d.Version is not null && File.Exists(Path.Combine(d.Dir, name + ".psd1")))
            .OrderByDescending(d => d.Version)
            .Select(d => Path.Combine(d.Dir, name + ".psd1"))
            .FirstOrDefault();
        if (versioned is not null)
        {
            return versioned;
        }

        if (manifestOnly)
        {
            return null;
        }

        var script = Path.Combine(dir, name + ".psm1");
        if (File.Exists(script))
        {
            return script;
        }

        var manifests = SafeFiles(dir, "*.psd1");
        if (manifests.Count == 1)
        {
            return manifests[0];
        }

        var scripts = SafeFiles(dir, "*.psm1");
        return scripts.Count == 1 ? scripts[0] : null;
    }

    private static bool MentionsPickle(string manifest)
    {
        try
        {
            return File.ReadAllText(manifest).Contains("Pickle", StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    // ───────────── .NET plugins ─────────────

    public IReadOnlyList<DotnetPluginCandidate> DiscoverDotnet()
    {
        var result = new List<DotnetPluginCandidate>();
        var pluginsDir = _runtime.Paths.PluginsDir;
        if (!Directory.Exists(pluginsDir))
        {
            return result;
        }

        foreach (var dir in SafeDirectories(pluginsDir))
        {
            var name = Path.GetFileName(dir);
            var manifestFile = Path.Combine(dir, "plugin.json");
            if (File.Exists(manifestFile))
            {
                if (ReadManifest(manifestFile, dir) is { } fromManifest)
                {
                    result.Add(fromManifest);
                }

                continue;
            }

            if (FindModuleFile(dir, name, manifestOnly: false) is not null)
            {
                continue;
            }

            var assemblies = SafeFiles(dir, "*.dll").Where(AssemblyInspector.ReferencesPickleAbstractions).ToList();
            var preferred = assemblies.FirstOrDefault(a => string.Equals(Path.GetFileNameWithoutExtension(a), name, StringComparison.OrdinalIgnoreCase));
            if (preferred is not null)
            {
                result.Add(new DotnetPluginCandidate(name, preferred, null, dir));
            }
            else
            {
                result.AddRange(assemblies.Select(a => new DotnetPluginCandidate(assemblies.Count == 1 ? name : Path.GetFileNameWithoutExtension(a), a, null, dir)));
            }
        }

        return result;
    }

    private DotnetPluginCandidate? ReadManifest(string manifestFile, string dir)
    {
        try
        {
            var manifest = JsonNode.Parse(File.ReadAllText(manifestFile)) as JsonObject;
            var assembly = manifest?["assembly"]?.GetValue<string>();
            if (string.IsNullOrWhiteSpace(assembly))
            {
                _runtime.Log.Warn("plugins", $"{manifestFile}: \"assembly\" is required");
                return null;
            }

            var path = Path.GetFullPath(Path.Combine(dir, assembly));
            if (!IsUnder(path, dir))
            {
                _runtime.Log.Warn("plugins", $"{manifestFile}: the assembly must be inside the plugin folder");
                return null;
            }

            var id = manifest?["id"]?.GetValue<string>();
            return new DotnetPluginCandidate(string.IsNullOrWhiteSpace(id) ? Path.GetFileName(dir) : id, path, manifest?["type"]?.GetValue<string>(), dir);
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or IOException or ArgumentException)
        {
            _runtime.Log.Warn("plugins", $"{manifestFile} is invalid: {ex.Message}");
            return null;
        }
    }

    public bool IsTrusted(string hash) =>
        _runtime.Config.Current.Plugins.TrustedAssemblies.Any(t => AssemblyInspector.NormalizeHash(t) == AssemblyInspector.NormalizeHash(hash));

    public IReadOnlyList<PluginInfo> LoadDotnet(DotnetPluginCandidate candidate)
    {
        var info = new PluginInfo { Id = candidate.Id, DisplayName = candidate.Id, Kind = PluginInfo.DotnetKind, Path = candidate.AssemblyPath };
        Add(info);
        if (IsDisabled(candidate.Id))
        {
            info.Status = PluginStatus.Disabled;
            return [info];
        }

        if (!File.Exists(candidate.AssemblyPath))
        {
            info.Status = PluginStatus.Failed;
            info.Error = $"Assembly not found: {candidate.AssemblyPath}";
            return [info];
        }

        info.Hash = AssemblyInspector.Sha256(candidate.AssemblyPath);
        if (!IsTrusted(info.Hash))
        {
            info.Status = PluginStatus.Untrusted;
            info.Error = "Not trusted. Run: pk plugin trust " + candidate.Id;
            Notices.Add($"Plugin {candidate.Id} is not trusted. Run: pk plugin trust {candidate.Id}");
            _runtime.Log.Warn("plugins", $".NET plugin {candidate.Id} ({candidate.AssemblyPath}, sha256 {info.Hash}) is not trusted; skipped");
            return [info];
        }

        List<IPicklePlugin> instances;
        try
        {
            var context = new PluginLoadContext(candidate.AssemblyPath);
            var assembly = context.LoadFromAssemblyPath(candidate.AssemblyPath);
            _loadedAssemblies.Add(candidate.AssemblyPath);
            var types = candidate.TypeName is not null
                ? [assembly.GetType(candidate.TypeName, throwOnError: true)!]
                : assembly.GetExportedTypes().Where(t => typeof(IPicklePlugin).IsAssignableFrom(t) && t is { IsAbstract: false, IsInterface: false } && t.GetConstructor(Type.EmptyTypes) is not null).ToList();
            if (types.Count == 0)
            {
                throw new InvalidOperationException("No public IPicklePlugin class with a parameterless constructor was found.");
            }

            instances = [.. types.Select(t => (IPicklePlugin)Activator.CreateInstance(t)!)];
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            info.Status = PluginStatus.Failed;
            info.Error = (ex is System.Reflection.TargetInvocationException { InnerException: { } inner } ? inner : ex).Message;
            _runtime.Log.Error("plugins", $".NET plugin {candidate.Id} failed to load", ex);
            return [info];
        }

        Forget(info);
        var results = new List<PluginInfo>();
        foreach (var plugin in instances)
        {
            var pluginInfo = new PluginInfo
            {
                Id = plugin.Id,
                DisplayName = plugin.DisplayName,
                Description = plugin.Description,
                Kind = PluginInfo.DotnetKind,
                Path = candidate.AssemblyPath,
                Hash = info.Hash,
                Version = plugin.GetType().Assembly.GetName().Version?.ToString(),
            };
            Add(pluginInfo);
            results.Add(pluginInfo);
            if (IsDisabled(plugin.Id))
            {
                pluginInfo.Status = PluginStatus.Disabled;
                continue;
            }

            try
            {
                InitializeTracked(plugin);
                pluginInfo.Status = PluginStatus.Loaded;
                _runtime.Log.Info("plugins", $"Loaded .NET plugin {plugin.Id} from {candidate.AssemblyPath}");
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                pluginInfo.Status = PluginStatus.Failed;
                pluginInfo.Error = ex.Message;
                _runtime.Log.Error("plugins", $".NET plugin {plugin.Id} failed to initialize", ex);
            }
        }

        return results;
    }

    // ───────────── Helpers ─────────────

    internal static bool IsUnder(string path, string root)
    {
        var fullPath = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var fullRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        return fullPath.Equals(fullRoot, comparison) || fullPath.StartsWith(fullRoot + Path.DirectorySeparatorChar, comparison);
    }

    private static List<string> SafeDirectories(string dir)
    {
        try
        {
            return [.. Directory.EnumerateDirectories(dir).Order(StringComparer.OrdinalIgnoreCase)];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    private static List<string> SafeFiles(string dir, string pattern)
    {
        try
        {
            return [.. Directory.EnumerateFiles(dir, pattern).Order(StringComparer.OrdinalIgnoreCase)];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    private sealed class Scope(Action dispose) : IDisposable
    {
        public void Dispose() => dispose();
    }
}
