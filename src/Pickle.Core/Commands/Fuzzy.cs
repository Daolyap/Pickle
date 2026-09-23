namespace Pickle.Core.Commands;

/// <summary>"Did you mean" suggestions for command names, config paths and plugin ids.</summary>
public static class Fuzzy
{
    public static int Distance(string a, string b)
    {
        a = a.ToLowerInvariant();
        b = b.ToLowerInvariant();
        var d = new int[a.Length + 1, b.Length + 1];
        for (var i = 0; i <= a.Length; i++)
        {
            d[i, 0] = i;
        }

        for (var j = 0; j <= b.Length; j++)
        {
            d[0, j] = j;
        }

        for (var i = 1; i <= a.Length; i++)
        {
            for (var j = 1; j <= b.Length; j++)
            {
                var cost = a[i - 1] == b[j - 1] ? 0 : 1;
                d[i, j] = Math.Min(Math.Min(d[i - 1, j] + 1, d[i, j - 1] + 1), d[i - 1, j - 1] + cost);

                // Adjacent transposition ("cnofig" → "config") counts as one edit.
                if (i > 1 && j > 1 && a[i - 1] == b[j - 2] && a[i - 2] == b[j - 1])
                {
                    d[i, j] = Math.Min(d[i, j], d[i - 2, j - 2] + 1);
                }
            }
        }

        return d[a.Length, b.Length];
    }

    /// <summary>Best matches for <paramref name="input"/>, closest first; empty when nothing is reasonably close.</summary>
    public static IReadOnlyList<string> Suggest(string input, IEnumerable<string> candidates, int max = 3)
    {
        if (string.IsNullOrWhiteSpace(input))
        {
            return [];
        }

        var threshold = Math.Max(2, input.Length / 3);
        return candidates
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(c => (Name: c, Score: Score(input, c)))
            .Where(x => x.Score <= threshold)
            .OrderBy(x => x.Score)
            .ThenBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
            .Take(max)
            .Select(x => x.Name)
            .ToList();
    }

    private static int Score(string input, string candidate)
    {
        if (candidate.StartsWith(input, StringComparison.OrdinalIgnoreCase) || input.StartsWith(candidate, StringComparison.OrdinalIgnoreCase))
        {
            return 0;
        }

        // Compare against the last dotted segment too, so "bellstyle" finds "editor.bellStyle".
        var lastDot = candidate.LastIndexOf('.');
        var distance = Distance(input, candidate);
        if (lastDot >= 0 && !input.Contains('.', StringComparison.Ordinal))
        {
            distance = Math.Min(distance, Distance(input, candidate[(lastDot + 1)..]));
        }

        return distance;
    }
}
