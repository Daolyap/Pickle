using System.Text.Json;
using System.Text.Json.Serialization;
using Pickle.Abstractions;
using Pickle.Core.Contracts;

namespace Pickle.Core.History;

/// <summary>
/// Command history in <c>history.jsonl</c>: one JSON object per line, appended with a single write when the command
/// finishes (so the outcome is recorded). Several Pickle sessions share the file: each appends with shared access and
/// picks up lines other sessions appended whenever the file grew. Secret-looking commands stay in memory only.
/// The file is trimmed to <see cref="HistorySettings.MaxEntries"/> by an atomic rewrite once it exceeds 1.2×.
/// </summary>
public sealed class JsonlHistoryStore : IHistoryStore, IRuntimeComponent, IDisposable
{
    private const double TrimFactor = 1.2;
    private const int HeadLength = 64;

    private readonly PickleRuntime _runtime;
    private readonly object _gate = new();
    private readonly List<HistoryEntry> _entries = [];
    private readonly HashSet<HistoryEntry> _memoryOnly = new(ReferenceEqualityComparer.Instance);
    private readonly HistoryIndex _index = new();
    private HistoryEntry[]? _snapshot;
    private HistoryEntry? _pending;
    private bool _pendingPersist;
    private bool _awaitingCompletion;
    private bool _loaded;
    private bool _disposed;
    private long _fileOffset;
    private int _fileLines;
    private byte[] _fileHead = [];

    public JsonlHistoryStore(PickleRuntime runtime) => _runtime = runtime;

    public string SessionId { get; } = Guid.NewGuid().ToString("N")[..12];

    public IReadOnlyList<HistoryEntry> Entries
    {
        get
        {
            lock (_gate)
            {
                Refresh();
                return _snapshot ??= [.. _entries];
            }
        }
    }

    private string FilePath => _runtime.Paths.HistoryFile;

    private HistorySettings Settings => _runtime.Config.Current.History;

    public void Initialize()
    {
        _runtime.CommandRegistry.Register(new HistoryCommand(this, _runtime));
        _runtime.KeyBindingRegistry.RegisterAction(EditorActionNames.HistorySearch, "Fuzzy-search command history", (buffer, _) =>
        {
            buffer.OpenOverlay(HistorySearchOverlay.Create(_runtime, this, buffer));
            return ValueTask.CompletedTask;
        });
    }

    /// <summary>Warm the lazy load off the REPL thread so the first keystroke doesn't parse the whole file.</summary>
    public void OnStarted() => _ = Task.Run(() =>
    {
        lock (_gate)
        {
            if (!_disposed)
            {
                EnsureLoaded();
            }
        }
    });

    public bool Add(HistoryEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        var settings = Settings;
        lock (_gate)
        {
            _awaitingCompletion = false;
            var line = entry.CommandLine;
            if (string.IsNullOrWhiteSpace(line) || (settings.IgnoreLeadingSpace && line[0] is ' ' or '\t'))
            {
                return false;
            }

            Refresh();
            FlushPending();
            if (settings.IgnoreDuplicates && _entries.Count > 0 && string.Equals(_entries[^1].CommandLine, line, StringComparison.Ordinal))
            {
                return false;
            }

            if (entry.SessionId is null)
            {
                entry = entry with { SessionId = SessionId };
            }

            var secret = settings.FilterSecrets && SecretDetector.ContainsSecret(line);
            _entries.Add(entry);
            _snapshot = null;
            _index.Add(entry);
            if (secret)
            {
                _memoryOnly.Add(entry);
            }

            _pending = entry;
            _pendingPersist = !secret;
            _awaitingCompletion = true;
            TrimMemory();
            return true;
        }
    }

    public void CompleteLast(bool success, long durationMs)
    {
        lock (_gate)
        {
            if (!_awaitingCompletion || _pending is null)
            {
                return;
            }

            _awaitingCompletion = false;
            var pending = _pending;
            _pending = null;
            var updated = pending with { Success = success, DurationMs = durationMs };
            var index = _entries.FindLastIndex(e => ReferenceEquals(e, pending));
            if (index >= 0)
            {
                _entries[index] = updated;
                _snapshot = null;
            }

            if (_memoryOnly.Remove(pending))
            {
                _memoryOnly.Add(updated);
            }

            _index.SetOutcome(updated.CommandLine, success);
            if (_pendingPersist)
            {
                Append(updated);
            }
        }
    }

    public void Clear()
    {
        lock (_gate)
        {
            _entries.Clear();
            _memoryOnly.Clear();
            _index.Clear();
            _snapshot = null;
            _pending = null;
            _awaitingCompletion = false;
            _loaded = true;
            _fileOffset = 0;
            _fileLines = 0;
            _fileHead = [];
            try
            {
                File.Delete(FilePath);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _runtime.Log.Warn("history", "Could not delete the history file", ex);
            }
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            FlushPending();
        }
    }

    /// <summary>The de-duplicated prefix index (loaded and synced with other sessions).</summary>
    internal HistoryIndex GetIndex()
    {
        lock (_gate)
        {
            Refresh();
        }

        return _index;
    }

    /// <summary>True when the entry is kept in memory only (secret filter).</summary>
    internal bool IsMemoryOnly(HistoryEntry entry)
    {
        lock (_gate)
        {
            return _memoryOnly.Contains(entry);
        }
    }

    // ───────────── File I/O (all under _gate) ─────────────

    private void Refresh()
    {
        EnsureLoaded();
        SyncFromFile();
    }

    private void EnsureLoaded()
    {
        if (_loaded)
        {
            return;
        }

        _loaded = true;
        ReloadFromFile();
    }

    private void SyncFromFile()
    {
        long length;
        try
        {
            var info = new FileInfo(FilePath);
            if (!info.Exists)
            {
                if (_fileOffset > 0)
                {
                    ReloadFromFile();
                }

                return;
            }

            length = info.Length;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return;
        }

        if (length == _fileOffset)
        {
            return;
        }

        if (length < _fileOffset)
        {
            ReloadFromFile();
            return;
        }

        var before = _entries.Count;
        if (!ReadNewLines(includeOwnSession: false))
        {
            ReloadFromFile();
            return;
        }

        if (_entries.Count != before)
        {
            for (var i = before; i < _entries.Count; i++)
            {
                _index.Add(_entries[i]);
            }

            _snapshot = null;
            TrimMemory();
        }
    }

    /// <summary>Re-reads the whole file (first load, or another session rewrote/cleared it). Keeps unsaved entries.</summary>
    private void ReloadFromFile()
    {
        var keep = _entries.Where(e => _memoryOnly.Contains(e) || (ReferenceEquals(e, _pending) && _pendingPersist)).ToList();
        _entries.Clear();
        _fileOffset = 0;
        _fileLines = 0;
        _fileHead = [];
        ReadNewLines(includeOwnSession: true);
        _entries.AddRange(keep);
        _snapshot = null;
        TrimMemory(force: true);
    }

    /// <summary>Reads complete lines after <see cref="_fileOffset"/>. Returns false when the file was rewritten.</summary>
    private bool ReadNewLines(bool includeOwnSession)
    {
        byte[] buffer;
        int count;
        try
        {
            using var stream = new FileStream(FilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if (_fileHead.Length > 0)
            {
                var head = new byte[_fileHead.Length];
                if (stream.ReadAtLeast(head, head.Length, throwOnEndOfStream: false) < head.Length || !head.AsSpan().SequenceEqual(_fileHead))
                {
                    return false;
                }
            }

            var length = stream.Length;
            if (length <= _fileOffset)
            {
                return length == _fileOffset;
            }

            count = (int)Math.Min(length - _fileOffset, int.MaxValue);
            buffer = new byte[count];
            stream.Position = _fileOffset;
            count = stream.ReadAtLeast(buffer, count, throwOnEndOfStream: false);
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _runtime.Log.Warn("history", "Could not read the history file", ex);
            return true;
        }

        var data = buffer.AsSpan(0, count);
        var consumed = data.LastIndexOf((byte)'\n') + 1;
        if (consumed == 0)
        {
            return true;
        }

        if (_fileOffset == 0)
        {
            _fileHead = data[..Math.Min(HeadLength, consumed)].ToArray();
        }

        var rest = data[..consumed];
        while (rest.Length > 0)
        {
            var end = rest.IndexOf((byte)'\n');
            var line = rest[..end];
            rest = rest[(end + 1)..];
            if (line.Length > 0 && line[^1] == (byte)'\r')
            {
                line = line[..^1];
            }

            if (line.IsEmpty)
            {
                continue;
            }

            _fileLines++;
            HistoryEntry? entry;
            try
            {
                entry = JsonSerializer.Deserialize(line, HistoryJsonContext.Default.HistoryEntry);
            }
            catch (JsonException)
            {
                continue;
            }

            if (entry is null || string.IsNullOrEmpty(entry.CommandLine)
                || (!includeOwnSession && string.Equals(entry.SessionId, SessionId, StringComparison.Ordinal)))
            {
                continue;
            }

            _entries.Add(entry);
        }

        _fileOffset += consumed;
        return true;
    }

    private void FlushPending()
    {
        if (_pending is null)
        {
            return;
        }

        var pending = _pending;
        _pending = null;
        _awaitingCompletion = false;
        if (_pendingPersist)
        {
            Append(pending);
        }
    }

    private void Append(HistoryEntry entry)
    {
        var path = FilePath;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var json = JsonSerializer.SerializeToUtf8Bytes(entry, HistoryJsonContext.Default.HistoryEntry);
            var line = new byte[json.Length + 1];
            json.CopyTo(line, 0);
            line[^1] = (byte)'\n';

            // One write per line so concurrent sessions appending to the same file never interleave within a line.
            using var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
            stream.Write(line);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _runtime.Log.Warn("history", "Could not append history", ex);
            return;
        }

        // Reads back our own line (counted, skipped) plus anything other sessions appended meanwhile.
        SyncFromFile();
        MaybeTrimFile();
    }

    private void MaybeTrimFile()
    {
        var max = Settings.MaxEntries;
        if (max <= 0 || _fileLines <= max * TrimFactor)
        {
            return;
        }

        var path = FilePath;
        var temp = $"{path}.{SessionId}.tmp";
        try
        {
            // Only one session trims at a time; the others skip (FileShare.None is a real lock on Windows and an
            // advisory flock on Unix, which every Pickle honours).
            using var trimLock = new FileStream(path + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, 1, FileOptions.DeleteOnClose);
            var content = ReadAllShared(path, 0);
            var complete = content.AsSpan(0, content.AsSpan().LastIndexOf((byte)'\n') + 1);
            var lines = new List<Range>();
            var start = 0;
            for (var i = 0; i < complete.Length; i++)
            {
                if (complete[i] == (byte)'\n')
                {
                    if (i > start && !(i == start + 1 && complete[start] == (byte)'\r'))
                    {
                        lines.Add(start..(i + 1));
                    }

                    start = i + 1;
                }
            }

            using (var output = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                foreach (var range in lines.Skip(Math.Max(0, lines.Count - max)))
                {
                    output.Write(complete[range]);
                }

                // Lines other sessions appended while we were rewriting.
                var tail = ReadAllShared(path, complete.Length);
                var tailComplete = tail.AsSpan().LastIndexOf((byte)'\n') + 1;
                output.Write(tail.AsSpan(0, tailComplete));
            }

            File.Move(temp, path, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _runtime.Log.Debug("history", $"History trim skipped: {ex.Message}");
            TryDelete(temp);
            return;
        }

        ReloadFromFile();
    }

    private static byte[] ReadAllShared(string path, long offset)
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

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    private void TrimMemory(bool force = false)
    {
        var max = Settings.MaxEntries;
        if (max > 0 && _entries.Count > max * TrimFactor)
        {
            var remove = _entries.Count - max;
            for (var i = 0; i < remove; i++)
            {
                _memoryOnly.Remove(_entries[i]);
            }

            _entries.RemoveRange(0, remove);
            _snapshot = null;
            force = true;
        }

        if (force)
        {
            _index.Rebuild(_entries);
        }
    }
}

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    PropertyNameCaseInsensitive = true)]
[JsonSerializable(typeof(HistoryEntry))]
internal sealed partial class HistoryJsonContext : JsonSerializerContext
{
}
