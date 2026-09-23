using Pickle.Core.Contracts;

namespace Pickle.Core.History;

/// <summary>FOUNDATION PLACEHOLDER (workstream W2 replaces this file): most recent history entry with the prefix.</summary>
public sealed class HistoryAutosuggest : IAutosuggestProvider
{
    private readonly PickleRuntime _runtime;

    public HistoryAutosuggest(PickleRuntime runtime) => _runtime = runtime;

    public string? Suggest(string input, string cwd)
    {
        if (string.IsNullOrEmpty(input))
        {
            return null;
        }

        var entries = _runtime.History.Entries;
        for (var i = entries.Count - 1; i >= 0; i--)
        {
            var cmd = entries[i].CommandLine;
            if (cmd.Length > input.Length && cmd.StartsWith(input, StringComparison.Ordinal))
            {
                return cmd;
            }
        }

        return null;
    }
}
