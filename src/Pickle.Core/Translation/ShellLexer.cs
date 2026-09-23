using System.Text;

namespace Pickle.Core.Translation;

public enum ShellSeparator
{
    None,
    Semicolon,
    Newline,
    Pipe,
    AndAnd,
    OrOr,
}

/// <summary>A whitespace-delimited word of the input line. Quotes, <c>$(...)</c>, <c>{...}</c> and <c>(...)</c> stay inside one word.</summary>
public sealed record ShellWord(int Start, int End, string Text)
{
    /// <summary>Only literal characters: no quotes, backticks, variables, braces or parentheses.</summary>
    public bool IsPlain { get; init; }

    public bool HasQuotes { get; init; }

    /// <summary>The word's value when it is made of literal text and simple (non-expanding) quoted strings; otherwise null.</summary>
    public string? Literal { get; init; }
}

/// <summary>A pipeline element: words up to the next <c>;</c>, newline, <c>|</c>, <c>&amp;&amp;</c> or <c>||</c> at nesting depth 0.</summary>
public sealed class ShellSegment
{
    public List<ShellWord> Words { get; } = [];

    public int Start { get; set; }

    public int End { get; set; }

    /// <summary>Separator before this segment (None for the first one).</summary>
    public ShellSeparator Before { get; set; }

    public ShellSeparator After { get; set; }

    public bool IsChained => Before is ShellSeparator.AndAnd or ShellSeparator.OrOr || After is ShellSeparator.AndAnd or ShellSeparator.OrOr;
}

/// <summary>Statement: segments joined by <c>|</c>, <c>&amp;&amp;</c>, <c>||</c>; statements are separated by <c>;</c> or newlines.</summary>
public sealed record ShellStatement(IReadOnlyList<ShellSegment> Segments)
{
    public int Start => Segments[0].Start;

    public int End => Segments[^1].End;
}

/// <summary>
/// A small PowerShell-aware lexer for input rewriters. It understands PowerShell quoting (single quotes with <c>''</c>,
/// double quotes with backtick escapes and <c>$(...)</c>, smart quotes), backtick escapes, comments and nesting, so
/// rewriters never touch text inside strings, script blocks or subexpressions.
/// </summary>
public static class ShellLexer
{
    public static IReadOnlyList<ShellSegment> Segments(string input)
    {
        var segments = new List<ShellSegment>();
        var current = new ShellSegment { Start = 0 };
        var i = 0;
        var n = input.Length;
        while (i < n)
        {
            var c = input[i];
            if (c is ' ' or '\t')
            {
                i++;
                continue;
            }

            if (c == '<' && i + 1 < n && input[i + 1] == '#')
            {
                var close = input.IndexOf("#>", i + 2, StringComparison.Ordinal);
                i = close < 0 ? n : close + 2;
                continue;
            }

            if (c == '#')
            {
                while (i < n && input[i] is not ('\n' or '\r'))
                {
                    i++;
                }

                continue;
            }

            var separator = SeparatorAt(input, i, out var length);
            if (separator != ShellSeparator.None)
            {
                current.End = TrimEnd(input, current.Start, i);
                current.After = separator;
                segments.Add(current);
                i += length;
                current = new ShellSegment { Start = SkipSpaces(input, i), Before = separator };
                continue;
            }

            var word = ScanWord(input, i);
            if (current.Words.Count == 0)
            {
                current.Start = i;
            }

            current.Words.Add(word);
            i = word.End;
        }

        current.End = current.Words.Count > 0 ? current.Words[^1].End : TrimEnd(input, current.Start, n);
        segments.Add(current);
        foreach (var segment in segments)
        {
            if (segment.Words.Count > 0)
            {
                segment.Start = segment.Words[0].Start;
                segment.End = segment.Words[^1].End;
            }
            else
            {
                segment.End = Math.Max(segment.Start, segment.End);
            }
        }

        return segments;
    }

    public static IReadOnlyList<ShellStatement> Statements(string input)
    {
        var statements = new List<ShellStatement>();
        var pending = new List<ShellSegment>();
        foreach (var segment in Segments(input))
        {
            pending.Add(segment);
            if (segment.After is ShellSeparator.Semicolon or ShellSeparator.Newline or ShellSeparator.None)
            {
                statements.Add(new ShellStatement([.. pending]));
                pending.Clear();
            }
        }

        if (pending.Count > 0)
        {
            statements.Add(new ShellStatement([.. pending]));
        }

        return statements;
    }

    public static IEnumerable<ShellWord> Words(string input) => Segments(input).SelectMany(s => s.Words);

    public static bool IsSingleQuote(char c) => c is '\'' or '‘' or '’' or '‚' or '‛';

    public static bool IsDoubleQuote(char c) => c is '"' or '“' or '”' or '„';

    private static ShellSeparator SeparatorAt(string s, int i, out int length)
    {
        length = 1;
        var c = s[i];
        var next = i + 1 < s.Length ? s[i + 1] : '\0';
        switch (c)
        {
            case ';':
                return ShellSeparator.Semicolon;
            case '\n':
                return ShellSeparator.Newline;
            case '\r':
                length = next == '\n' ? 2 : 1;
                return ShellSeparator.Newline;
            case '|':
                if (next == '|')
                {
                    length = 2;
                    return ShellSeparator.OrOr;
                }

                return ShellSeparator.Pipe;
            case '&' when next == '&':
                length = 2;
                return ShellSeparator.AndAnd;
            default:
                return ShellSeparator.None;
        }
    }

    private static ShellWord ScanWord(string s, int start)
    {
        var i = start;
        var depth = 0;
        var hasQuotes = false;
        var plain = true;
        while (i < s.Length)
        {
            var c = s[i];
            if (depth == 0 && (c is ' ' or '\t' or '\r' or '\n' or ';' or '|' || (c == '&' && i + 1 < s.Length && s[i + 1] == '&')))
            {
                break;
            }

            if (c == '`')
            {
                plain = false;
                i = Math.Min(s.Length, i + 2);
                continue;
            }

            if (IsSingleQuote(c))
            {
                hasQuotes = true;
                plain = false;
                i = SkipSingleQuoted(s, i);
                continue;
            }

            if (IsDoubleQuote(c))
            {
                hasQuotes = true;
                plain = false;
                i = SkipDoubleQuoted(s, i);
                continue;
            }

            if (c is '(' or '{')
            {
                depth++;
                plain = false;
            }
            else if (c is ')' or '}')
            {
                depth = Math.Max(0, depth - 1);
                plain = false;
            }
            else if (c == '$')
            {
                plain = false;
            }

            i++;
        }

        var text = s[start..i];
        return new ShellWord(start, i, text)
        {
            IsPlain = plain,
            HasQuotes = hasQuotes,
            Literal = plain ? text : TryGetLiteral(text),
        };
    }

    /// <summary>Returns the index just past the closing quote (or the end of input).</summary>
    public static int SkipSingleQuoted(string s, int openIndex)
    {
        var i = openIndex + 1;
        while (i < s.Length)
        {
            if (IsSingleQuote(s[i]))
            {
                if (i + 1 < s.Length && IsSingleQuote(s[i + 1]))
                {
                    i += 2;
                    continue;
                }

                return i + 1;
            }

            i++;
        }

        return s.Length;
    }

    public static int SkipDoubleQuoted(string s, int openIndex)
    {
        var i = openIndex + 1;
        while (i < s.Length)
        {
            var c = s[i];
            if (c == '`')
            {
                i += 2;
                continue;
            }

            if (IsDoubleQuote(c))
            {
                if (i + 1 < s.Length && IsDoubleQuote(s[i + 1]))
                {
                    i += 2;
                    continue;
                }

                return i + 1;
            }

            if (c == '$' && i + 1 < s.Length && s[i + 1] == '(')
            {
                i = SkipBalanced(s, i + 1);
                continue;
            }

            i++;
        }

        return s.Length;
    }

    /// <summary>From an opening '(' or '{', returns the index past its matching close, honoring nested quotes.</summary>
    public static int SkipBalanced(string s, int openIndex)
    {
        var depth = 0;
        var i = openIndex;
        while (i < s.Length)
        {
            var c = s[i];
            if (c == '`')
            {
                i += 2;
                continue;
            }

            if (IsSingleQuote(c))
            {
                i = SkipSingleQuoted(s, i);
                continue;
            }

            if (IsDoubleQuote(c))
            {
                i = SkipDoubleQuoted(s, i);
                continue;
            }

            if (c is '(' or '{')
            {
                depth++;
            }
            else if (c is ')' or '}')
            {
                depth--;
                if (depth == 0)
                {
                    return i + 1;
                }
            }

            i++;
        }

        return s.Length;
    }

    /// <summary>Value of a word made of literal text and quoted strings that don't expand anything (e.g. <c>'a b'</c>, <c>x"y"</c>).</summary>
    public static string? TryGetLiteral(string text)
    {
        var sb = new StringBuilder(text.Length);
        var i = 0;
        while (i < text.Length)
        {
            var c = text[i];
            if (IsSingleQuote(c))
            {
                var end = SkipSingleQuoted(text, i);
                if (end > text.Length || !IsSingleQuote(text[end - 1]) || end - 1 == i)
                {
                    return null;
                }

                for (var j = i + 1; j < end - 1; j++)
                {
                    sb.Append(text[j]);
                    if (IsSingleQuote(text[j]))
                    {
                        j++;
                    }
                }

                i = end;
                continue;
            }

            if (IsDoubleQuote(c))
            {
                var end = SkipDoubleQuoted(text, i);
                if (end - 1 == i || !IsDoubleQuote(text[end - 1]))
                {
                    return null;
                }

                for (var j = i + 1; j < end - 1; j++)
                {
                    var d = text[j];
                    if (d is '$' or '`')
                    {
                        return null;
                    }

                    sb.Append(d);
                    if (IsDoubleQuote(d))
                    {
                        j++;
                    }
                }

                i = end;
                continue;
            }

            if (c is '$' or '`' or '(' or ')' or '{' or '}' || (c == '@' && i == 0))
            {
                return null;
            }

            sb.Append(c);
            i++;
        }

        return sb.ToString();
    }

    private static int SkipSpaces(string s, int i)
    {
        while (i < s.Length && s[i] is ' ' or '\t')
        {
            i++;
        }

        return i;
    }

    private static int TrimEnd(string s, int start, int end)
    {
        while (end > start && s[end - 1] is ' ' or '\t')
        {
            end--;
        }

        return end;
    }
}

/// <summary>Collects span replacements and applies them to the original text in one pass.</summary>
public sealed class TextEdits
{
    private readonly List<(int Start, int End, string Text)> _edits = [];

    public int Count => _edits.Count;

    public void Replace(int start, int end, string text) => _edits.Add((start, end, text));

    public string Apply(string input)
    {
        var sb = new StringBuilder(input.Length + 32);
        var position = 0;
        foreach (var (start, end, text) in _edits.OrderBy(e => e.Start))
        {
            if (start < position)
            {
                continue;
            }

            sb.Append(input, position, start - position).Append(text);
            position = end;
        }

        sb.Append(input, position, input.Length - position);
        return sb.ToString();
    }
}
