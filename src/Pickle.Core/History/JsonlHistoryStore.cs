using System.Text.Json;
using Pickle.Abstractions;
using Pickle.Core.Contracts;

namespace Pickle.Core.History;

/// <summary>
/// FOUNDATION PLACEHOLDER (workstream W2 completes this file): loads history.jsonl and appends one JSON object
/// per line. W2 adds secret filtering, dedupe, trimming to MaxEntries and concurrent-session safety.
/// </summary>
public sealed class JsonlHistoryStore : IHistoryStore, IRuntimeComponent
{
    private readonly PickleRuntime _runtime;
    private readonly List<HistoryEntry> _entries = [];
    private readonly object _gate = new();
    private bool _loaded;

    public JsonlHistoryStore(PickleRuntime runtime) => _runtime = runtime;

    public string SessionId { get; } = Guid.NewGuid().ToString("N")[..12];

    public IReadOnlyList<HistoryEntry> Entries
    {
        get
        {
            lock (_gate)
            {
                EnsureLoaded();
                return [.. _entries];
            }
        }
    }

    public bool Add(HistoryEntry entry)
    {
        if (string.IsNullOrWhiteSpace(entry.CommandLine))
        {
            return false;
        }

        lock (_gate)
        {
            EnsureLoaded();
            _entries.Add(entry);
        }

        return true;
    }

    public void CompleteLast(bool success, long durationMs)
    {
        HistoryEntry last;
        lock (_gate)
        {
            if (_entries.Count == 0)
            {
                return;
            }

            last = _entries[^1] with { Success = success, DurationMs = durationMs };
            _entries[^1] = last;
        }

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_runtime.Paths.HistoryFile)!);
            File.AppendAllText(_runtime.Paths.HistoryFile, JsonSerializer.Serialize(last, PickleJson.Compact) + "\n");
        }
        catch (IOException ex)
        {
            _runtime.Log.Warn("history", "Could not append history", ex);
        }
    }

    public void Clear()
    {
        lock (_gate)
        {
            _entries.Clear();
            _loaded = true;
            File.Delete(_runtime.Paths.HistoryFile);
        }
    }

    private void EnsureLoaded()
    {
        if (_loaded)
        {
            return;
        }

        _loaded = true;
        if (!File.Exists(_runtime.Paths.HistoryFile))
        {
            return;
        }

        foreach (var line in File.ReadLines(_runtime.Paths.HistoryFile))
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            try
            {
                if (JsonSerializer.Deserialize<HistoryEntry>(line, PickleJson.Options) is { } entry)
                {
                    _entries.Add(entry);
                }
            }
            catch (JsonException)
            {
            }
        }
    }
}
