using System.Globalization;
using Pickle.Abstractions;

namespace Pickle.Core.History;

/// <summary>Summary returned by <c>pk history stats</c>.</summary>
public sealed record HistoryStats(
    int Entries,
    int UniqueCommands,
    int Sessions,
    int Directories,
    double? SuccessRate,
    DateTimeOffset? Oldest,
    DateTimeOffset? Newest,
    string[] TopCommands,
    string File,
    long FileBytes);

/// <summary><c>pk history [list [N | -n N]] | search &lt;query&gt; [-n N] | clear [--force] | stats</c>.</summary>
internal sealed class HistoryCommand(IHistoryStore store, PickleRuntime runtime) : IPickleCommand
{
    private const int DefaultCount = 20;

    public string Name => "history";

    public string Description => "List, search, clear or summarize command history";

    public string Usage => "pk history [list [N | -n N]] | search <query> [-n N] | clear [--force] | stats";

    public ValueTask<int> ExecuteAsync(PickleCommandContext context, IReadOnlyList<string> args, CancellationToken cancellationToken)
    {
        var sub = args.Count == 0 ? "list" : args[0].ToLowerInvariant();
        var rest = args.Skip(1).ToList();

        // `pk history 50` / `pk history list 50`: a bare count, since `-n` is taken by pk's own -Name when unquoted.
        if (IsCount(sub))
        {
            rest.Insert(0, sub);
            sub = "list";
        }

        if (sub is "list" or "ls" && rest.Count == 1 && IsCount(rest[0]))
        {
            rest.Insert(0, "--count");
        }

        if (!TryTakeCount(rest, out var count))
        {
            context.WriteError("-n expects a positive number. Usage: " + Usage);
            return ValueTask.FromResult(2);
        }

        var exit = sub switch
        {
            "list" or "ls" when rest.Count == 0 => List(context, count),
            "search" or "find" when rest.Count > 0 => Search(context, string.Join(' ', rest), count),
            "clear" => Clear(context, rest),
            "stats" when rest.Count == 0 => Stats(context),
            _ => Fail(context),
        };
        return ValueTask.FromResult(exit);
    }

    private int List(PickleCommandContext context, int count)
    {
        var entries = store.Entries;
        var first = Math.Max(0, entries.Count - count);
        var numberWidth = entries.Count.ToString(CultureInfo.InvariantCulture).Length;
        var colors = runtime.Themes.Current.Ui;
        for (var i = first; i < entries.Count; i++)
        {
            var entry = entries[i];
            var number = (i + 1).ToString(CultureInfo.InvariantCulture).PadLeft(numberWidth);
            var when = entry.Timestamp.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
            var status = entry.Success == false ? Ansi.Colorize("✗", colors.Error) : " ";
            context.WriteHost($"{Ansi.Colorize(number, colors.Muted)}  {Ansi.Colorize(when, colors.Muted)} {status} {HistorySearchOverlay.OneLine(entry.CommandLine)}");
        }

        return 0;
    }

    private int Search(PickleCommandContext context, string query, int count)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var newestFirst = store.Entries.Reverse().Where(e => seen.Add(e.CommandLine));
        var colors = runtime.Themes.Current.Ui;
        var now = DateTimeOffset.Now;
        var ranked = FuzzyMatcher.Rank(query, newestFirst, e => e.CommandLine, count);
        foreach (var (entry, match) in ranked)
        {
            var age = HistorySearchOverlay.RelativeTime(now - entry.Timestamp).PadLeft(4);
            var line = FuzzyMatcher.Highlight(HistorySearchOverlay.OneLine(entry.CommandLine), match.Positions, colors.MatchHighlight);
            context.WriteHost($"{Ansi.Colorize(age, colors.Muted)}  {line}{Ansi.Reset}");
        }

        return ranked.Count > 0 ? 0 : 1;
    }

    private int Clear(PickleCommandContext context, List<string> rest)
    {
        var force = rest.Any(a => a is "--force" or "-f" or "-y" or "--yes" or "-Force");
        if (!force && !context.Confirm("Clear all Pickle command history?", false))
        {
            context.WriteHost("History not cleared.");
            return 1;
        }

        store.Clear();
        context.WriteHost("History cleared.");
        return 0;
    }

    private int Stats(PickleCommandContext context)
    {
        var entries = store.Entries;
        var completed = entries.Where(e => e.Success.HasValue).ToList();
        var top = entries
            .Select(e => FirstWord(e.CommandLine))
            .Where(w => w.Length > 0)
            .GroupBy(w => w, StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(g => g.Count())
            .ThenBy(g => g.Key, StringComparer.OrdinalIgnoreCase)
            .Take(10)
            .Select(g => $"{g.Key} ({g.Count().ToString(CultureInfo.InvariantCulture)})")
            .ToArray();
        var file = runtime.Paths.HistoryFile;
        context.WriteObject(new HistoryStats(
            Entries: entries.Count,
            UniqueCommands: entries.Select(e => e.CommandLine).Distinct(StringComparer.Ordinal).Count(),
            Sessions: entries.Select(e => e.SessionId).Where(s => s is not null).Distinct(StringComparer.Ordinal).Count(),
            Directories: entries.Select(e => e.Cwd).Where(c => c is not null).Distinct(StringComparer.OrdinalIgnoreCase).Count(),
            SuccessRate: completed.Count == 0 ? null : Math.Round(completed.Count(e => e.Success == true) * 100.0 / completed.Count, 1),
            Oldest: entries.Count == 0 ? null : entries.Min(e => e.Timestamp),
            Newest: entries.Count == 0 ? null : entries.Max(e => e.Timestamp),
            TopCommands: top,
            File: file,
            FileBytes: File.Exists(file) ? new FileInfo(file).Length : 0));
        return 0;
    }

    private int Fail(PickleCommandContext context)
    {
        context.WriteError("Usage: " + Usage);
        return 2;
    }

    private static bool TryTakeCount(List<string> args, out int count)
    {
        count = DefaultCount;
        for (var i = 0; i < args.Count; i++)
        {
            if (args[i] is "-n" or "--count" or "-Count" or "-First")
            {
                if (i + 1 >= args.Count || !int.TryParse(args[i + 1], NumberStyles.Integer, CultureInfo.InvariantCulture, out count) || count <= 0)
                {
                    return false;
                }

                args.RemoveRange(i, 2);
                return true;
            }
        }

        return true;
    }

    private static bool IsCount(string text) =>
        int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var n) && n > 0;

    private static string FirstWord(string commandLine)
    {
        var span = commandLine.AsSpan().TrimStart();
        var end = span.IndexOfAny(' ', '\t', '\n');
        return (end < 0 ? span : span[..end]).ToString();
    }
}
