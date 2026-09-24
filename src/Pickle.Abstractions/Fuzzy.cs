using System.Text;

namespace Pickle.Abstractions;

/// <summary>A fuzzy match: higher <see cref="Score"/> is better; <see cref="Positions"/> are matched char indices (ascending).</summary>
public sealed record FuzzyMatch(int Score, IReadOnlyList<int> Positions)
{
    public static FuzzyMatch Empty { get; } = new(0, []);
}

/// <summary>
/// fzf "v2"-style fuzzy matching: the query must appear as a subsequence of the candidate; matches score higher at
/// word boundaries (after whitespace or <c>/ \ - _ . : , ; |</c>), on camelCase humps and digits, in consecutive runs
/// and at the start. Smart case: the match is case-sensitive only when the query contains an uppercase letter.
/// </summary>
public static class FuzzyMatcher
{
    private const int ScoreMatch = 16;
    private const int ScoreGapStart = -3;
    private const int ScoreGapExtension = -1;
    private const int BonusBoundary = ScoreMatch / 2;
    private const int BonusNonWord = ScoreMatch / 2;
    private const int BonusCamel123 = BonusBoundary + ScoreGapExtension;
    private const int BonusConsecutive = -(ScoreGapStart + ScoreGapExtension);
    private const int BonusFirstCharMultiplier = 2;
    private const int BonusBoundaryWhite = BonusBoundary + 2;
    private const int BonusBoundaryDelimiter = BonusBoundary + 1;

    [ThreadStatic]
    private static Scratch? t_scratch;

    // Order matters: classes after NonWord count as "word" characters for boundary bonuses (as in fzf).
    private enum CharClass : byte
    {
        White,
        NonWord,
        Delimiter,
        Lower,
        Upper,
        Letter,
        Number,
    }

    /// <summary>Returns null when <paramref name="query"/> is not a subsequence of <paramref name="candidate"/>.</summary>
    public static FuzzyMatch? Match(string query, string candidate)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(candidate);
        if (query.Length == 0)
        {
            return FuzzyMatch.Empty;
        }

        var pattern = query;
        var caseSensitive = HasUpper(query);
        var positions = new List<int>(pattern.Length);
        var score = Score(pattern, candidate, caseSensitive, positions);
        return score < 0 ? null : new FuzzyMatch(score, positions);
    }

    /// <summary>
    /// Matches every item and returns the best <paramref name="limit"/> by score (ties keep input order). An empty
    /// query returns the first <paramref name="limit"/> items in input order with score 0.
    /// </summary>
    public static IReadOnlyList<(T Item, FuzzyMatch Match)> Rank<T>(string query, IEnumerable<T> items, Func<T, string> text, int limit = 200)
    {
        ArgumentNullException.ThrowIfNull(items);
        ArgumentNullException.ThrowIfNull(text);
        if (limit <= 0)
        {
            return [];
        }

        if (string.IsNullOrEmpty(query))
        {
            var all = new List<(T, FuzzyMatch)>();
            foreach (var item in items)
            {
                if (all.Count >= limit)
                {
                    break;
                }

                all.Add((item, FuzzyMatch.Empty));
            }

            return all;
        }

        var pattern = query;
        var caseSensitive = HasUpper(query);

        // Min-heap of the best `limit` so far; the root is the worst kept candidate (lowest score, then latest index).
        var heap = new PriorityQueue<(T Item, string Text, int Index), (int Score, int Index)>(WorstFirst.Instance);
        var index = 0;
        foreach (var item in items)
        {
            var candidate = text(item) ?? string.Empty;
            var score = Score(pattern, candidate, caseSensitive, positions: null);
            if (score >= 0)
            {
                if (heap.Count < limit)
                {
                    heap.Enqueue((item, candidate, index), (score, index));
                }
                else if (heap.TryPeek(out _, out var worst) && WorstFirst.Instance.Compare((score, index), worst) > 0)
                {
                    heap.EnqueueDequeue((item, candidate, index), (score, index));
                }
            }

            index++;
        }

        var kept = new List<(T Item, string Text, int Index, int Score)>(heap.Count);
        while (heap.TryDequeue(out var entry, out var priority))
        {
            kept.Add((entry.Item, entry.Text, entry.Index, priority.Score));
        }

        kept.Sort(static (a, b) => a.Score != b.Score ? b.Score.CompareTo(a.Score) : a.Index.CompareTo(b.Index));
        var result = new List<(T, FuzzyMatch)>(kept.Count);
        foreach (var (item, candidate, _, score) in kept)
        {
            var positions = new List<int>(pattern.Length);
            Score(pattern, candidate, caseSensitive, positions);
            result.Add((item, new FuzzyMatch(score, positions)));
        }

        return result;
    }

    /// <summary>
    /// Renders <paramref name="text"/> with the chars at <paramref name="positions"/> in <paramref name="matchColor"/>
    /// (bold). <paramref name="baseStyle"/> is a raw SGR sequence emitted first and re-applied after every highlighted
    /// run, so the result ends in the base style (no trailing reset).
    /// </summary>
    public static string Highlight(string text, IReadOnlyList<int> positions, string? matchColor, string? baseStyle = null)
    {
        ArgumentNullException.ThrowIfNull(text);
        baseStyle ??= string.Empty;
        if (positions is null || positions.Count == 0 || text.Length == 0)
        {
            return baseStyle + text;
        }

        var matchStyle = Ansi.Style(foreground: matchColor, bold: true);
        var sorted = IsAscending(positions) ? positions : [.. positions.Distinct().Order()];
        var sb = new StringBuilder(text.Length + (sorted.Count * 16) + baseStyle.Length);
        sb.Append(baseStyle);
        var p = 0;
        var inMatch = false;
        for (var i = 0; i < text.Length; i++)
        {
            while (p < sorted.Count && sorted[p] < i)
            {
                p++;
            }

            var isMatch = p < sorted.Count && sorted[p] == i;
            if (isMatch && !inMatch)
            {
                sb.Append(matchStyle);
                inMatch = true;
            }
            else if (!isMatch && inMatch)
            {
                sb.Append(Ansi.Reset).Append(baseStyle);
                inMatch = false;
            }

            sb.Append(text[i]);
        }

        if (inMatch)
        {
            sb.Append(Ansi.Reset).Append(baseStyle);
        }

        return sb.ToString();
    }

    private static bool IsAscending(IReadOnlyList<int> positions)
    {
        for (var i = 1; i < positions.Count; i++)
        {
            if (positions[i] <= positions[i - 1])
            {
                return false;
            }
        }

        return true;
    }

    private static bool HasUpper(string query)
    {
        foreach (var c in query)
        {
            if (char.IsUpper(c))
            {
                return true;
            }
        }

        return false;
    }

    private static char Fold(char c) => c is >= 'A' and <= 'Z' ? (char)(c | 0x20) : c < 128 ? c : char.ToLowerInvariant(c);

    private static CharClass ClassOf(char c)
    {
        if (c < 128)
        {
            return c switch
            {
                >= 'a' and <= 'z' => CharClass.Lower,
                >= 'A' and <= 'Z' => CharClass.Upper,
                >= '0' and <= '9' => CharClass.Number,
                ' ' or '\t' or '\n' or '\r' or '\v' or '\f' => CharClass.White,
                '/' or '\\' or '-' or '_' or '.' or ':' or ',' or ';' or '|' => CharClass.Delimiter,
                _ => CharClass.NonWord,
            };
        }

        if (char.IsLower(c))
        {
            return CharClass.Lower;
        }

        if (char.IsUpper(c))
        {
            return CharClass.Upper;
        }

        if (char.IsDigit(c))
        {
            return CharClass.Number;
        }

        if (char.IsLetter(c))
        {
            return CharClass.Letter;
        }

        return char.IsWhiteSpace(c) ? CharClass.White : CharClass.NonWord;
    }

    private static int BonusFor(CharClass prev, CharClass current)
    {
        if (current > CharClass.NonWord)
        {
            switch (prev)
            {
                case CharClass.White:
                    return BonusBoundaryWhite;
                case CharClass.Delimiter:
                    return BonusBoundaryDelimiter;
                case CharClass.NonWord:
                    return BonusBoundary;
            }
        }

        if ((prev == CharClass.Lower && current == CharClass.Upper) || (prev != CharClass.Number && current == CharClass.Number))
        {
            return BonusCamel123;
        }

        return current switch
        {
            CharClass.NonWord or CharClass.Delimiter => BonusNonWord,
            CharClass.White => BonusBoundaryWhite,
            _ => 0,
        };
    }

    /// <summary>Returns the score, or -1 when there is no match. Fills <paramref name="positions"/> when given.</summary>
    private static int Score(string pattern, string text, bool caseSensitive, List<int>? positions)
    {
        var m = pattern.Length;
        var n = text.Length;
        if (m == 0)
        {
            return 0;
        }

        if (m > n)
        {
            return -1;
        }

        // Phase 1: cheap subsequence check, also finding where the first pattern char first occurs.
        var first = -1;
        var pi = 0;
        var pc = caseSensitive ? pattern[0] : Fold(pattern[0]);
        for (var i = 0; i < n; i++)
        {
            var c = caseSensitive ? text[i] : Fold(text[i]);
            if (c == pc)
            {
                if (pi == 0)
                {
                    first = i;
                }

                if (++pi == m)
                {
                    break;
                }

                pc = caseSensitive ? pattern[pi] : Fold(pattern[pi]);
            }
        }

        if (pi < m)
        {
            return -1;
        }

        var s = t_scratch ??= new Scratch();
        s.EnsureText(n);
        s.EnsurePattern(m);
        var p = s.Pattern;
        for (var i = 0; i < m; i++)
        {
            p[i] = caseSensitive ? pattern[i] : Fold(pattern[i]);
        }

        var t = s.Text;
        var b = s.Bonus;
        var h0 = s.H0;
        var c0 = s.C0;
        var f = s.First;

        // Phase 2: per-char bonus, first-row scores, and the first occurrence of each pattern char.
        var maxScore = 0;
        var maxScorePos = 0;
        var pidx = 0;
        var lastIdx = 0;
        var pchar0 = p[0];
        var pchar = p[0];
        var prevH0 = 0;
        var prevClass = first > 0 ? ClassOf(text[first - 1]) : CharClass.White;
        var inGap = false;
        for (var j = first; j < n; j++)
        {
            var raw = text[j];
            var cls = ClassOf(raw);
            var c = caseSensitive ? raw : Fold(raw);
            t[j] = c;
            var bonus = BonusFor(prevClass, cls);
            b[j] = bonus;
            prevClass = cls;

            if (c == pchar)
            {
                if (pidx < m)
                {
                    f[pidx] = j;
                    pidx++;
                    pchar = p[Math.Min(pidx, m - 1)];
                }

                lastIdx = j;
            }

            if (c == pchar0)
            {
                var score = ScoreMatch + (bonus * BonusFirstCharMultiplier);
                h0[j] = score;
                c0[j] = 1;
                if (m == 1 && score > maxScore)
                {
                    maxScore = score;
                    maxScorePos = j;
                    if (bonus >= BonusBoundary)
                    {
                        break;
                    }
                }

                inGap = false;
            }
            else
            {
                h0[j] = Math.Max(prevH0 + (inGap ? ScoreGapExtension : ScoreGapStart), 0);
                c0[j] = 0;
                inGap = true;
            }

            prevH0 = h0[j];
        }

        if (m == 1)
        {
            positions?.Add(maxScorePos);
            return maxScore;
        }

        // Phase 3: fill the score matrix H (and consecutive-run lengths C) over the window [f0, lastIdx].
        var f0 = f[0];
        var width = lastIdx - f0 + 1;
        s.EnsureMatrix(width * m);
        var h = s.H;
        var cm = s.C;
        Array.Copy(h0, f0, h, 0, width);
        Array.Copy(c0, f0, cm, 0, width);
        Array.Clear(h, width, width * (m - 1));
        Array.Clear(cm, width, width * (m - 1));

        for (var i = 1; i < m; i++)
        {
            var fi = f[i];
            var pch = p[i];
            var row = i * width;
            var gap = false;
            h[row + fi - f0 - 1] = 0;
            for (var col = fi; col <= lastIdx; col++)
            {
                var j0 = col - f0;
                var s1 = 0;
                var consecutive = 0;
                var s2 = h[row + j0 - 1] + (gap ? ScoreGapExtension : ScoreGapStart);
                if (pch == t[col])
                {
                    s1 = h[row - width + j0 - 1] + ScoreMatch;
                    var bonus = b[col];
                    consecutive = cm[row - width + j0 - 1] + 1;
                    if (consecutive > 1)
                    {
                        var fb = b[col - consecutive + 1];
                        if (bonus >= BonusBoundary && bonus > fb)
                        {
                            consecutive = 1;
                        }
                        else
                        {
                            bonus = Math.Max(bonus, Math.Max(BonusConsecutive, fb));
                        }
                    }

                    if (s1 + bonus < s2)
                    {
                        s1 += b[col];
                        consecutive = 0;
                    }
                    else
                    {
                        s1 += bonus;
                    }
                }

                cm[row + j0] = consecutive;
                gap = s1 < s2;
                var score = Math.Max(Math.Max(s1, s2), 0);
                if (i == m - 1 && score > maxScore)
                {
                    maxScore = score;
                    maxScorePos = col;
                }

                h[row + j0] = score;
            }
        }

        // Phase 4: backtrace for the matched positions.
        if (positions is not null)
        {
            var start = positions.Count;
            var i = m - 1;
            var j = maxScorePos;
            var preferMatch = true;
            var total = width * m;
            while (true)
            {
                var rowStart = i * width;
                var j0 = j - f0;
                var score = h[rowStart + j0];
                var diag = 0;
                var left = 0;
                if (i > 0 && j >= f[i])
                {
                    diag = h[rowStart - width + j0 - 1];
                }

                if (j > f[i])
                {
                    left = h[rowStart + j0 - 1];
                }

                if (score > diag && (score > left || (score == left && preferMatch)))
                {
                    positions.Add(j);
                    if (i == 0)
                    {
                        break;
                    }

                    i--;
                }

                preferMatch = cm[rowStart + j0] > 1 || (rowStart + width + j0 + 1 < total && cm[rowStart + width + j0 + 1] > 0);
                j--;
            }

            positions.Reverse(start, positions.Count - start);
        }

        return maxScore;
    }

    private sealed class WorstFirst : IComparer<(int Score, int Index)>
    {
        public static readonly WorstFirst Instance = new();

        // Lower score is "smaller"; on equal scores the later item is "smaller" so earlier input wins ties.
        public int Compare((int Score, int Index) x, (int Score, int Index) y) =>
            x.Score != y.Score ? x.Score.CompareTo(y.Score) : y.Index.CompareTo(x.Index);
    }

    private sealed class Scratch
    {
        public char[] Text = new char[256];
        public int[] Bonus = new int[256];
        public int[] H0 = new int[256];
        public int[] C0 = new int[256];
        public char[] Pattern = new char[32];
        public int[] First = new int[32];
        public int[] H = new int[1024];
        public int[] C = new int[1024];

        public void EnsureText(int n)
        {
            if (Text.Length < n)
            {
                var size = Math.Max(n, Text.Length * 2);
                Text = new char[size];
                Bonus = new int[size];
                H0 = new int[size];
                C0 = new int[size];
            }
        }

        public void EnsurePattern(int m)
        {
            if (Pattern.Length < m)
            {
                var size = Math.Max(m, Pattern.Length * 2);
                Pattern = new char[size];
                First = new int[size];
            }
        }

        public void EnsureMatrix(int size)
        {
            if (H.Length < size)
            {
                var length = Math.Max(size, H.Length * 2);
                H = new int[length];
                C = new int[length];
            }
        }
    }
}
