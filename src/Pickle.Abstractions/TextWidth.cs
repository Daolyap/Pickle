using System.Globalization;
using System.Text;

namespace Pickle.Abstractions;

/// <summary>Terminal column width helpers (ANSI-aware, East Asian wide and zero-width aware).</summary>
public static class TextWidth
{
    /// <summary>Removes CSI (ESC [ ... final) and OSC (ESC ] ... BEL/ST) sequences.</summary>
    public static string StripAnsi(string text)
    {
        if (text.IndexOf('\u001b') < 0)
        {
            return text;
        }

        var sb = new StringBuilder(text.Length);
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (c != '\u001b')
            {
                sb.Append(c);
                continue;
            }

            if (i + 1 >= text.Length)
            {
                break;
            }

            var next = text[i + 1];
            if (next == '[')
            {
                i += 2;
                while (i < text.Length && (text[i] < '@' || text[i] > '~'))
                {
                    i++;
                }
            }
            else if (next == ']')
            {
                i += 2;
                while (i < text.Length)
                {
                    if (text[i] == '\u0007')
                    {
                        break;
                    }

                    if (text[i] == '\u001b' && i + 1 < text.Length && text[i + 1] == '\\')
                    {
                        i++;
                        break;
                    }

                    i++;
                }
            }
            else
            {
                i++;
            }
        }

        return sb.ToString();
    }

    /// <summary>Visible width in terminal columns, ignoring ANSI sequences.</summary>
    public static int VisibleWidth(string text)
    {
        var plain = StripAnsi(text);
        var width = 0;
        var enumerator = StringInfo.GetTextElementEnumerator(plain);
        while (enumerator.MoveNext())
        {
            width += ElementWidth((string)enumerator.Current);
        }

        return width;
    }

    /// <summary>Width of one grapheme cluster.</summary>
    public static int ElementWidth(string element)
    {
        if (element.Length == 0)
        {
            return 0;
        }

        var rune = Rune.GetRuneAt(element, 0);
        return RuneWidth(rune);
    }

    public static int RuneWidth(Rune rune)
    {
        var cp = rune.Value;
        if (cp == 0)
        {
            return 0;
        }

        if (cp < 32 || (cp >= 0x7F && cp < 0xA0))
        {
            return 0;
        }

        var category = Rune.GetUnicodeCategory(rune);
        if (category is UnicodeCategory.NonSpacingMark or UnicodeCategory.EnclosingMark or UnicodeCategory.Format)
        {
            return 0;
        }

        if (cp == 0x200B)
        {
            return 0;
        }

        return IsWide(cp) ? 2 : 1;
    }

    private static bool IsWide(int cp) =>
        (cp >= 0x1100 && cp <= 0x115F) ||
        cp == 0x2329 || cp == 0x232A ||
        (cp >= 0x2E80 && cp <= 0x303E) ||
        (cp >= 0x3041 && cp <= 0x33FF) ||
        (cp >= 0x3400 && cp <= 0x4DBF) ||
        (cp >= 0x4E00 && cp <= 0x9FFF) ||
        (cp >= 0xA000 && cp <= 0xA4CF) ||
        (cp >= 0xAC00 && cp <= 0xD7A3) ||
        (cp >= 0xF900 && cp <= 0xFAFF) ||
        (cp >= 0xFE30 && cp <= 0xFE4F) ||
        (cp >= 0xFF00 && cp <= 0xFF60) ||
        (cp >= 0xFFE0 && cp <= 0xFFE6) ||
        (cp >= 0x1F300 && cp <= 0x1F64F) ||
        (cp >= 0x1F900 && cp <= 0x1F9FF) ||
        (cp >= 0x1FA70 && cp <= 0x1FAFF) ||
        (cp >= 0x20000 && cp <= 0x3FFFD);

    /// <summary>Truncate plain text to at most <paramref name="maxWidth"/> columns, appending an ellipsis if cut.</summary>
    public static string Truncate(string plain, int maxWidth, string ellipsis = "…")
    {
        if (maxWidth <= 0)
        {
            return string.Empty;
        }

        if (VisibleWidth(plain) <= maxWidth)
        {
            return plain;
        }

        var budget = maxWidth - VisibleWidth(ellipsis);
        var sb = new StringBuilder();
        var used = 0;
        var enumerator = StringInfo.GetTextElementEnumerator(plain);
        while (enumerator.MoveNext())
        {
            var element = (string)enumerator.Current;
            var w = ElementWidth(element);
            if (used + w > budget)
            {
                break;
            }

            sb.Append(element);
            used += w;
        }

        return sb.Append(ellipsis).ToString();
    }

    /// <summary>Pad plain text with spaces on the right to exactly <paramref name="width"/> columns (truncating if needed).</summary>
    public static string PadRight(string plain, int width)
    {
        var truncated = Truncate(plain, width);
        var w = VisibleWidth(truncated);
        return w >= width ? truncated : truncated + new string(' ', width - w);
    }
}
