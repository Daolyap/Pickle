using Pickle.Abstractions;

namespace Pickle.Core.History;

/// <summary>
/// De-duplicated history, most recent last, for prefix lookups (autosuggest). Re-adding a command tombstones its
/// older node and appends a new one, so updates are O(1). Nodes are also bucketed by their case-folded first two
/// characters, so a lookup scans only the commands that could match (a miss on 50k entries costs nothing).
/// </summary>
internal sealed class HistoryIndex
{
    private const int MaxCwdsPerCommand = 8;

    private static readonly StringComparison PathComparison =
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    private readonly object _gate = new();
    private readonly Dictionary<string, Node> _byText = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<Node>> _byPrefix = new(StringComparer.Ordinal);
    private Node[] _nodes = new Node[256];
    private int _count;
    private int _dead;

    public int Count
    {
        get
        {
            lock (_gate)
            {
                return _byText.Count;
            }
        }
    }

    public void Add(HistoryEntry entry)
    {
        lock (_gate)
        {
            AddCore(entry);
        }
    }

    public void SetOutcome(string commandLine, bool? success)
    {
        lock (_gate)
        {
            if (_byText.TryGetValue(commandLine, out var node))
            {
                node.Success = success;
            }
        }
    }

    public void Rebuild(IEnumerable<HistoryEntry> entries)
    {
        lock (_gate)
        {
            ClearCore();
            foreach (var entry in entries)
            {
                AddCore(entry);
            }
        }
    }

    public void Clear()
    {
        lock (_gate)
        {
            ClearCore();
        }
    }

    /// <summary>
    /// Best full-line completion of <paramref name="input"/>: case-sensitive prefix matches first, then
    /// case-insensitive (re-cased to start with exactly <paramref name="input"/>). Within each pass, commands run in
    /// <paramref name="cwd"/> beat others, successful beat failed, and then the most recent wins.
    /// </summary>
    public string? Suggest(string input, string? cwd)
    {
        if (string.IsNullOrEmpty(input))
        {
            return null;
        }

        lock (_gate)
        {
            var exact = Find(input, cwd, StringComparison.Ordinal);
            if (exact is not null)
            {
                return exact;
            }

            var loose = Find(input, cwd, StringComparison.OrdinalIgnoreCase);
            return loose is null ? null : string.Concat(input, loose.AsSpan(input.Length));
        }
    }

    private string? Find(string input, string? cwd, StringComparison comparison)
    {
        ReadOnlySpan<Node> candidates = input.Length < PrefixLength
            ? _nodes.AsSpan(0, _count)
            : _byPrefix.TryGetValue(PrefixKey(input), out var bucket) ? System.Runtime.InteropServices.CollectionsMarshal.AsSpan(bucket) : [];
        Node? best = null;
        var bestTier = -1;
        for (var i = candidates.Length - 1; i >= 0; i--)
        {
            var node = candidates[i];
            if (node.Dead || node.Text.Length <= input.Length || !node.Text.StartsWith(input, comparison))
            {
                continue;
            }

            var tier = (node.RanIn(cwd) ? 2 : 0) + (node.Success == false ? 0 : 1);
            if (tier > bestTier)
            {
                best = node;
                bestTier = tier;
                if (tier == 3)
                {
                    break;
                }
            }
        }

        return best?.Text;
    }

    private void AddCore(HistoryEntry entry)
    {
        var text = entry.CommandLine;
        if (string.IsNullOrEmpty(text))
        {
            return;
        }

        string[]? cwds = null;
        if (_byText.TryGetValue(text, out var old))
        {
            old.Dead = true;
            _dead++;
            cwds = old.Cwds;
        }

        var node = new Node(text, MergeCwd(cwds, entry.Cwd)) { Success = entry.Success };
        _byText[text] = node;
        if (_count == _nodes.Length)
        {
            Array.Resize(ref _nodes, _nodes.Length * 2);
        }

        _nodes[_count++] = node;
        if (text.Length >= PrefixLength)
        {
            var key = PrefixKey(text);
            if (!_byPrefix.TryGetValue(key, out var bucket))
            {
                _byPrefix[key] = bucket = [];
            }

            bucket.Add(node);
        }

        if (_dead > 1024 && _dead > _count / 2)
        {
            Compact();
        }
    }

    private void Compact()
    {
        var write = 0;
        for (var read = 0; read < _count; read++)
        {
            if (!_nodes[read].Dead)
            {
                _nodes[write++] = _nodes[read];
            }
        }

        Array.Clear(_nodes, write, _count - write);
        _count = write;
        _dead = 0;
        foreach (var bucket in _byPrefix.Values)
        {
            bucket.RemoveAll(n => n.Dead);
        }
    }

    private void ClearCore()
    {
        _byText.Clear();
        _byPrefix.Clear();
        Array.Clear(_nodes, 0, _count);
        _count = 0;
        _dead = 0;
    }

    private const int PrefixLength = 2;

    // Upper-casing is what OrdinalIgnoreCase compares, so a case-insensitive prefix match always shares the bucket.
    private static string PrefixKey(string text) => text[..PrefixLength].ToUpperInvariant();

    private static string[] MergeCwd(string[]? existing, string? cwd)
    {
        if (string.IsNullOrEmpty(cwd))
        {
            return existing ?? [];
        }

        if (existing is null || existing.Length == 0)
        {
            return [cwd];
        }

        if (string.Equals(existing[0], cwd, PathComparison))
        {
            return existing;
        }

        var merged = new List<string>(Math.Min(existing.Length + 1, MaxCwdsPerCommand)) { cwd };
        foreach (var dir in existing)
        {
            if (merged.Count >= MaxCwdsPerCommand)
            {
                break;
            }

            if (!string.Equals(dir, cwd, PathComparison))
            {
                merged.Add(dir);
            }
        }

        return [.. merged];
    }

    private sealed class Node(string text, string[] cwds)
    {
        public string Text { get; } = text;

        public string[] Cwds { get; } = cwds;

        public bool? Success { get; set; }

        public bool Dead { get; set; }

        public bool RanIn(string? cwd)
        {
            if (string.IsNullOrEmpty(cwd))
            {
                return false;
            }

            foreach (var dir in Cwds)
            {
                if (string.Equals(dir, cwd, PathComparison))
                {
                    return true;
                }
            }

            return false;
        }
    }
}
