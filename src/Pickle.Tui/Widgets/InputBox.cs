using Terminal.Gui.Drawing;
using Terminal.Gui.ViewBase;

namespace Pickle.Tui.Widgets;

/// <summary>Draws a full single-line box around an input so its extent is clear whatever the theme's input colors.</summary>
public static class InputBox
{
    /// <summary>Rows a boxed input takes besides its content (the top and bottom border).</summary>
    public const int Chrome = 2;

    public static T Boxed<T>(T field, int contentHeight = 1)
        where T : View
    {
        field.BorderStyle = LineStyle.Rounded;
        field.Border!.Thickness = new Thickness(1);
        field.Height = contentHeight + Chrome;
        return field;
    }
}
