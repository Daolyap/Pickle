using Pickle.Abstractions;
using Terminal.Gui.ViewBase;

namespace Pickle.Tui;

/// <summary>
/// Maps the active Pickle theme (Theme.Ui colors) onto Terminal.Gui schemes.
/// FOUNDATION VERSION — workstream W6 implements the real mapping; panels just call Apply.
/// </summary>
public static class PanelStyle
{
    public static void Apply(View view, IPickleContext pickle)
    {
    }
}
