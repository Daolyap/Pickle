using Pickle.Tui.Widgets;
using Terminal.Gui.ViewBase;
using Attribute = Terminal.Gui.Drawing.Attribute;

namespace Pickle.Tui.Panels.Dashboard;

/// <summary>Draws a <see cref="DashboardCanvas"/> produced for the view's current size, mapping roles to theme attributes.</summary>
internal sealed class CanvasView : View, IThemedWidget
{
    private readonly Func<int, int, DashboardCanvas?> _render;

    public CanvasView(Func<int, int, DashboardCanvas?> render)
    {
        _render = render;
        Width = Dim.Fill();
        Height = Dim.Fill();
        CanFocus = true;
    }

    public PanelSchemes Schemes { get; set; } = PanelStyle.For(new Abstractions.Theme());

    /// <summary>The last frame drawn (tests).</summary>
    public DashboardCanvas? LastFrame { get; private set; }

    protected override bool OnDrawingContent(DrawContext? context)
    {
        var width = Viewport.Width;
        var height = Viewport.Height;
        var canvas = _render(width, height);
        LastFrame = canvas;
        var y = 0;
        if (canvas is not null)
        {
            foreach (var line in canvas.Runs())
            {
                if (y >= height)
                {
                    break;
                }

                Move(0, y++);
                foreach (var (text, role) in line)
                {
                    SetAttribute(Attr(role));
                    AddStr(text);
                }
            }
        }

        SetAttribute(Schemes.Normal);
        for (; y < height; y++)
        {
            Move(0, y);
            AddStr(new string(' ', Math.Max(0, width)));
        }

        return true;
    }

    private Attribute Attr(CellRole role)
    {
        var background = Schemes.Normal.Background;
        return role switch
        {
            CellRole.Muted => Schemes.Muted,
            CellRole.Accent => Schemes.Accent,
            CellRole.Title => new Attribute(Schemes.Accent.Foreground, background, Terminal.Gui.Drawing.TextStyle.Bold),
            CellRole.Border => new Attribute(Schemes.Border.Normal.Foreground, background),
            CellRole.Success => Schemes.Success,
            CellRole.Warning => Schemes.Warning,
            CellRole.Error => Schemes.ErrorText,
            CellRole.Info => Schemes.Info,
            _ => Schemes.Normal,
        };
    }
}
