using Pickle.Abstractions;

namespace Pickle.Core.Input;

/// <summary>
/// Up/Down history browsing, fish style: entries must start with what was typed before browsing began (all entries
/// when nothing was typed), duplicates are skipped, and stepping past the newest entry restores the typed text.
/// </summary>
internal sealed class HistoryNavigator(string original, IReadOnlyList<HistoryEntry> entries)
{
    private readonly Stack<int> _shown = new();
    private readonly HashSet<string> _seen = new(StringComparer.Ordinal);

    public string Original { get; } = original;

    public string? Previous()
    {
        var start = _shown.Count > 0 ? _shown.Peek() : entries.Count;
        for (var i = start - 1; i >= 0; i--)
        {
            var command = entries[i].CommandLine;
            if (command.Length == 0
                || string.Equals(command, Original, StringComparison.Ordinal)
                || !command.StartsWith(Original, StringComparison.OrdinalIgnoreCase)
                || _seen.Contains(command))
            {
                continue;
            }

            _seen.Add(command);
            _shown.Push(i);
            return command;
        }

        return null;
    }

    /// <summary>The next newer match, the original text after the newest, or null when already back at the original.</summary>
    public string? Next()
    {
        if (!_shown.TryPop(out var index))
        {
            return null;
        }

        _seen.Remove(entries[index].CommandLine);
        return _shown.Count > 0 ? entries[_shown.Peek()].CommandLine : Original;
    }
}
