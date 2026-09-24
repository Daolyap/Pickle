using System.Text;
using Pickle.Abstractions;
using Pickle.Core.Terminal;

namespace Pickle.Core.Render;

/// <summary>
/// Draws <see cref="Frame"/>s at the current terminal position and keeps them up to date with minimal output: each
/// render diffs against the previous frame cell by cell and only rewrites changed spans.
/// <para>
/// All positions are tracked relative to the frame origin (row 0) because the absolute row is unknown and changes
/// whenever the terminal scrolls. New rows below the ones already on screen are created with CR LF (which scrolls
/// at the bottom); rows that exist are reached with relative cursor moves. Rows scrolled off the top can no longer
/// be reached and are left alone.
/// </para>
/// </summary>
public sealed class FrameRenderer
{
    private readonly ITerminal _terminal;
    private Frame? _previous;
    private int _cursorRow;
    private int _cursorColumn;
    private int _rowsOnScreen = 1;
    private int _width;
    private int _indent;
    private bool _fresh = true;
    private bool _invalid;
    private string _style = string.Empty;

    public FrameRenderer(ITerminal terminal)
    {
        _terminal = terminal;
        _width = terminal.Width;
    }

    /// <summary>The frame currently on screen, or null before the first render.</summary>
    public Frame? Current => _previous;

    /// <summary>Rows of the current frame that exist on screen (including ones that were cleared).</summary>
    public int RowsOnScreen => _rowsOnScreen;

    /// <summary>Start a new frame at the current cursor position, which is assumed to be column <paramref name="indent"/>.</summary>
    public void Reset(int indent = 0)
    {
        _previous = null;
        _cursorRow = 0;
        _cursorColumn = indent;
        _rowsOnScreen = 1;
        _width = _terminal.Width;
        _indent = indent;
        _fresh = true;
        _invalid = false;
        _style = string.Empty;
    }

    /// <summary>The next render clears the frame area and redraws everything.</summary>
    public void Invalidate() => _invalid = true;

    public void Render(Frame frame)
    {
        var sb = new StringBuilder(256);
        sb.Append(Ansi.BeginSynchronizedUpdate).Append(Ansi.HideCursor);
        var prefixLength = sb.Length;

        if (_fresh || _invalid || frame.Width != _width || frame.Indent != _indent)
        {
            if (!_fresh)
            {
                MoveTo(sb, MinReachableRow, 0);
            }

            sb.Append(Ansi.ClearToEndOfScreen);
            _previous = null;
            _fresh = false;
            _invalid = false;
            _width = frame.Width;
            _indent = frame.Indent;
        }

        var old = _previous?.Rows ?? [];
        for (var r = 0; r < frame.Rows.Count; r++)
        {
            if (r >= MinReachableRow)
            {
                DiffRow(sb, r, r < old.Count ? old[r] : [], frame.Rows[r]);
            }
        }

        if (old.Count > frame.Rows.Count)
        {
            MoveTo(sb, Math.Max(frame.Rows.Count, MinReachableRow), 0);
            ResetStyle(sb);
            sb.Append(Ansi.ClearToEndOfScreen);
        }

        MoveTo(sb, frame.CursorRow, frame.CursorColumn);
        _previous = frame;
        if (sb.Length == prefixLength)
        {
            return;
        }

        ResetStyle(sb);
        sb.Append(Ansi.ShowCursor).Append(Ansi.EndSynchronizedUpdate);
        _terminal.Write(sb.ToString());
        _terminal.Flush();
    }

    /// <summary>Leave the frame as drawn, move to the start of the line below it and forget it.</summary>
    public void Finish()
    {
        if (_previous is not null && !_fresh)
        {
            var sb = new StringBuilder();
            MoveTo(sb, _previous.Rows.Count - 1, 0);
            ResetStyle(sb);
            sb.Append("\r\n");
            _terminal.Write(sb.ToString());
            _terminal.Flush();
        }

        Reset();
    }

    private int MinReachableRow => Math.Max(0, _rowsOnScreen - Math.Max(1, _terminal.Height));

    private void DiffRow(StringBuilder sb, int row, Cell[] oldRow, Cell[] newRow)
    {
        var max = Math.Max(oldRow.Length, newRow.Length);
        var first = -1;
        var last = -1;
        for (var c = 0; c < max; c++)
        {
            var o = c < oldRow.Length ? oldRow[c] : Cell.Blank;
            var n = c < newRow.Length ? newRow[c] : Cell.Blank;
            if (o != n)
            {
                if (first < 0)
                {
                    first = c;
                }

                last = c;
            }
        }

        if (first < 0)
        {
            return;
        }

        while (first > 0 && first < newRow.Length && newRow[first].IsWideTail)
        {
            first--;
        }

        var offset = row == 0 ? _indent : 0;
        var end = Math.Min(last, newRow.Length - 1);
        if (first <= end)
        {
            MoveTo(sb, row, first);
            for (var c = first; c <= end; c++)
            {
                var cell = newRow[c];
                if (cell.IsWideTail)
                {
                    continue;
                }

                SetStyle(sb, cell.Style);
                sb.Append(cell.Text);
                var wide = c + 1 < newRow.Length && newRow[c + 1].IsWideTail;
                _cursorColumn = offset + c + (wide ? 2 : 1);
            }

            // At the right margin the terminal is in the "pending wrap" state; don't trust the column until moved.
            if (_cursorColumn >= _width)
            {
                _cursorColumn = -1;
            }
        }

        if (last >= newRow.Length && offset + newRow.Length < _width)
        {
            MoveTo(sb, row, newRow.Length);
            ResetStyle(sb);
            sb.Append(Ansi.ClearToEndOfLine);
        }
    }

    private void MoveTo(StringBuilder sb, int row, int column)
    {
        row = Math.Max(row, MinReachableRow);
        if (row > _cursorRow)
        {
            var existing = Math.Min(row, _rowsOnScreen - 1);
            if (existing > _cursorRow)
            {
                sb.Append(Ansi.CursorDown(existing - _cursorRow));
                _cursorRow = existing;
            }

            if (_cursorRow < row)
            {
                // Erasing/scrolling uses the current background (BCE), so never create rows with a style active.
                ResetStyle(sb);
                while (_cursorRow < row)
                {
                    sb.Append("\r\n");
                    _cursorRow++;
                }

                _cursorColumn = 0;
                _rowsOnScreen = Math.Max(_rowsOnScreen, _cursorRow + 1);
            }
        }
        else if (row < _cursorRow)
        {
            sb.Append(Ansi.CursorUp(_cursorRow - row));
            _cursorRow = row;
        }

        var absolute = column + (row == 0 ? _indent : 0);
        if (_cursorColumn != absolute)
        {
            sb.Append(absolute == 0 ? "\r" : Ansi.CursorToColumn(absolute + 1));
            _cursorColumn = absolute;
        }
    }

    private void SetStyle(StringBuilder sb, string style)
    {
        if (style == _style)
        {
            return;
        }

        if (_style.Length > 0)
        {
            sb.Append(Ansi.Reset);
        }

        sb.Append(style);
        _style = style;
    }

    private void ResetStyle(StringBuilder sb)
    {
        if (_style.Length > 0)
        {
            sb.Append(Ansi.Reset);
            _style = string.Empty;
        }
    }
}
