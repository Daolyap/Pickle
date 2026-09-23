namespace Pickle.Tui.Widgets;

/// <summary>A widget that draws with <see cref="PanelSchemes"/> attributes; <see cref="PanelWindow"/> updates it on theme changes.</summary>
public interface IThemedWidget
{
    PanelSchemes Schemes { get; set; }
}
