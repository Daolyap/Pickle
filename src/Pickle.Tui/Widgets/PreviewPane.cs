using System.Globalization;
using Pickle.Abstractions;
using Terminal.Gui.Drawing;
using Terminal.Gui.Input;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;
using Attribute = Terminal.Gui.Drawing.Attribute;

namespace Pickle.Tui.Widgets;

/// <summary>One line of preview content; <see cref="Color"/> overrides the foreground.</summary>
public readonly record struct PreviewLine(string Text, Color? Color = null, bool Muted = false);

/// <summary>
/// A titled, read-only, scrollable text pane for previews and details (file heads with line numbers, directory
/// listings, job output). Scroll with Shift+Up/Down or PgUp/PgDn when focused, or the mouse wheel.
/// </summary>
public class PreviewPane : FrameView, IThemedWidget
{
    private readonly ContentView _content;

    public PreviewPane(string title = "Preview")
    {
        Title = title;
        Width = Dim.Fill();
        Height = Dim.Fill();
        CanFocus = false;
        _content = new ContentView(this) { X = 0, Y = 0, Width = Dim.Fill(), Height = Dim.Fill() };
        Add(_content);
    }

    public PanelSchemes Schemes { get; set; } = PanelStyle.For(new Theme());

    public IReadOnlyList<PreviewLine> Lines { get; private set; } = [];

    public bool LineNumbers { get; private set; }

    public int ScrollOffset { get; private set; }

    /// <summary>All lines as plain text (handy for tests and copying).</summary>
    public string PlainText => string.Join('\n', Lines.Select(l => l.Text));

    public void Show(string title, IEnumerable<PreviewLine> lines, bool lineNumbers = false)
    {
        Title = title;
        Lines = [.. lines];
        LineNumbers = lineNumbers;
        ScrollOffset = 0;
        SetNeedsDraw();
        _content.SetNeedsDraw();
    }

    public void Show(string title, IEnumerable<string> lines, bool lineNumbers = false) =>
        Show(title, lines.Select(l => new PreviewLine(l)), lineNumbers);

    public void ShowMessage(string title, string message) =>
        Show(title, message.Split('\n').Select(l => new PreviewLine(l, Muted: true)));

    public void Clear() => Show(string.Empty, Array.Empty<PreviewLine>());

    public void ScrollBy(int delta)
    {
        var max = Math.Max(0, Lines.Count - Math.Max(1, _content.Viewport.Height));
        ScrollOffset = Math.Clamp(ScrollOffset + delta, 0, max);
        _content.SetNeedsDraw();
    }

    protected override bool OnKeyDown(Key key)
    {
        var page = Math.Max(1, _content.Viewport.Height - 1);
        if (key == Key.CursorDown.WithShift || key == Key.CursorDown)
        {
            ScrollBy(1);
        }
        else if (key == Key.CursorUp.WithShift || key == Key.CursorUp)
        {
            ScrollBy(-1);
        }
        else if (key == Key.PageDown)
        {
            ScrollBy(page);
        }
        else if (key == Key.PageUp)
        {
            ScrollBy(-page);
        }
        else
        {
            return base.OnKeyDown(key);
        }

        return true;
    }

    private sealed class ContentView(PreviewPane owner) : View
    {
        protected override bool OnMouseEvent(Mouse mouse)
        {
            if (mouse.Flags.HasFlag(MouseFlags.WheeledDown))
            {
                owner.ScrollBy(3);
                return true;
            }

            if (mouse.Flags.HasFlag(MouseFlags.WheeledUp))
            {
                owner.ScrollBy(-3);
                return true;
            }

            return base.OnMouseEvent(mouse);
        }

        protected override bool OnDrawingContent(DrawContext? context)
        {
            var schemes = owner.Schemes;
            var baseScheme = schemes.Base;
            var normal = baseScheme.Normal;
            var muted = new Attribute(schemes.Muted.Foreground, normal.Background);
            var width = Viewport.Width;
            var height = Viewport.Height;
            var lines = owner.Lines;
            var gutter = owner.LineNumbers ? Math.Max(3, (owner.ScrollOffset + height).ToString(CultureInfo.InvariantCulture).Length) + 1 : 0;
            for (var row = 0; row < height; row++)
            {
                var index = owner.ScrollOffset + row;
                Move(0, row);
                if (index >= lines.Count)
                {
                    SetAttribute(normal);
                    AddStr(new string(' ', Math.Max(0, width)));
                    continue;
                }

                var line = lines[index];
                var used = 0;
                if (gutter > 0)
                {
                    SetAttribute(muted);
                    var number = (index + 1).ToString(CultureInfo.InvariantCulture).PadLeft(gutter - 1) + " ";
                    AddStr(TextWidth.Truncate(number, width, string.Empty));
                    used = Math.Min(width, gutter);
                }

                var attribute = line.Muted ? muted : line.Color is { } c ? new Attribute(c, normal.Background) : normal;
                var text = TextWidth.Truncate(line.Text.Replace("\t", "    ", StringComparison.Ordinal), Math.Max(0, width - used), string.Empty);
                SetAttribute(attribute);
                AddStr(text);
                used += TextWidth.VisibleWidth(text);
                if (used < width)
                {
                    SetAttribute(normal);
                    AddStr(new string(' ', width - used));
                }
            }

            return true;
        }
    }
}
