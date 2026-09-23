using System.Globalization;
using System.Text;
using Pickle.Abstractions;

namespace Pickle.Core.Render;

/// <summary>
/// Lays text out into <see cref="Frame"/> rows: soft-wraps at the width (wide characters never split), expands tabs,
/// shows control characters in caret notation, and parses ANSI strings (SGR becomes cell styles, other escape
/// sequences are carried along zero-width).
/// </summary>
public sealed class FrameBuilder
{
    private const int TabSize = 4;
    private readonly List<List<Cell>> _rows = [[]];
    private readonly int _width;
    private readonly int _indent;
    private (int Row, int Column)? _cursor;
    private bool _cursorPending;
    private string _ansiStyle = string.Empty;
    private string _zeroWidth = string.Empty;
    private bool _clipping;

    public FrameBuilder(int width, int indent = 0)
    {
        _width = Math.Max(1, width);
        _indent = Math.Clamp(indent, 0, _width - 1);
    }

    public int Width => _width;

    public int Row => _rows.Count - 1;

    public int Column => _rows[^1].Count;

    public int RowWidth(int row) => row == 0 ? _width - _indent : _width;

    /// <summary>The next written element (or the end of the frame) is where the cursor goes.</summary>
    public void MarkCursor()
    {
        _cursor = null;
        _cursorPending = true;
    }

    public void NewLine()
    {
        ResolvePendingCursor();
        _rows.Add([]);
        _clipping = false;
    }

    /// <summary>Plain text in one style. '\n' starts a new row.</summary>
    public void Write(string text, string style)
    {
        if (text.Length == 1)
        {
            WriteElement(text, style);
            return;
        }

        var index = 0;
        while (index < text.Length)
        {
            var length = StringInfo.GetNextTextElementLength(text, index);
            WriteElement(text.Substring(index, length), style);
            index += length;
        }
    }

    /// <summary>
    /// Text with ANSI sequences. With <paramref name="clip"/>, characters past the row end are dropped instead of
    /// wrapping (menus, status lines).
    /// </summary>
    public void WriteAnsi(string text, bool clip = false)
    {
        var i = 0;
        while (i < text.Length)
        {
            if (text[i] == '\u001b')
            {
                i = ParseEscape(text, i);
                continue;
            }

            var next = text.IndexOf('\u001b', i);
            var end = next < 0 ? text.Length : next;
            var run = text[i..end];
            if (clip)
            {
                WriteClipped(run);
            }
            else
            {
                Write(run, _ansiStyle);
            }

            i = end;
        }

        FlushZeroWidth();
        _ansiStyle = string.Empty;
    }

    /// <summary>Visible width of <paramref name="ansi"/> when laid out by this builder (tabs, caret notation...).</summary>
    public static int MeasureAnsi(string ansi)
    {
        var builder = new FrameBuilder(int.MaxValue / 2);
        builder.WriteAnsi(ansi.Replace("\n", " ", StringComparison.Ordinal));
        return builder.Column;
    }

    /// <summary>Right-aligns <paramref name="ansi"/> on <paramref name="row"/> if it fits after the existing content with a gap.</summary>
    public bool TryPlaceRight(int row, string ansi, int gap = 1)
    {
        if (row < 0 || row >= _rows.Count)
        {
            return false;
        }

        var measure = new FrameBuilder(RowWidth(row));
        measure.WriteAnsi(ansi.Split('\n')[0], clip: true);
        var cells = measure._rows[0];
        var target = _rows[row];
        var rowWidth = RowWidth(row);
        if (cells.Count == 0 || target.Count + gap + cells.Count > rowWidth)
        {
            return false;
        }

        while (target.Count < rowWidth - cells.Count)
        {
            target.Add(Cell.Blank);
        }

        target.AddRange(cells);
        return true;
    }

    public Frame Build()
    {
        ResolvePendingCursor();
        var cursor = _cursor ?? (Row, Column);
        var rows = new Cell[_rows.Count][];
        for (var r = 0; r < _rows.Count; r++)
        {
            var row = _rows[r];
            var length = row.Count;
            while (length > 0 && row[length - 1].IsBlank)
            {
                length--;
            }

            rows[r] = length == row.Count ? [.. row] : [.. row.GetRange(0, length)];
        }

        return new Frame(rows, cursor.Row, cursor.Column, _width, _indent);
    }

    private void WriteElement(string element, string style)
    {
        if (element is "\n" or "\r\n")
        {
            NewLine();
            return;
        }

        if (element == "\r")
        {
            return;
        }

        if (element == "\t")
        {
            var spaces = TabSize - (Column % TabSize);
            for (var s = 0; s < spaces; s++)
            {
                WriteElement(" ", style);
            }

            return;
        }

        var first = element[0];
        if (first < ' ' || first == '\u007f')
        {
            WriteElement("^", style);
            WriteElement(((char)(first ^ 0x40)).ToString(), style);
            return;
        }

        var width = SafeWidth(element);
        if (width < 0 || first is >= '\u0080' and < '\u00a0')
        {
            element = "\uFFFD";
            width = 1;
        }

        if (width == 0)
        {
            AttachZeroWidth(element);
            return;
        }

        var rowWidth = RowWidth(Row);
        if (width > rowWidth)
        {
            element = "?";
            width = 1;
        }

        if (Column + width > rowWidth)
        {
            if (_clipping)
            {
                return;
            }

            _rows.Add([]);
        }

        if (_cursorPending)
        {
            _cursor = (Row, Column);
            _cursorPending = false;
        }

        var text = _zeroWidth.Length > 0 ? _zeroWidth + element : element;
        _zeroWidth = string.Empty;
        var row = _rows[^1];
        row.Add(new Cell(text, style));
        if (width == 2)
        {
            row.Add(new Cell(null, style));
        }
    }

    private void WriteClipped(string run)
    {
        var index = 0;
        while (index < run.Length)
        {
            var length = StringInfo.GetNextTextElementLength(run, index);
            var element = run.Substring(index, length);
            index += length;
            if (element is "\n" or "\r\n")
            {
                NewLine();
                continue;
            }

            var width = element == "\t" ? TabSize - (Column % TabSize) : Math.Max(1, SafeWidth(element));
            if (Column + width > RowWidth(Row))
            {
                _clipping = true;
                continue;
            }

            WriteElement(element, _ansiStyle);
        }
    }

    private void ResolvePendingCursor()
    {
        if (!_cursorPending)
        {
            return;
        }

        _cursorPending = false;
        if (Column >= RowWidth(Row))
        {
            _rows.Add([]);
        }

        _cursor = (Row, Column);
    }

    private int ParseEscape(string text, int i)
    {
        if (i + 1 >= text.Length)
        {
            return text.Length;
        }

        var next = text[i + 1];
        if (next == '[')
        {
            var j = i + 2;
            while (j < text.Length && (text[j] < '@' || text[j] > '~'))
            {
                j++;
            }

            if (j >= text.Length)
            {
                return text.Length;
            }

            if (text[j] == 'm')
            {
                ApplySgr(text[(i + 2)..j], text[i..(j + 1)]);
            }

            return j + 1;
        }

        if (next is ']' or 'P' or '_')
        {
            var j = i + 2;
            while (j < text.Length && text[j] != '\u0007' && !(text[j] == '\u001b' && j + 1 < text.Length && text[j + 1] == '\\'))
            {
                j++;
            }

            var end = j >= text.Length ? text.Length : (text[j] == '\u0007' ? j + 1 : j + 2);
            _zeroWidth += text[i..end];
            return end;
        }

        return i + 2;
    }

    private void ApplySgr(string parameters, string sequence)
    {
        if (parameters.Length == 0 || parameters == "0")
        {
            _ansiStyle = string.Empty;
        }
        else if (parameters.StartsWith("0;", StringComparison.Ordinal))
        {
            _ansiStyle = $"\u001b[{parameters[2..]}m";
        }
        else
        {
            _ansiStyle += sequence;
        }
    }

    private void AttachZeroWidth(string element)
    {
        var row = _rows[^1];
        for (var c = row.Count - 1; c >= 0; c--)
        {
            if (row[c].Text is { } text)
            {
                row[c] = row[c] with { Text = text + element };
                return;
            }
        }

        _zeroWidth += element;
    }

    // Escape sequences with nothing after them (e.g. an OSC 133 mark at the very end of a prompt) ride on the last cell.
    private void FlushZeroWidth()
    {
        if (_zeroWidth.Length == 0)
        {
            return;
        }

        var row = _rows[^1];
        for (var c = row.Count - 1; c >= 0; c--)
        {
            if (row[c].Text is { } text)
            {
                row[c] = row[c] with { Text = text + _zeroWidth };
                _zeroWidth = string.Empty;
                return;
            }
        }
    }

    /// <summary>Column width of one text element; -1 for invalid UTF-16 (a lone surrogate).</summary>
    internal static int SafeWidth(string element) =>
        Rune.DecodeFromUtf16(element, out var rune, out _) == System.Buffers.OperationStatus.Done ? TextWidth.RuneWidth(rune) : -1;
}
