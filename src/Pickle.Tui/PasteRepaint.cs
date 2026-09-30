using Terminal.Gui.App;

namespace Pickle.Tui;

/// <summary>
/// After a paste, the next frame repaints the whole screen. On Windows, pasted text has stayed invisible in a panel's
/// text box until a later edit redrew the box, although the text was in it and the cursor had moved past it; a
/// repaint shows it either way. A burst of printable keys within one frame counts as a paste too, because terminals
/// that don't bracket a paste type it.
/// </summary>
internal static class PasteRepaint
{
    public static void Attach(IApplication app)
    {
        var pending = false;
        var typed = 0;
        app.Paste += (_, _) => pending = true;
        app.Keyboard.KeyDown += (_, key) =>
        {
            if (!key.IsCtrl && !key.IsAlt && key.AsRune.Value >= ' ' && ++typed > 1)
            {
                pending = true;
            }
        };

        app.LayoutAndDrawComplete += (_, _) =>
        {
            typed = 0;
            if (pending)
            {
                pending = false;
                app.ClearScreenNextIteration = true;
            }
        };
    }
}
