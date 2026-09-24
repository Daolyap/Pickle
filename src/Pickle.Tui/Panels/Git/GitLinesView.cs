using Pickle.Abstractions;
using Terminal.Gui.Drawing;
using Terminal.Gui.Input;
using Terminal.Gui.ViewBase;
using Attribute = Terminal.Gui.Drawing.Attribute;

namespace Pickle.Tui.Panels.Git;

public enum GitRowStyle
{
    Normal,
    Header,
    Muted,
    Added,
    Removed,
    Hunk,
    Warning,
    Conflict,
}

public sealed record GitRow(string Text, GitRowStyle Style = GitRowStyle.Normal, object? Tag = null, bool Selectable = true);

/// <summary>A scrolling list of colored rows with a cursor (used for the status list, diffs, branches, log and stashes).</summary>
internal sealed class GitLinesView : View
{
    private readonly UiColors _colors;
    private IReadOnlyList<GitRow> _rows = [];
    private int _top;

    public GitLinesView(UiColors colors)
    {
        _colors = colors;
        CanFocus = true;
    }

    public IReadOnlyList<GitRow> Rows => _rows;

    public int Cursor { get; private set; } = -1;

    public GitRow? Current => Cursor >= 0 && Cursor < _rows.Count ? _rows[Cursor] : null;

    /// <summary>Optional one-column marker drawn left of each row (e.g. the selected hunk or marked lines).</summary>
    public Func<int, string?>? Gutter { get; set; }

    public event Action? CursorChanged;

    public void SetRows(IReadOnlyList<GitRow> rows, int cursor = 0)
    {
        _rows = rows;
        _top = 0;
        Cursor = -1;
        MoveTo(Math.Clamp(cursor, 0, Math.Max(0, rows.Count - 1)), preferForward: true, force: true);
        SetNeedsDraw();
    }

    public void MoveTo(int index, bool preferForward = true, bool force = false)
    {
        var target = FindSelectable(index, preferForward ? 1 : -1) ?? FindSelectable(index, preferForward ? -1 : 1) ?? -1;
        if (target == Cursor && !force)
        {
            return;
        }

        Cursor = target;
        EnsureVisible();
        SetNeedsDraw();
        CursorChanged?.Invoke();
    }

    /// <summary>Scrolls so <paramref name="index"/> is the top row (used to show a whole hunk after jumping to it).</summary>
    public void ScrollTo(int index)
    {
        _top = Math.Clamp(index, 0, Math.Max(0, _rows.Count - 1));
        EnsureVisible();
        SetNeedsDraw();
    }

    protected override bool OnKeyDown(Key key)
    {
        var page = Math.Max(1, Viewport.Height - 1);
        if (key == Key.CursorDown)
        {
            Step(1);
        }
        else if (key == Key.CursorUp)
        {
            Step(-1);
        }
        else if (key == Key.PageDown)
        {
            MoveTo(Math.Min(_rows.Count - 1, Math.Max(Cursor, 0) + page), preferForward: false);
        }
        else if (key == Key.PageUp)
        {
            MoveTo(Math.Max(0, Cursor - page), preferForward: true);
        }
        else if (key == Key.Home)
        {
            MoveTo(0, preferForward: true);
        }
        else if (key == Key.End)
        {
            MoveTo(_rows.Count - 1, preferForward: false);
        }
        else
        {
            return false;
        }

        return true;
    }

    protected override bool OnMouseEvent(Mouse mouse)
    {
        if (mouse.Flags.HasFlag(MouseFlags.WheeledDown))
        {
            _top = Math.Min(Math.Max(0, _rows.Count - Viewport.Height), _top + 3);
            SetNeedsDraw();
            return true;
        }

        if (mouse.Flags.HasFlag(MouseFlags.WheeledUp))
        {
            _top = Math.Max(0, _top - 3);
            SetNeedsDraw();
            return true;
        }

        if (mouse.Flags.HasFlag(MouseFlags.LeftButtonClicked) && mouse.Position is { } position)
        {
            SetFocus();
            MoveTo(_top + position.Y);
            return true;
        }

        return false;
    }

    protected override void OnHasFocusChanged(bool newHasFocus, View? previousFocusedView, View? focusedView) => SetNeedsDraw();

    protected override bool OnDrawingContent(DrawContext? context)
    {
        var normal = GetAttributeForRole(VisualRole.Normal);
        var width = Viewport.Width;
        var gutter = Gutter is null ? 0 : 1;
        EnsureVisible();
        for (var y = 0; y < Viewport.Height; y++)
        {
            var i = _top + y;
            Move(0, y);
            if (i >= _rows.Count)
            {
                SetAttribute(normal);
                AddStr(new string(' ', width));
                continue;
            }

            var row = _rows[i];
            var attr = RowAttribute(row.Style, normal);
            if (i == Cursor)
            {
                attr = HasFocus
                    ? new Attribute(ToColor(_colors.HighlightForeground) ?? normal.Foreground, ToColor(_colors.HighlightBackground) ?? normal.Background)
                    : new Attribute(attr.Foreground, ToColor(_colors.HighlightBackground) ?? normal.Background, attr.Style);
            }

            if (gutter > 0)
            {
                SetAttribute(new Attribute(ToColor(_colors.Accent) ?? normal.Foreground, normal.Background));
                AddStr(Gutter!(i) ?? " ");
            }

            SetAttribute(attr);
            AddStr(TextWidth.PadRight(TextWidth.Truncate(Sanitize(row.Text), Math.Max(0, width - gutter), string.Empty), Math.Max(0, width - gutter)));
        }

        return true;
    }

    private Attribute RowAttribute(GitRowStyle style, Attribute normal)
    {
        var (color, bold) = style switch
        {
            GitRowStyle.Header => (_colors.Accent, true),
            GitRowStyle.Muted => (_colors.Muted, false),
            GitRowStyle.Added => (_colors.Success, false),
            GitRowStyle.Removed => (_colors.Error, false),
            GitRowStyle.Hunk => (_colors.Info, false),
            GitRowStyle.Warning => (_colors.Warning, false),
            GitRowStyle.Conflict => (_colors.Error, true),
            _ => (null, false),
        };
        var fg = ToColor(color) ?? normal.Foreground;
        return bold ? new Attribute(fg, normal.Background, TextStyle.Bold) : new Attribute(fg, normal.Background);
    }

    private void Step(int direction)
    {
        var next = FindSelectable(Cursor + direction, direction);
        if (next is { } index)
        {
            MoveTo(index, preferForward: direction > 0);
        }
    }

    private int? FindSelectable(int start, int direction)
    {
        for (var i = start; i >= 0 && i < _rows.Count; i += direction)
        {
            if (_rows[i].Selectable)
            {
                return i;
            }
        }

        return null;
    }

    private void EnsureVisible()
    {
        var height = Math.Max(1, Viewport.Height);
        if (Cursor >= 0 && Cursor < _top)
        {
            _top = Cursor;
        }
        else if (Cursor >= _top + height)
        {
            _top = Cursor - height + 1;
        }

        _top = Math.Clamp(_top, 0, Math.Max(0, _rows.Count - 1));
    }

    private static string Sanitize(string text)
    {
        if (!text.Any(char.IsControl))
        {
            return text;
        }

        var chars = new System.Text.StringBuilder(text.Length);
        foreach (var c in text)
        {
            if (c == '\t')
            {
                chars.Append("    ");
            }
            else if (!char.IsControl(c))
            {
                chars.Append(c);
            }
        }

        return chars.ToString();
    }

    internal static Color? ToColor(string? value)
    {
        if (PickleColor.Parse(value) is not { } c)
        {
            return null;
        }

        if (c.IsRgb)
        {
            return new Color(c.R, c.G, c.B, 255);
        }

        ColorName16 name = c.AnsiIndex switch
        {
            0 => ColorName16.Black,
            1 => ColorName16.Red,
            2 => ColorName16.Green,
            3 => ColorName16.Yellow,
            4 => ColorName16.Blue,
            5 => ColorName16.Magenta,
            6 => ColorName16.Cyan,
            7 => ColorName16.Gray,
            8 => ColorName16.DarkGray,
            9 => ColorName16.BrightRed,
            10 => ColorName16.BrightGreen,
            11 => ColorName16.BrightYellow,
            12 => ColorName16.BrightBlue,
            13 => ColorName16.BrightMagenta,
            14 => ColorName16.BrightCyan,
            _ => ColorName16.White,
        };
        return new Color(name);
    }
}
