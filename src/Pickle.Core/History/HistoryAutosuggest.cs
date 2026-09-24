using Pickle.Abstractions;
using Pickle.Core.Contracts;

namespace Pickle.Core.History;

/// <summary>
/// Fish-style autosuggestion: the best previous command line that starts with the input (see
/// <see cref="HistoryIndex.Suggest"/> for the ranking). Uses the store's live index; any other
/// <see cref="IHistoryStore"/> gets an index rebuilt whenever its entry list changes.
/// </summary>
public sealed class HistoryAutosuggest : IAutosuggestProvider
{
    private readonly PickleRuntime _runtime;
    private readonly object _gate = new();
    private HistoryIndex? _fallback;
    private IReadOnlyList<HistoryEntry>? _fallbackSource;
    private int _fallbackCount;

    public HistoryAutosuggest(PickleRuntime runtime) => _runtime = runtime;

    public string? Suggest(string input, string cwd)
    {
        if (string.IsNullOrEmpty(input) || !_runtime.Config.Current.Editor.Autosuggestions)
        {
            return null;
        }

        var index = _runtime.History is JsonlHistoryStore store ? store.GetIndex() : FallbackIndex(_runtime.History);
        return index.Suggest(input, cwd);
    }

    private HistoryIndex FallbackIndex(IHistoryStore history)
    {
        var entries = history.Entries;
        lock (_gate)
        {
            if (_fallback is null || !ReferenceEquals(entries, _fallbackSource) || entries.Count != _fallbackCount)
            {
                _fallback ??= new HistoryIndex();
                _fallback.Rebuild(entries);
                _fallbackSource = entries;
                _fallbackCount = entries.Count;
            }

            return _fallback;
        }
    }
}
