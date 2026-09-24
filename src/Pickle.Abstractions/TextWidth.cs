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
        var width = RuneWidth(rune);

        // VS16 requests emoji presentation, which terminals draw two columns wide (e.g. "⚙️").
        return width == 1 && element.Contains('\uFE0F', StringComparison.Ordinal) ? 2 : width;
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
        cp == 0x1F004 || cp == 0x1F0CF || cp == 0x1F18E ||
        (cp >= 0x1F191 && cp <= 0x1F19A) ||
        (cp >= 0x1F200 && cp <= 0x1F2FF) ||
        (cp >= 0x1F300 && cp <= 0x1F64F) ||
        (cp >= 0x1F680 && cp <= 0x1F6FF) ||
        (cp >= 0x1F7E0 && cp <= 0x1F7EB) ||
        (cp >= 0x1F900 && cp <= 0x1F9FF) ||
        (cp >= 0x1FA70 && cp <= 0x1FAFF) ||
        (cp >= 0x20000 && cp <= 0x3FFFD) ||
        (cp < 0x10000 && IsBmpEmojiPresentation(cp));

    // BMP characters with Emoji_Presentation=Yes (East Asian Width W since Unicode 9), e.g. ⌚ ⚡ ✅ ⭐.
    private static bool IsBmpEmojiPresentation(int cp) => cp switch
    {
        0x231A or 0x231B or 0x23F0 or 0x23F3 or 0x267F or 0x2693 or 0x26A1 or 0x26CE or 0x26D4 or 0x26EA
            or 0x26F5 or 0x26FA or 0x26FD or 0x2705 or 0x2728 or 0x274C or 0x274E or 0x2757 or 0x27B0 or 0x27BF
            or 0x2B50 or 0x2B55 => true,
        >= 0x23E9 and <= 0x23EC => true,
        >= 0x25FD and <= 0x25FE => true,
        >= 0x2614 and <= 0x2615 => true,
        >= 0x2648 and <= 0x2653 => true,
        >= 0x26AA and <= 0x26AB => true,
        >= 0x26BD and <= 0x26BE => true,
        >= 0x26C4 and <= 0x26C5 => true,
        >= 0x26F2 and <= 0x26F3 => true,
        >= 0x270A and <= 0x270B => true,
        >= 0x2753 and <= 0x2755 => true,
        >= 0x2795 and <= 0x2797 => true,
        >= 0x2B1B and <= 0x2B1C => true,
        _ => false,
    };

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
