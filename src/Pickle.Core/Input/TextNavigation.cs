using System.Globalization;

namespace Pickle.Core.Input;

/// <summary>Cursor arithmetic over the input text: grapheme steps, PowerShell-flavoured words, logical lines.</summary>
public static class TextNavigation
{
    /// <summary>
    /// Word characters: letters, digits, '_', '-' and '$', so <c>Get-ChildItem</c>, <c>-Force</c> and <c>$env</c> are
    /// single words while path separators, ':', '.', quotes and brackets split words.
    /// </summary>
    public static bool IsWordChar(char c) => char.IsLetterOrDigit(c) || c is '_' or '-' or '$';

    public static int NextGrapheme(string text, int index) =>
        index >= text.Length ? text.Length : index + StringInfo.GetNextTextElementLength(text, index);

    public static int PreviousGrapheme(string text, int index)
    {
        if (index <= 0)
        {
            return 0;
        }

        var start = LineStart(text, index - 1);
        var previous = start;
        while (start < index)
        {
            previous = start;
            start = NextGrapheme(text, start);
        }

        return previous;
    }

    /// <summary>Start of the word at or before <paramref name="index"/> (skips separators first).</summary>
    public static int WordStartBefore(string text, int index)
    {
        var i = Math.Clamp(index, 0, text.Length);
        while (i > 0 && !IsWordChar(text[i - 1]))
        {
            i--;
        }

        while (i > 0 && IsWordChar(text[i - 1]))
        {
            i--;
        }

        return i;
    }

    /// <summary>End of the word at or after <paramref name="index"/> (skips separators first).</summary>
    public static int WordEndAfter(string text, int index)
    {
        var i = Math.Clamp(index, 0, text.Length);
        while (i < text.Length && !IsWordChar(text[i]))
        {
            i++;
        }

        while (i < text.Length && IsWordChar(text[i]))
        {
            i++;
        }

        return i;
    }

    public static int LineStart(string text, int index)
    {
        if (index <= 0)
        {
            return 0;
        }

        var nl = text.LastIndexOf('\n', Math.Min(index, text.Length) - 1);
        return nl < 0 ? 0 : nl + 1;
    }

    public static int LineEnd(string text, int index)
    {
        var nl = text.IndexOf('\n', Math.Clamp(index, 0, text.Length));
        return nl < 0 ? text.Length : nl;
    }

    public static int LineIndex(string text, int index)
    {
        var count = 0;
        var end = Math.Min(index, text.Length);
        for (var i = 0; i < end; i++)
        {
            if (text[i] == '\n')
            {
                count++;
            }
        }

        return count;
    }

    public static int LineCount(string text) => LineIndex(text, text.Length) + 1;

    /// <summary>Display column of <paramref name="index"/> within its logical line.</summary>
    public static int ColumnOf(string text, int index)
    {
        var i = LineStart(text, index);
        var width = 0;
        while (i < index)
        {
            var next = NextGrapheme(text, i);
            width += Math.Max(0, Render.FrameBuilder.SafeWidth(text[i..next]));
            i = next;
        }

        return width;
    }

    /// <summary>Index on the line starting at <paramref name="lineStart"/> closest to display column <paramref name="column"/>.</summary>
    public static int IndexAtColumn(string text, int lineStart, int column)
    {
        var end = LineEnd(text, lineStart);
        var i = lineStart;
        var width = 0;
        while (i < end)
        {
            var next = NextGrapheme(text, i);
            var w = Math.Max(0, Render.FrameBuilder.SafeWidth(text[i..next]));
            if (width + w > column)
            {
                break;
            }

            width += w;
            i = next;
        }

        return i;
    }
}
