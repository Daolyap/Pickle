using System.Management.Automation;
using Pickle.Abstractions;

namespace Pickle.Core.Cmdlets;

/// <summary>
/// <c>Get-PickleHistory [[-Query] &lt;fuzzy&gt;] [-Count N] [-Directory &lt;path&gt;]</c>. Without a query entries come oldest
/// first (the last N with -Count); with one they are de-duplicated and ranked by fuzzy score, then recency.
/// </summary>
[Cmdlet(VerbsCommon.Get, "PickleHistory")]
[OutputType(typeof(HistoryEntry))]
public sealed class GetPickleHistoryCmdlet : PickleCmdlet
{
    private static readonly StringComparison PathComparison =
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    [Parameter(Position = 0)]
    public string? Query { get; set; }

    [Parameter]
    [ValidateRange(1, int.MaxValue)]
    public int Count { get; set; }

    /// <summary>Only commands run in this directory (relative paths resolve against the current location).</summary>
    [Parameter]
    public string? Directory { get; set; }

    protected override void EndProcessing()
    {
        IEnumerable<HistoryEntry> entries = Runtime.History.Entries;
        if (!string.IsNullOrEmpty(Directory))
        {
            var dir = Normalize(SessionState.Path.GetUnresolvedProviderPathFromPSPath(Directory));
            entries = entries.Where(e => e.Cwd is not null && string.Equals(Normalize(e.Cwd), dir, PathComparison));
        }

        if (string.IsNullOrEmpty(Query))
        {
            var list = entries.ToList();
            foreach (var entry in Count > 0 ? list.Skip(Math.Max(0, list.Count - Count)) : list)
            {
                WriteObject(entry);
            }

            return;
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        var newestFirst = entries.Reverse().Where(e => seen.Add(e.CommandLine));
        foreach (var (entry, _) in FuzzyMatcher.Rank(Query, newestFirst, e => e.CommandLine, Count > 0 ? Count : int.MaxValue))
        {
            WriteObject(entry);
        }
    }

    private static string Normalize(string path)
    {
        var trimmed = path.TrimEnd('/', '\\');
        return trimmed.Length == 0 || trimmed.EndsWith(':') ? path : trimmed;
    }
}
