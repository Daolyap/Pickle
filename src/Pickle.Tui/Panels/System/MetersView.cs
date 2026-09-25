using Pickle.Abstractions;
using Pickle.Tui.Widgets;
using Terminal.Gui.ViewBase;
using Attribute = Terminal.Gui.Drawing.Attribute;

namespace Pickle.Tui.Panels.SystemMonitoring;

/// <summary>
/// One meter line: a label, a value and a sparkline of the history. <see cref="Level"/> (0–1) picks the sparkline
/// color: success, warning above 70%, error above 90%.
/// </summary>
internal sealed record MeterRow(string Label, string Value, IReadOnlyList<double> History, double? Max = null, double Level = 0);

/// <summary>Draws <see cref="MeterRow"/>s, one per line, with the sparkline filling the rest of the width.</summary>
internal sealed class MetersView : View, IThemedWidget
{
    private IReadOnlyList<MeterRow> _rows = [];

    public MetersView()
    {
        Width = Dim.Fill();
        Height = 1;
        CanFocus = false;
    }

    public PanelSchemes Schemes { get; set; } = PanelStyle.For(new Theme());

    public int LabelWidth { get; set; } = 8;

    public int ValueWidth { get; set; } = 22;

    public IReadOnlyList<MeterRow> Rows => _rows;

    /// <summary>All rows as plain text (tests).</summary>
    public string PlainText => string.Join('\n', _rows.Select(r => $"{r.Label} {r.Value}"));

    public void SetRows(IReadOnlyList<MeterRow> rows)
    {
        _rows = rows;
        SetNeedsDraw();
    }

    protected override bool OnDrawingContent(DrawContext? context)
    {
        var normal = Schemes.Base.Normal;
        var muted = new Attribute(Schemes.Muted.Foreground, normal.Background);
        var width = Viewport.Width;
        for (var y = 0; y < Viewport.Height; y++)
        {
            Move(0, y);
            if (y >= _rows.Count)
            {
                SetAttribute(normal);
                AddStr(new string(' ', Math.Max(0, width)));
                continue;
            }

            var row = _rows[y];
            var labelWidth = Math.Min(width, LabelWidth + 1);
            var valueWidth = Math.Min(width - labelWidth, ValueWidth + 1);
            var sparkWidth = width - labelWidth - valueWidth;
            SetAttribute(muted);
            AddStr(Fit(row.Label, labelWidth));
            SetAttribute(Schemes.Accent);
            AddStr(Fit(row.Value, valueWidth));
            if (sparkWidth > 0)
            {
                SetAttribute(row.Level >= 0.9 ? Schemes.ErrorText : row.Level >= 0.7 ? Schemes.Warning : Schemes.Success);
                AddStr(SystemFormat.Sparkline(row.History, sparkWidth, row.Max));
            }
        }

        return true;
    }

    private static string Fit(string text, int width) =>
        width <= 0 ? string.Empty : TextWidth.Truncate(text, width, string.Empty).PadRight(width);
}
