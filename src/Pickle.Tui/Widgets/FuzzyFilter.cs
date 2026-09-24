using Pickle.Abstractions;

namespace Pickle.Tui.Widgets;

/// <summary>
/// Fuzzy filtering for panels on top of the shared <see cref="FuzzyMatcher"/> (the same scoring as completion and
/// history search), adding space-separated terms that must all match and keyword fallbacks.
/// </summary>
public static class FuzzyFilter
{
    /// <summary>
    /// Matches <paramref name="pattern"/> against <paramref name="text"/>; space-separated terms must all match.
    /// Returns null when it doesn't match; an empty pattern matches everything with score 0.
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
            if (FuzzyMatcher.Match(term, text) is not { } match)
            {
                return null;
            }

            total += match.Score + PathBonus(text, match);
            positions.UnionWith(match.Positions);
        }

        return new FuzzyMatch(total, [.. positions]);
    }

    // Panels list paths a lot: prefer matches inside the last path segment (file names over directory names).
    private static int PathBonus(string text, FuzzyMatch match)
    {
        var lastSeparator = text.LastIndexOfAny(['/', '\\']);
        return lastSeparator >= 0 && match.Positions.Count > 0 && match.Positions[0] > lastSeparator ? 24 : 0;
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
}
