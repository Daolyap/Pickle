using System.Text.Json;
using System.Text.Json.Nodes;
using Pickle.Abstractions;

namespace Pickle.Core.Config;

/// <summary>
/// config.json (synced) layered with config.local.json (machine-only, never synced, never written by Save).
/// Values set through <see cref="SetValue"/> or <see cref="Update"/> are written to config.json.
/// </summary>
public sealed class JsonConfigStore : IConfigStore
{
    private readonly PicklePaths _paths;
    private readonly IPickleLogger _log;
    private readonly object _gate = new();
    private PickleConfig _current = new();

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

    public event EventHandler<ConfigChangedEventArgs>? Changed;

    public void Update(Action<PickleConfig> mutate)
    {
        PickleConfig snapshot;
        lock (_gate)
        {
            mutate(_current);
            snapshot = _current;
            SaveUnlocked();
        }

        Changed?.Invoke(this, new ConfigChangedEventArgs(snapshot, null));
    }

    public JsonElement? GetValue(string path)
    {
        var node = JsonSerializer.SerializeToNode(Current, PickleJson.Options);
        foreach (var segment in SplitPath(path))
        {
            node = node is JsonObject obj ? FindChild(obj, segment) : null;
            if (node is null)
            {
                return null;
            }
        }

        return JsonSerializer.Deserialize<JsonElement>(node!.ToJsonString());
    }

    public void SetValue(string path, string value)
    {
        var segments = SplitPath(path);
        if (segments.Length == 0)
        {
            throw new ArgumentException("Config path is empty.", nameof(path));
        }

        PickleConfig snapshot;
        lock (_gate)
        {
            var root = JsonSerializer.SerializeToNode(_current, PickleJson.Options)!.AsObject();
            var parent = root;
            for (var i = 0; i < segments.Length - 1; i++)
            {
                var child = FindChild(parent, segments[i]);
                if (child is not JsonObject childObj)
                {
                    childObj = new JsonObject();
                    parent[ResolveKey(parent, segments[i])] = childObj;
                }

                parent = childObj;
            }

            parent[ResolveKey(parent, segments[^1])] = ParseValue(value);
            var updated = root.Deserialize<PickleConfig>(PickleJson.Options)
                ?? throw new InvalidOperationException("Config became invalid.");
            _current = updated;
            snapshot = updated;
            SaveUnlocked();
        }

        Changed?.Invoke(this, new ConfigChangedEventArgs(snapshot, path));
    }

    public void Reload()
    {
        PickleConfig loaded;
        lock (_gate)
        {
            var node = ReadObject(_paths.ConfigFile) ?? new JsonObject();
            if (ReadObject(_paths.LocalConfigFile) is { } local)
            {
                DeepMerge(node, local);
            }

            try
            {
                loaded = node.Deserialize<PickleConfig>(PickleJson.Options) ?? new PickleConfig();
            }
            catch (JsonException ex)
            {
                _log.Error("config", $"Invalid config, using defaults: {ex.Message}", ex);
                loaded = new PickleConfig();
            }

            _current = loaded;
        }

        Changed?.Invoke(this, new ConfigChangedEventArgs(loaded, null));
    }

    public void Save()
    {
        lock (_gate)
        {
            SaveUnlocked();
        }
    }

    private void SaveUnlocked()
    {
        try
        {
            Directory.CreateDirectory(_paths.ConfigDir);
            var json = JsonSerializer.Serialize(_current, PickleJson.Options);
            var tmp = _paths.ConfigFile + ".tmp";
            File.WriteAllText(tmp, json);
            File.Move(tmp, _paths.ConfigFile, overwrite: true);
        }
        catch (IOException ex)
        {
            _log.Error("config", "Failed to save config", ex);
        }
        catch (UnauthorizedAccessException ex)
        {
            _log.Error("config", "Failed to save config", ex);
        }
    }

    private JsonObject? ReadObject(string file)
    {
        if (!File.Exists(file))
        {
            return null;
        }

        try
        {
            return JsonNode.Parse(File.ReadAllText(file), documentOptions: new JsonDocumentOptions
            {
                CommentHandling = JsonCommentHandling.Skip,
                AllowTrailingCommas = true,
            }) as JsonObject;
        }
        catch (JsonException ex)
        {
            _log.Error("config", $"Could not parse {file}: {ex.Message}", ex);
            return null;
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

    private static string[] SplitPath(string path) =>
        path.Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

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

    private static JsonNode? ParseValue(string value)
    {
        try
        {
            return JsonNode.Parse(value);
        }
        catch (JsonException)
        {
            return JsonValue.Create(value);
        }
    }
}
