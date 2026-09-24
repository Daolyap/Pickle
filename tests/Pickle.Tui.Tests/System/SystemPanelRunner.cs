using Pickle.Tui.Tests.Windows;
using Terminal.Gui.Views;

namespace Pickle.Tui.Tests.SystemMonitoring;

internal static class SystemPanelRunner
{
    /// <summary><see cref="PanelRunner.Run"/>, returning the screen as drawn after the last step.</summary>
    public static string RunAndDraw(Window panel, params (Func<bool> Until, Action Then)[] steps)
    {
        var screen = string.Empty;
        PanelRunner.Run(panel, [.. steps, PanelRunner.Do(() => screen = TuiHarness.Screen(panel.App!))]);
        if (Environment.GetEnvironmentVariable("PICKLE_DUMP_SCREENS") is { Length: > 0 } dir)
        {
            File.AppendAllText(Path.Combine(dir, panel.GetType().Name + ".txt"), screen + "\n\n");
        }

        return screen;
    }
}
