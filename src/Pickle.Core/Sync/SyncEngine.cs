using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Pickle.Abstractions;

namespace Pickle.Core.Sync;

public enum SyncDirection
{
    /// <summary>Bring remote changes into this machine; don't modify the remote.</summary>
    Pull,

    /// <summary>Send this machine's changes to the remote; don't modify local files.</summary>
    Push,

    /// <summary>Both at once (auto sync, init).</summary>
    Both,
}

/// <summary>Hashes of every synced item at the last successful sync (DataDir/sync-state.json): the merge base.</summary>
public sealed class SyncState
{
    public string Backend { get; set; } = "none";
    public string? Target { get; set; }
    public DateTimeOffset? LastSync { get; set; }
    public string? LastResult { get; set; }
    public Dictionary<string, string> Items { get; set; } = new(StringComparer.Ordinal);

    public static SyncState Load(string file)
    {
        try
        {
            return File.Exists(file) ? JsonSerializer.Deserialize<SyncState>(File.ReadAllText(file), PickleJson.Options) ?? new SyncState() : new SyncState();
        }
        catch (JsonException)
        {
            return new SyncState();
        }
    }

    public void Save(string file)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        SyncFiles.WriteText(file, JsonSerializer.Serialize(this, PickleJson.Options));
    }
}

/// <param name="HistoryMaxEntries">Merged history keeps at most this many newest commands (0 = all).</param>
public sealed record SyncOptions(SyncDirection Direction, bool SyncHistory = true, bool DryRun = false, bool PreferRemoteOnConflict = false, int HistoryMaxEntries = 0);

public sealed class SyncResult
{
    public List<string> Changes { get; } = [];
    public List<string> Conflicts { get; } = [];
    public bool LocalChanged { get; set; }
    public bool RemoteChanged { get; set; }
    public bool AliasesChanged { get; set; }
    public int Pending { get; set; }
    public Dictionary<string, string> NewBase { get; } = new(StringComparer.Ordinal);
}

/// <summary>
/// Merges Pickle's synced files between the config folder and a sync folder (a plain folder or a git working copy).
/// config.json is merged per setting, aliases.json per alias (UpdatedAt decides conflicts), plugins.json per
/// plugin, history.jsonl is a union, and themes/wizards/profile per file. For each item: only one side changed
/// since the last sync → take it; both changed → the newer one wins and the conflict is reported.
/// config.local.json, logs, caches and plugin binaries are never synced.
/// </summary>
public sealed class SyncEngine
{
    public const string ConfigFile = "config.json";
    public const string AliasesFile = "aliases.json";
    public const string HistoryFile = "history.jsonl";
    public const string ProfileFile = "profile.ps1";
    public const string PluginsFile = "plugins.json";

    private static readonly string[] MachineSpecificConfig = ["/$schema", "/sync/backend", "/sync/target"];

    private readonly string _localRoot;
    private readonly string _remoteRoot;
    private readonly Func<JsonObject>? _readLocalConfig;
    private readonly Action<JsonObject>? _writeLocalConfig;

    /// <param name="readLocalConfig">Local config.json as loaded by the config store (null: read the file).</param>
    /// <param name="writeLocalConfig">Persist a merged config.json locally (null: write the file).</param>
    public SyncEngine(string localRoot, string remoteRoot, Func<JsonObject>? readLocalConfig = null, Action<JsonObject>? writeLocalConfig = null)
    {
        _localRoot = localRoot;
        _remoteRoot = remoteRoot;
        _readLocalConfig = readLocalConfig;
        _writeLocalConfig = writeLocalConfig;
    }

    private sealed record Item(string Hash, DateTimeOffset Timestamp, JsonNode? Json, string? FilePath);

    public SyncResult Run(SyncState state, SyncOptions options)
    {
        var result = new SyncResult();
        MergeConfig(state, options, result);
        MergeList(AliasesFile, "alias", "name", ReadAliasTimestamp, state, options, result);
        MergeList(PluginsFile, "plugin", "name", null, state, options, result);
        MergeFiles(state, options, result);
        if (options.SyncHistory)
        {
            MergeHistory(options, result);
        }

        return result;
    }

    // ───────────── Generic three-way item merge ─────────────

    private enum Winner
    {
        None,
        Local,
        Remote,
    }

    private static Winner Decide(string key, Item? local, Item? remote, SyncState state, SyncOptions options, SyncResult result, string label)
    {
        state.Items.TryGetValue(key, out var baseHash);
        if (local?.Hash == remote?.Hash)
        {
            if (local is not null)
            {
                result.NewBase[key] = local.Hash;
            }

            return Winner.None;
        }

        var localChanged = local?.Hash != baseHash;
        var remoteChanged = remote?.Hash != baseHash;
        Winner winner;
        if (localChanged && !remoteChanged)
        {
            winner = Winner.Local;
        }
        else if (remoteChanged && !localChanged)
        {
            winner = Winner.Remote;
        }
        else
        {
            var localTime = local?.Timestamp ?? DateTimeOffset.MinValue;
            var remoteTime = remote?.Timestamp ?? DateTimeOffset.MinValue;
            winner = options.PreferRemoteOnConflict && remote is not null ? Winner.Remote
                : remoteTime > localTime ? Winner.Remote
                : Winner.Local;
            result.Conflicts.Add($"{label}: changed on both sides; kept the {(winner == Winner.Remote ? "remote" : "local")} version ({(options.PreferRemoteOnConflict ? "first sync" : "newer")})");
        }

        var canWrite = winner == Winner.Local ? options.Direction != SyncDirection.Pull : options.Direction != SyncDirection.Push;
        if (canWrite)
        {
            var source = winner == Winner.Local ? local : remote;
            result.Changes.Add($"{(winner == Winner.Local ? "↑" : "↓")} {(source is null ? "removed " : string.Empty)}{label}");
            if (source is not null)
            {
                result.NewBase[key] = source.Hash;
            }
        }
        else
        {
            result.Pending++;
            if (baseHash is not null)
            {
                result.NewBase[key] = baseHash;
            }
        }

        return canWrite ? winner : Winner.None;
    }

    // ───────────── config.json (per setting) ─────────────

    private void MergeConfig(SyncState state, SyncOptions options, SyncResult result)
    {
        var localFile = Path.Combine(_localRoot, ConfigFile);
        var remoteFile = Path.Combine(_remoteRoot, ConfigFile);
        var localConfig = _readLocalConfig?.Invoke() ?? ReadObject(localFile);
        var remoteConfig = ReadObject(remoteFile);
        var localTime = SyncFiles.Timestamp(localFile);
        var remoteTime = SyncFiles.Timestamp(remoteFile);
        var local = Leaves(localConfig).ToDictionary(l => l.Pointer, l => new Item(Hash(l.Value), localTime, l.Value, null));
        var remote = Leaves(remoteConfig).ToDictionary(l => l.Pointer, l => new Item(Hash(l.Value), remoteTime, l.Value, null));

        var localEdits = new List<(string Pointer, JsonNode? Value, bool Remove)>();
        var remoteEdits = new List<(string Pointer, JsonNode? Value, bool Remove)>();
        foreach (var pointer in Keys(local.Keys, remote.Keys, state, "config:"))
        {
            local.TryGetValue(pointer, out var l);
            remote.TryGetValue(pointer, out var r);
            var label = "config " + string.Join('.', Unescape(pointer));
            switch (Decide("config:" + pointer, l, r, state, options, result, label))
            {
                case Winner.Local:
                    remoteEdits.Add((pointer, l?.Json, l is null));
                    break;
                case Winner.Remote:
                    localEdits.Add((pointer, r?.Json, r is null));
                    break;
            }
        }

        if (options.DryRun)
        {
            return;
        }

        if (localEdits.Count > 0)
        {
            var updated = (JsonObject)localConfig.DeepClone();
            Apply(updated, localEdits);
            if (_writeLocalConfig is not null)
            {
                _writeLocalConfig(updated);
            }
            else
            {
                SyncFiles.WriteText(localFile, updated.ToJsonString(SyncFiles.JsonWrite) + "\n");
            }

            result.LocalChanged = true;
        }

        if (remoteEdits.Count > 0)
        {
            var updated = (JsonObject)remoteConfig.DeepClone();
            Apply(updated, remoteEdits);
            SyncFiles.WriteText(remoteFile, updated.ToJsonString(SyncFiles.JsonWrite) + "\n");
            result.RemoteChanged = true;
        }
    }

    private static IEnumerable<(string Pointer, JsonNode? Value)> Leaves(JsonObject obj, string prefix = "")
    {
        foreach (var (key, value) in obj)
        {
            var pointer = prefix + "/" + key.Replace("~", "~0", StringComparison.Ordinal).Replace("/", "~1", StringComparison.Ordinal);
            if (MachineSpecificConfig.Contains(pointer, StringComparer.OrdinalIgnoreCase))
            {
                continue;
            }

            if (value is JsonObject child && child.Count > 0)
            {
                foreach (var leaf in Leaves(child, pointer))
                {
                    yield return leaf;
                }
            }
            else if (value is not JsonObject)
            {
                yield return (pointer, value);
            }
        }
    }

    private static List<string> Unescape(string pointer) =>
        [.. pointer.Split('/', StringSplitOptions.RemoveEmptyEntries).Select(s => s.Replace("~1", "/", StringComparison.Ordinal).Replace("~0", "~", StringComparison.Ordinal))];

    private static void Apply(JsonObject root, List<(string Pointer, JsonNode? Value, bool Remove)> edits)
    {
        foreach (var (pointer, value, remove) in edits)
        {
            var segments = Unescape(pointer);
            var parent = root;
            for (var i = 0; i < segments.Count - 1; i++)
            {
                if (parent[segments[i]] is not JsonObject child)
                {
                    if (remove)
                    {
                        parent = null;
                        break;
                    }

                    child = [];
                    parent[segments[i]] = child;
                }

                parent = child;
            }

            if (parent is null)
            {
                continue;
            }

            if (remove)
            {
                parent.Remove(segments[^1]);
            }
            else
            {
                parent[segments[^1]] = value?.DeepClone();
            }
        }

        PruneEmpty(root);
    }

    private static void PruneEmpty(JsonObject obj)
    {
        foreach (var (key, value) in obj.ToList())
        {
            if (value is JsonObject child)
            {
                PruneEmpty(child);
                if (child.Count == 0)
                {
                    obj.Remove(key);
                }
            }
        }
    }

    // ───────────── aliases.json / plugins.json (per entry) ─────────────

    private static DateTimeOffset? ReadAliasTimestamp(JsonObject entry) =>
        entry["updatedAt"] is JsonValue v && v.GetValueKind() == JsonValueKind.String
            && DateTimeOffset.TryParse(v.GetValue<string>(), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var t)
            ? t
            : null;

    private void MergeList(string fileName, string kind, string keyProperty, Func<JsonObject, DateTimeOffset?>? entryTime, SyncState state, SyncOptions options, SyncResult result)
    {
        var localFile = Path.Combine(_localRoot, fileName);
        var remoteFile = Path.Combine(_remoteRoot, fileName);
        var (localRoot, localKey, localList) = ReadList(localFile);
        var (remoteRoot, remoteKey, remoteList) = ReadList(remoteFile);
        Dictionary<string, Item> Index(JsonArray list, string file)
        {
            var fileTime = SyncFiles.Timestamp(file);
            var map = new Dictionary<string, Item>(StringComparer.Ordinal);
            foreach (var entry in list.OfType<JsonObject>())
            {
                if (entry[keyProperty] is JsonValue name && name.GetValueKind() == JsonValueKind.String)
                {
                    map[name.GetValue<string>().ToLowerInvariant()] = new Item(Hash(entry), entryTime?.Invoke(entry) ?? fileTime, entry, null);
                }
            }

            return map;
        }

        var local = Index(localList, localFile);
        var remote = Index(remoteList, remoteFile);
        var localEdits = new Dictionary<string, JsonNode?>(StringComparer.Ordinal);
        var remoteEdits = new Dictionary<string, JsonNode?>(StringComparer.Ordinal);
        foreach (var name in Keys(local.Keys, remote.Keys, state, kind + ":"))
        {
            local.TryGetValue(name, out var l);
            remote.TryGetValue(name, out var r);
            switch (Decide($"{kind}:{name}", l, r, state, options, result, $"{kind} {name}"))
            {
                case Winner.Local:
                    remoteEdits[name] = l?.Json;
                    break;
                case Winner.Remote:
                    localEdits[name] = r?.Json;
                    break;
            }
        }

        if (options.DryRun)
        {
            return;
        }

        if (localEdits.Count > 0)
        {
            WriteList(localFile, localRoot, localKey ?? remoteKey, localList, localEdits, keyProperty);
            result.LocalChanged = true;
            result.AliasesChanged |= kind == "alias";
        }

        if (remoteEdits.Count > 0)
        {
            WriteList(remoteFile, remoteRoot, remoteKey ?? localKey, remoteList, remoteEdits, keyProperty);
            result.RemoteChanged = true;
        }
    }

    /// <summary>aliases.json is an array; plugins.json is <c>{ "plugins": [...] }</c> (any object with an array property works).</summary>
    private static (JsonObject? Root, string? ArrayKey, JsonArray List) ReadList(string file)
    {
        var node = ReadNode(file);
        if (node is JsonArray array)
        {
            return (null, null, array);
        }

        if (node is JsonObject obj && obj.FirstOrDefault(p => p.Value is JsonArray) is { Value: JsonArray list } property)
        {
            return (obj, property.Key, list);
        }

        return (null, null, []);
    }

    private static void WriteList(string file, JsonObject? root, string? arrayKey, JsonArray list, Dictionary<string, JsonNode?> edits, string keyProperty)
    {
        var updated = new JsonArray();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entry in list)
        {
            var name = (entry as JsonObject)?[keyProperty]?.GetValue<string>().ToLowerInvariant();
            if (name is not null && edits.TryGetValue(name, out var replacement))
            {
                seen.Add(name);
                if (replacement is not null)
                {
                    updated.Add(replacement.DeepClone());
                }
            }
            else
            {
                updated.Add(entry?.DeepClone());
            }
        }

        foreach (var (name, value) in edits)
        {
            if (!seen.Contains(name) && value is not null)
            {
                updated.Add(value.DeepClone());
            }
        }

        JsonNode output = arrayKey is not null ? CloneWith(root ?? [], arrayKey, updated) : updated;
        SyncFiles.WriteText(file, output.ToJsonString(SyncFiles.JsonWrite) + "\n");
    }

    private static JsonObject CloneWith(JsonObject obj, string key, JsonNode value)
    {
        var clone = (JsonObject)obj.DeepClone();
        clone[key] = value;
        return clone;
    }

    // ───────────── themes, wizards, profile (per file) ─────────────

    private static IEnumerable<string> SyncedFiles(string root)
    {
        if (File.Exists(Path.Combine(root, ProfileFile)))
        {
            yield return ProfileFile;
        }

        foreach (var folder in new[] { "themes", "wizards" })
        {
            var dir = Path.Combine(root, folder);
            if (!Directory.Exists(dir))
            {
                continue;
            }

            foreach (var file in Directory.EnumerateFiles(dir, "*.json"))
            {
                yield return folder + "/" + Path.GetFileName(file);
            }
        }
    }

    private void MergeFiles(SyncState state, SyncOptions options, SyncResult result)
    {
        Dictionary<string, Item> Index(string root) =>
            SyncedFiles(root).ToDictionary(
                relative => relative,
                relative =>
                {
                    var path = Path.Combine(root, relative);
                    return new Item(SyncFiles.HashFile(path), SyncFiles.Timestamp(path), null, path);
                },
                StringComparer.Ordinal);

        var local = Index(_localRoot);
        var remote = Index(_remoteRoot);
        foreach (var relative in Keys(local.Keys, remote.Keys, state, "file:"))
        {
            local.TryGetValue(relative, out var l);
            remote.TryGetValue(relative, out var r);
            var winner = Decide("file:" + relative, l, r, state, options, result, relative);
            if (winner == Winner.None || options.DryRun)
            {
                continue;
            }

            var (source, destinationRoot) = winner == Winner.Local ? (l, _remoteRoot) : (r, _localRoot);
            var destination = Path.Combine(destinationRoot, relative);
            if (source is null)
            {
                File.Delete(destination);
            }
            else
            {
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                File.Copy(source.FilePath!, destination, overwrite: true);
                File.SetLastWriteTimeUtc(destination, source.Timestamp.UtcDateTime);
            }

            if (winner == Winner.Local)
            {
                result.RemoteChanged = true;
            }
            else
            {
                result.LocalChanged = true;
            }
        }
    }

    // ───────────── history.jsonl (union) ─────────────

    private void MergeHistory(SyncOptions options, SyncResult result)
    {
        var localFile = Path.Combine(_localRoot, HistoryFile);
        var remoteFile = Path.Combine(_remoteRoot, HistoryFile);
        var remoteLines = ReadLines(remoteFile);
        var (merged, localMissing, remoteMissing) = MergeHistoryLines(ReadLines(localFile), remoteLines, options.HistoryMaxEntries);

        void Write(string file, int missing, bool local)
        {
            if (missing <= 0)
            {
                return;
            }

            var canWrite = local ? options.Direction != SyncDirection.Push : options.Direction != SyncDirection.Pull;
            if (!canWrite)
            {
                result.Pending++;
                return;
            }

            result.Changes.Add($"{(local ? "↓" : "↑")} history (+{missing} command{(missing == 1 ? string.Empty : "s")})");
            if (options.DryRun)
            {
                return;
            }

            if (local)
            {
                ReplaceLiveHistory(file, remoteLines, options.HistoryMaxEntries);
                result.LocalChanged = true;
            }
            else
            {
                SyncFiles.WriteText(file, string.Concat(merged.Select(l => l + "\n")));
                result.RemoteChanged = true;
            }
        }

        Write(localFile, localMissing, local: true);
        Write(remoteFile, remoteMissing, local: false);
    }

    /// <summary>Union by (timestamp, command), oldest first, newest <paramref name="maxEntries"/> kept; counts what each side lacks.</summary>
    internal static (List<string> Lines, int LocalMissing, int RemoteMissing) MergeHistoryLines(IReadOnlyList<string> local, IReadOnlyList<string> remote, int maxEntries)
    {
        var localKeyed = local.Select(l => (Line: l, Key: HistoryKey(l))).ToList();
        var remoteKeyed = remote.Select(l => (Line: l, Key: HistoryKey(l))).ToList();
        var merged = new Dictionary<string, (DateTimeOffset Time, string Line)>(StringComparer.Ordinal);
        foreach (var (line, (key, time)) in localKeyed.Concat(remoteKeyed))
        {
            merged.TryAdd(key, (time, line));
        }

        var ordered = merged.OrderBy(p => p.Value.Time).ToList();
        if (maxEntries > 0 && ordered.Count > maxEntries)
        {
            ordered = ordered[^maxEntries..];
        }

        var localKeys = localKeyed.Select(k => k.Key.Key).ToHashSet(StringComparer.Ordinal);
        var remoteKeys = remoteKeyed.Select(k => k.Key.Key).ToHashSet(StringComparer.Ordinal);
        return ([.. ordered.Select(p => p.Value.Line)], ordered.Count(p => !localKeys.Contains(p.Key)), ordered.Count(p => !remoteKeys.Contains(p.Key)));
    }

    /// <summary>
    /// Rewrites the live history file the way JsonlHistoryStore trims it: under its lock file, from a fresh read, and
    /// keeping lines other sessions append meanwhile (they never take the lock).
    /// </summary>
    private static void ReplaceLiveHistory(string file, IReadOnlyList<string> remote, int maxEntries)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        using var gate = AcquireLock(file + ".lock");
        var content = ReadShared(file, 0);
        var complete = content.AsSpan(0, content.AsSpan().LastIndexOf((byte)'\n') + 1).ToArray();
        var local = Encoding.UTF8.GetString(complete).TrimStart('\uFEFF').Split('\n')
            .Select(l => l.TrimEnd('\r'))
            .Where(l => !string.IsNullOrWhiteSpace(l))
            .ToList();
        var merged = MergeHistoryLines(local, remote, maxEntries).Lines;
        var temp = file + ".sync.tmp";
        using (var output = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            output.Write(Encoding.UTF8.GetBytes(string.Concat(merged.Select(l => l + "\n"))));
            var tail = ReadShared(file, complete.Length);
            output.Write(tail.AsSpan(0, tail.AsSpan().LastIndexOf((byte)'\n') + 1));
        }

        File.Move(temp, file, overwrite: true);
    }

    private static FileStream AcquireLock(string path)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, 1, FileOptions.DeleteOnClose);
            }
            catch (IOException) when (attempt < 40)
            {
                Thread.Sleep(25);
            }
        }
    }

    private static byte[] ReadShared(string path, long offset)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if (stream.Length <= offset)
            {
                return [];
            }

            var buffer = new byte[stream.Length - offset];
            stream.Position = offset;
            var read = stream.ReadAtLeast(buffer, buffer.Length, throwOnEndOfStream: false);
            return read == buffer.Length ? buffer : buffer[..read];
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            return [];
        }
    }

    private static (string Key, DateTimeOffset Time) HistoryKey(string line)
    {
        try
        {
            if (JsonNode.Parse(line) is JsonObject obj)
            {
                var timestamp = obj["timestamp"]?.ToString() ?? string.Empty;
                var command = obj["commandLine"]?.ToString() ?? string.Empty;
                var time = DateTimeOffset.TryParse(timestamp, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var t) ? t : DateTimeOffset.MinValue;
                return (timestamp + "\n" + command, time);
            }
        }
        catch (JsonException)
        {
        }

        return (line, DateTimeOffset.MinValue);
    }

    private static List<string> ReadLines(string file) =>
        File.Exists(file) ? [.. File.ReadAllLines(file).Where(l => !string.IsNullOrWhiteSpace(l))] : [];

    // ───────────── Helpers ─────────────

    private static IEnumerable<string> Keys(IEnumerable<string> local, IEnumerable<string> remote, SyncState state, string prefix) =>
        local.Union(remote, StringComparer.Ordinal)
            .Union(state.Items.Keys.Where(k => k.StartsWith(prefix, StringComparison.Ordinal)).Select(k => k[prefix.Length..]), StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToList();

    private static JsonObject ReadObject(string file) => ReadNode(file) as JsonObject ?? [];

    private static JsonNode? ReadNode(string file)
    {
        if (!File.Exists(file))
        {
            return null;
        }

        var text = File.ReadAllText(file);
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        try
        {
            return JsonNode.Parse(text, documentOptions: new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException($"{file} is not valid JSON ({ex.Message}). Fix it before syncing.", ex);
        }
    }

    /// <summary>Hash of canonical JSON (object keys sorted), so reformatting a file isn't a change.</summary>
    private static string Hash(JsonNode? node)
    {
        var canonical = Canonical(node)?.ToJsonString() ?? "null";
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)))[..32];
    }

    private static JsonNode? Canonical(JsonNode? node) => node switch
    {
        JsonObject obj => new JsonObject(obj.OrderBy(p => p.Key, StringComparer.Ordinal).Select(p => KeyValuePair.Create(p.Key, Canonical(p.Value)))),
        JsonArray array => new JsonArray([.. array.Select(Canonical)]),
        _ => node?.DeepClone(),
    };
}

internal static class SyncFiles
{
    public static readonly JsonSerializerOptions JsonWrite = new(PickleJson.Options)
    {
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static DateTimeOffset Timestamp(string file) =>
        File.Exists(file) ? new DateTimeOffset(File.GetLastWriteTimeUtc(file), TimeSpan.Zero) : DateTimeOffset.MinValue;

    public static string HashFile(string file)
    {
        // Normalize line endings so a CRLF checkout of the same file isn't a change.
        var text = File.ReadAllText(file).ReplaceLineEndings("\n");
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)))[..32];
    }

    public static void WriteText(string file, string text)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        var tmp = file + ".pickle-tmp";
        File.WriteAllText(tmp, text);
        File.Move(tmp, file, overwrite: true);
    }
}
