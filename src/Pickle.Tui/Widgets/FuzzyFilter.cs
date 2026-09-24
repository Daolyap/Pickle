namespace Pickle.Tui.Widgets;

/// <summary>A successful fuzzy match: higher <see cref="Score"/> is better; <see cref="Positions"/> are matched char indexes.</summary>
public sealed record FuzzyMatch(int Score, IReadOnlyList<int> Positions)
{
    public static FuzzyMatch Empty { get; } = new(0, []);
}

/// <summary>
/// Fuzzy filtering for panels. <see cref="Match"/> is the single scoring seam: it is a small local matcher until the
/// shared <c>Pickle.Abstractions.FuzzyMatcher</c> is available, then only that method's body needs to change.
/// </summary>
public static class FuzzyFilter
{
    private const int MaxStarts = 12;

    /// <summary>
    /// Matches <paramref name="pattern"/> against <paramref name="text"/> (case-insensitive subsequence; space-separated
    /// terms must all match). Returns null when it doesn't match; an empty pattern matches everything with score 0.
    /// </summary>
    public static FuzzyMatch? Match(string pattern, string text)
    {
        if (string.IsNullOrWhiteSpace(pattern))
        {
            return FuzzyMatch.Empty;
        }

        var total = 0;
        var positions = new SortedSet<int>();
        foreach (var term in pattern.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            if (MatchTerm(term, text) is not { } match)
            {
                return null;
            }

            total += match.Score;
            positions.UnionWith(match.Positions);
        }

        return new FuzzyMatch(total, [.. positions]);
    }

    /// <summary>Filter and rank items (stable for equal scores); an empty pattern keeps the original order.</summary>
    public static List<(T Item, FuzzyMatch Match)> Filter<T>(IEnumerable<T> items, string pattern, Func<T, string> text, int limit = int.MaxValue, CancellationToken cancellationToken = default) =>
        Filter(items, pattern, text, keywords: null, limit, cancellationToken);

    /// <summary>
    /// Like <see cref="Filter{T}(IEnumerable{T}, string, Func{T, string}, int, CancellationToken)"/>, but items whose
    /// text doesn't match may still match their <paramref name="keywords"/> (ranked lower, nothing highlighted).
    /// </summary>
    public static List<(T Item, FuzzyMatch Match)> Filter<T>(IEnumerable<T> items, string pattern, Func<T, string> text, Func<T, string?>? keywords, int limit = int.MaxValue, CancellationToken cancellationToken = default)
    {
        var results = new List<(T Item, FuzzyMatch Match, int Index)>();
        var index = 0;
        var empty = string.IsNullOrWhiteSpace(pattern);
        foreach (var item in items)
        {
            if ((index & 1023) == 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }

            if (empty)
            {
                if (results.Count >= limit)
                {
                    break;
                }

                results.Add((item, FuzzyMatch.Empty, index++));
                continue;
            }

            if (Match(pattern, text(item)) is { } match)
            {
                results.Add((item, match, index));
            }
            else if (keywords?.Invoke(item) is { Length: > 0 } extra && Match(pattern, extra) is { } keywordMatch)
            {
                results.Add((item, new FuzzyMatch(keywordMatch.Score - 40, []), index));
            }

            index++;
        }

        if (!empty)
        {
            results.Sort((a, b) => a.Match.Score != b.Match.Score ? b.Match.Score.CompareTo(a.Match.Score) : a.Index.CompareTo(b.Index));
            if (results.Count > limit)
            {
                results.RemoveRange(limit, results.Count - limit);
            }
        }

        return [.. results.Select(r => (r.Item, r.Match))];
    }

    private static FuzzyMatch? MatchTerm(string term, string text)
    {
        // Quick reject: the term must be a subsequence at all.
        var ti = 0;
        for (var i = 0; i < text.Length && ti < term.Length; i++)
        {
            if (Same(text[i], term[ti]))
            {
                ti++;
            }
        }

        if (ti < term.Length)
        {
            return null;
        }

        // Score greedy matches from several start positions (word starts first) and keep the best.
        FuzzyMatch? best = null;
        var starts = 0;
        for (var start = 0; start < text.Length && starts < MaxStarts; start++)
        {
            if (!Same(text[start], term[0]))
            {
                continue;
            }

            starts++;
            if (Score(term, text, start) is { } candidate && (best is null || candidate.Score > best.Score))
            {
                best = candidate;
            }
        }

        return best;
    }

    private static FuzzyMatch? Score(string term, string text, int start)
    {
        var positions = new int[term.Length];
        var score = 0;
        var ti = 0;
        var previous = -2;
        for (var i = start; i < text.Length && ti < term.Length; i++)
        {
            if (!Same(text[i], term[ti]))
            {
                continue;
            }

            positions[ti] = i;
            score += 16;
            if (i == previous + 1)
            {
                score += 24;
            }
            else if (previous >= 0)
            {
                score -= Math.Min(12, i - previous);
            }

            if (IsWordStart(text, i))
            {
                score += i == 0 ? 20 : 14;
            }

            if (text[i] == term[ti])
            {
                score += 1;
            }

            previous = i;
            ti++;
        }

        if (ti < term.Length)
        {
            return null;
        }

        // Prefer matches in the last path segment (file names over directories) and shorter texts.
        var lastSeparator = text.LastIndexOfAny(['/', '\\']);
        if (positions[0] > lastSeparator)
        {
            score += 10;
        }

        score -= Math.Min(30, text.Length / 8);
        return new FuzzyMatch(score, positions);
    }

    private static bool Same(char a, char b) => a == b || char.ToLowerInvariant(a) == char.ToLowerInvariant(b);

    private static bool IsWordStart(string text, int i)
    {
        if (i == 0)
        {
            return true;
        }

        var prev = text[i - 1];
        return prev is '/' or '\\' or '_' or '-' or '.' or ' ' or ':'
            || (char.IsLower(prev) && char.IsUpper(text[i]))
            || (!char.IsLetterOrDigit(prev) && char.IsLetterOrDigit(text[i]));
    }
}
