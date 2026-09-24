using System.Text.Json;
using System.Text.Json.Nodes;
using Pickle.Abstractions;

namespace Pickle.Core.Config;

/// <summary>
/// config.json (synced) layered with config.local.json (machine-only, never synced). The two layers are kept as
/// separate JSON trees; <see cref="Current"/> is their merge. <see cref="SetValue"/> and <see cref="Update"/> write
/// only what changed into config.json, so local overrides never leak into the synced file; <see cref="SetLocalValue"/>
/// writes config.local.json. Invalid values are reported in <see cref="Problems"/> and replaced by defaults.
/// </summary>
public sealed class JsonConfigStore : IConfigStore, IDisposable
{
    public const string BaseSource = "config.json";
    public const string LocalSource = "config.local.json";

    private static readonly JsonSerializerOptions WithNulls = new(PickleJson.Options)
    {
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.Never,
    };

    private static readonly JsonSerializerOptions WriteOptions = new(PickleJson.Options)
    {
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private static readonly JsonDocumentOptions ReadOptions = new()
    {
        CommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    private readonly PicklePaths _paths;
    private readonly IPickleLogger _log;
    private readonly object _gate = new();

    // Session-only values by config path (Set-PSReadLineOption): applied on every rebuild, never saved, dropped when
    // that setting is changed explicitly.
    private readonly Dictionary<string, Action<PickleConfig>> _sessionValues = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string?> _knownText = new(StringComparer.Ordinal);
    private JsonObject _base = [];
    private JsonObject _local = [];
    private PickleConfig _current = new();
    private IReadOnlyList<ConfigProblem> _problems = [];
    private string? _baseParseError;
    private string? _localParseError;
    private FileSystemWatcher? _watcher;
    private Timer? _debounce;

    public JsonConfigStore(PicklePaths paths, IPickleLogger log)
    {
        _paths = paths;
        _log = log;
        Reload();
    }

    public PickleConfig Current
    {
        get
        {
            lock (_gate)
            {
                return _current;
            }
        }
    }

    /// <summary>Syntax errors, unknown settings and invalid values found in the last load.</summary>
    public IReadOnlyList<ConfigProblem> Problems
    {
        get
        {
            lock (_gate)
            {
                return _problems;
            }
        }
    }

    public string ConfigFile => _paths.ConfigFile;

    public string LocalConfigFile => _paths.LocalConfigFile;

    public bool IsWatching => _watcher is not null;

    public event EventHandler<ConfigChangedEventArgs>? Changed;

    // ───────────── IConfigStore ─────────────

    public void Update(Action<PickleConfig> mutate)
    {
        PickleConfig snapshot;
        string? changedPath;
        lock (_gate)
        {
            var before = ToNode(_current);
            var copy = before.Deserialize<PickleConfig>(PickleJson.Options) ?? new PickleConfig();
            mutate(copy);
            var after = ToNode(copy);
            var changed = new List<string>();
            var updated = (JsonObject)_base.DeepClone();
            ApplyDiff(updated, before, after, string.Empty, changed);
            if (changed.Count == 0)
            {
                return;
            }

            foreach (var path in changed)
            {
                _sessionValues.Remove(path);
            }

            _base = updated;
            Rebuild();
            SaveLayer(_paths.ConfigFile, _base, _baseParseError);
            snapshot = _current;
            changedPath = changed.Count == 1 ? changed[0] : null;
        }

        Changed?.Invoke(this, new ConfigChangedEventArgs(snapshot, changedPath));
    }

    public JsonElement? GetValue(string path)
    {
        var node = Find(ToNode(Current), ConfigSchema.Split(path));
        return node is null ? null : JsonSerializer.Deserialize<JsonElement>(node.ToJsonString());
    }

    /// <summary>Set a value in config.json. Throws <see cref="ConfigValidationException"/> with a friendly message.</summary>
    public void SetValue(string path, string value) => Set(path, value, local: false);

    /// <summary>Set a machine-only value in config.local.json (<c>pk config set --local</c>).</summary>
    public void SetLocalValue(string path, string value) => Set(path, value, local: true);

    /// <summary>Remove a value from config.json (or config.local.json) so the default (or the other layer) applies.</summary>
    public bool Reset(string path, bool local = false)
    {
        var info = ConfigSchema.Resolve(path);
        PickleConfig snapshot;
        lock (_gate)
        {
            EnsureWritable(local);
            var layer = (JsonObject)(local ? _local : _base).DeepClone();
            if (!Remove(layer, info.Segments))
            {
                return false;
            }

            if (local)
            {
                _local = layer;
            }
            else
            {
                _base = layer;
            }

            _sessionValues.Remove(info.Path);
            Rebuild();
            SaveLayer(local ? _paths.LocalConfigFile : _paths.ConfigFile, layer, null);
            snapshot = _current;
        }

        Changed?.Invoke(this, new ConfigChangedEventArgs(snapshot, info.Path));
        return true;
    }

    /// <summary>Set a value for this session only (never saved); it survives reloads until the setting is changed explicitly.</summary>
    public void SetSessionValue(string path, Action<PickleConfig> apply)
    {
        PickleConfig snapshot;
        lock (_gate)
        {
            _sessionValues[path] = apply;
            Rebuild();
            snapshot = _current;
        }

        Changed?.Invoke(this, new ConfigChangedEventArgs(snapshot, path));
    }

    public void Reload()
    {
        PickleConfig loaded;
        lock (_gate)
        {
            _base = ReadLayer(_paths.ConfigFile, out _baseParseError);
            _local = ReadLayer(_paths.LocalConfigFile, out _localParseError);
            Rebuild();
            loaded = _current;
        }

        Changed?.Invoke(this, new ConfigChangedEventArgs(loaded, null));
    }

    public void Save()
    {
        lock (_gate)
        {
            SaveLayer(_paths.ConfigFile, _base, _baseParseError);
        }
    }

    // ───────────── Layers ─────────────

    /// <summary>Where the effective value of <paramref name="path"/> comes from: config.local.json, config.json or default.</summary>
    public string GetSource(string path)
    {
        var segments = ConfigSchema.Split(path);
        lock (_gate)
        {
            if (Find(_local, segments) is not null || HasKey(_local, segments))
            {
                return LocalSource;
            }

            return Find(_base, segments) is not null || HasKey(_base, segments) ? BaseSource : "default";
        }
    }

    public bool IsOverriddenLocally(string path) => GetSource(path) == LocalSource;

    /// <summary>A copy of config.json as currently loaded (only the values the user set).</summary>
    public JsonObject GetBaseLayer()
    {
        lock (_gate)
        {
            return (JsonObject)_base.DeepClone();
        }
    }

    public JsonObject GetLocalLayer()
    {
        lock (_gate)
        {
            return (JsonObject)_local.DeepClone();
        }
    }

    /// <summary>Replace config.json wholesale (used by sync after merging), then reload.</summary>
    public void ReplaceBaseLayer(JsonObject layer)
    {
        PickleConfig snapshot;
        lock (_gate)
        {
            _base = (JsonObject)layer.DeepClone();
            _baseParseError = null;
            Rebuild();
            SaveLayer(_paths.ConfigFile, _base, null);
            snapshot = _current;
        }

        Changed?.Invoke(this, new ConfigChangedEventArgs(snapshot, null));
    }

    /// <summary>Write config.schema.json next to config.json and reference it, so editors offer completion.</summary>
    public string EnsureSchemaReference()
    {
        var schemaFile = Path.Combine(_paths.ConfigDir, "config.schema.json");
        Directory.CreateDirectory(_paths.ConfigDir);
        if (!File.Exists(schemaFile) || File.ReadAllText(schemaFile) != ConfigSchema.SchemaText)
        {
            File.WriteAllText(schemaFile, ConfigSchema.SchemaText);
        }

        lock (_gate)
        {
            if (_baseParseError is null && !_base.ContainsKey("$schema"))
            {
                var updated = new JsonObject { ["$schema"] = "./config.schema.json" };
                foreach (var (key, value) in _base)
                {
                    updated[key] = value?.DeepClone();
                }

                _base = updated;
                SaveLayer(_paths.ConfigFile, _base, null);
            }
        }

        return schemaFile;
    }

    // ───────────── Watching ─────────────

    /// <summary>Reload (debounced) when config.json / config.local.json are edited outside Pickle.</summary>
    public void StartWatching(int debounceMs = 250)
    {
        lock (_gate)
        {
            if (_watcher is not null)
            {
                return;
            }

            try
            {
                Directory.CreateDirectory(_paths.ConfigDir);
                _debounce = new Timer(_ => ReloadIfChangedExternally(), null, Timeout.Infinite, Timeout.Infinite);
                var watcher = new FileSystemWatcher(_paths.ConfigDir)
                {
                    IncludeSubdirectories = false,
                    NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size | NotifyFilters.CreationTime,
                };
                FileSystemEventHandler onChange = (_, e) => OnFileEvent(e.Name, debounceMs);
                watcher.Changed += onChange;
                watcher.Created += onChange;
                watcher.Deleted += onChange;
                watcher.Renamed += (_, e) => OnFileEvent(e.Name, debounceMs);
                watcher.EnableRaisingEvents = true;
                _watcher = watcher;
            }
            catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException or PlatformNotSupportedException)
            {
                _log.Warn("config", $"Can't watch {_paths.ConfigDir} for changes: {ex.Message}");
                _debounce?.Dispose();
                _debounce = null;
            }
        }
    }

    public void StopWatching()
    {
        lock (_gate)
        {
            _watcher?.Dispose();
            _watcher = null;
            _debounce?.Dispose();
            _debounce = null;
        }
    }

    public void Dispose() => StopWatching();

    private void OnFileEvent(string? name, int debounceMs)
    {
        if (name is null
            || !(name.Equals(Path.GetFileName(_paths.ConfigFile), StringComparison.OrdinalIgnoreCase)
                || name.Equals(Path.GetFileName(_paths.LocalConfigFile), StringComparison.OrdinalIgnoreCase)))
        {
            return;
        }

        lock (_gate)
        {
            _debounce?.Change(debounceMs, Timeout.Infinite);
        }
    }

    private void ReloadIfChangedExternally()
    {
        try
        {
            bool changed;
            lock (_gate)
            {
                changed = TryReadText(_paths.ConfigFile) != Known(_paths.ConfigFile)
                    || TryReadText(_paths.LocalConfigFile) != Known(_paths.LocalConfigFile);
            }

            if (changed)
            {
                _log.Info("config", "Config changed on disk; reloading");
                Reload();
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log.Warn("config", $"Config reload failed: {ex.Message}");
        }
    }

    private string? Known(string file) => _knownText.TryGetValue(file, out var text) ? text : null;

    // ───────────── Internals ─────────────

    private void Set(string path, string value, bool local)
    {
        var info = ConfigSchema.Resolve(path);
        var node = ConfigSchema.Convert(info, value);
        PickleConfig snapshot;
        lock (_gate)
        {
            EnsureWritable(local);
            var layer = (JsonObject)(local ? _local : _base).DeepClone();
            SetAt(layer, info.Segments, node);
            if (local)
            {
                _local = layer;
            }
            else
            {
                _base = layer;
            }

            _sessionValues.Remove(info.Path);
            Rebuild();
            SaveLayer(local ? _paths.LocalConfigFile : _paths.ConfigFile, layer, null);
            snapshot = _current;
        }

        Changed?.Invoke(this, new ConfigChangedEventArgs(snapshot, info.Path));
    }

    private void EnsureWritable(bool local)
    {
        var error = local ? _localParseError : _baseParseError;
        if (error is not null)
        {
            throw new ConfigValidationException($"{(local ? LocalSource : BaseSource)} has a syntax error ({error}). Fix it first: pk config edit{(local ? " --local" : string.Empty)}");
        }
    }

    private void Rebuild()
    {
        var problems = new List<ConfigProblem>();
        if (_baseParseError is not null)
        {
            problems.Add(new ConfigProblem(ConfigProblemSeverity.Error, BaseSource, string.Empty, $"Syntax error, file ignored: {_baseParseError}"));
        }

        if (_localParseError is not null)
        {
            problems.Add(new ConfigProblem(ConfigProblemSeverity.Error, LocalSource, string.Empty, $"Syntax error, file ignored: {_localParseError}"));
        }

        var merged = (JsonObject)_base.DeepClone();
        problems.AddRange(ConfigSchema.Validate(merged, BaseSource, removeInvalid: true));
        var local = (JsonObject)_local.DeepClone();
        problems.AddRange(ConfigSchema.Validate(local, LocalSource, removeInvalid: true));
        DeepMerge(merged, local);

        PickleConfig config;
        try
        {
            config = merged.Deserialize<PickleConfig>(PickleJson.Options) ?? new PickleConfig();
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or NotSupportedException)
        {
            problems.Add(new ConfigProblem(ConfigProblemSeverity.Error, BaseSource, string.Empty, $"Config could not be read, using defaults: {ex.Message}"));
            config = new PickleConfig();
        }

        foreach (var problem in problems)
        {
            _log.Warn("config", problem.ToString());
        }

        foreach (var apply in _sessionValues.Values)
        {
            apply(config);
        }

        _current = config;
        _problems = problems;
    }

    private JsonObject ReadLayer(string file, out string? parseError)
    {
        parseError = null;
        var text = TryReadText(file);
        _knownText[file] = text;
        if (string.IsNullOrWhiteSpace(text))
        {
            return [];
        }

        try
        {
            if (JsonNode.Parse(text, documentOptions: ReadOptions) is JsonObject obj)
            {
                return obj;
            }

            parseError = "the file must contain a JSON object";
        }
        catch (JsonException ex)
        {
            parseError = ex.LineNumber is { } line ? $"line {line + 1}: {FirstSentence(ex.Message)}" : FirstSentence(ex.Message);
        }

        _log.Error("config", $"Could not parse {file}: {parseError}");
        return [];
    }

    private static string FirstSentence(string message)
    {
        var index = message.IndexOf(". ", StringComparison.Ordinal);
        return index > 0 ? message[..index] : message.TrimEnd('.');
    }

    private void SaveLayer(string file, JsonObject layer, string? parseError)
    {
        if (parseError is not null)
        {
            _log.Error("config", $"Not saving {file}: it has a syntax error ({parseError})");
            return;
        }

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            var json = layer.ToJsonString(WriteOptions) + Environment.NewLine;
            var tmp = file + ".tmp";
            File.WriteAllText(tmp, json);
            File.Move(tmp, file, overwrite: true);
            _knownText[file] = json;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log.Error("config", $"Failed to save {file}", ex);
        }
    }

    private static string? TryReadText(string file)
    {
        try
        {
            return File.Exists(file) ? File.ReadAllText(file) : null;
        }
        catch (IOException)
        {
            return null;
        }
    }

    private static JsonObject ToNode(PickleConfig config) => JsonSerializer.SerializeToNode(config, WithNulls)!.AsObject();

    /// <summary>Copy every leaf that differs between <paramref name="before"/> and <paramref name="after"/> into <paramref name="target"/>.</summary>
    private static void ApplyDiff(JsonObject target, JsonObject before, JsonObject after, string prefix, List<string> changed)
    {
        var keys = before.Select(p => p.Key).Union(after.Select(p => p.Key), StringComparer.Ordinal).ToList();
        foreach (var key in keys)
        {
            var hasBefore = before.TryGetPropertyValue(key, out var b);
            var hasAfter = after.TryGetPropertyValue(key, out var a);
            var path = prefix + key;
            if (a is JsonObject afterObj && (b is JsonObject || !hasBefore))
            {
                var existingKey = ResolveKey(target, key);
                var child = target[existingKey] as JsonObject ?? [];
                var count = changed.Count;
                ApplyDiff(child, b as JsonObject ?? [], afterObj, path + ".", changed);
                if (changed.Count > count && !ReferenceEquals(child, target[existingKey]))
                {
                    target[existingKey] = child;
                }

                continue;
            }

            if (hasBefore == hasAfter && JsonNode.DeepEquals(a, b))
            {
                continue;
            }

            changed.Add(path);
            if (hasAfter)
            {
                target[ResolveKey(target, key)] = a?.DeepClone();
            }
            else
            {
                target.Remove(ResolveKey(target, key));
            }
        }
    }

    internal static void DeepMerge(JsonObject target, JsonObject source)
    {
        foreach (var (key, value) in source.ToList())
        {
            var existingKey = ResolveKey(target, key);
            if (value is JsonObject sourceObj && target[existingKey] is JsonObject targetObj)
            {
                DeepMerge(targetObj, sourceObj);
            }
            else
            {
                target[existingKey] = value?.DeepClone();
            }
        }
    }

    internal static JsonNode? Find(JsonObject root, IReadOnlyList<string> segments)
    {
        JsonNode? node = root;
        foreach (var segment in segments)
        {
            node = node is JsonObject obj ? FindChild(obj, segment) : null;
            if (node is null)
            {
                return null;
            }
        }

        return node;
    }

    private static bool HasKey(JsonObject root, IReadOnlyList<string> segments)
    {
        if (segments.Count == 0)
        {
            return false;
        }

        return (segments.Count == 1 ? root : Find(root, segments.Take(segments.Count - 1).ToList())) is JsonObject parent
            && parent.Any(p => string.Equals(p.Key, segments[^1], StringComparison.OrdinalIgnoreCase));
    }

    private static void SetAt(JsonObject root, IReadOnlyList<string> segments, JsonNode? value)
    {
        var parent = root;
        for (var i = 0; i < segments.Count - 1; i++)
        {
            var key = ResolveKey(parent, segments[i]);
            if (parent[key] is not JsonObject child)
            {
                child = [];
                parent[key] = child;
            }

            parent = child;
        }

        parent[ResolveKey(parent, segments[^1])] = value;
    }

    private static bool Remove(JsonObject root, IReadOnlyList<string> segments)
    {
        if ((segments.Count == 1 ? root : Find(root, segments.Take(segments.Count - 1).ToList())) is not JsonObject parent)
        {
            return false;
        }

        var removed = parent.Remove(ResolveKey(parent, segments[^1]));

        // Drop sections left empty so config.json stays tidy.
        for (var depth = segments.Count - 1; removed && depth > 0; depth--)
        {
            var prefix = segments.Take(depth).ToList();
            if (Find(root, prefix) is JsonObject { Count: 0 })
            {
                var owner = depth == 1 ? root : (JsonObject)Find(root, prefix.Take(depth - 1).ToList())!;
                owner.Remove(ResolveKey(owner, prefix[^1]));
            }
        }

        return removed;
    }

    private static JsonNode? FindChild(JsonObject obj, string key)
    {
        foreach (var (k, v) in obj)
        {
            if (string.Equals(k, key, StringComparison.OrdinalIgnoreCase))
            {
                return v;
            }
        }

        return null;
    }

    private static string ResolveKey(JsonObject obj, string key)
    {
        foreach (var (k, _) in obj)
        {
            if (string.Equals(k, key, StringComparison.OrdinalIgnoreCase))
            {
                return k;
            }
        }

        return key;
    }
}
