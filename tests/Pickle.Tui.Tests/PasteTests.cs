using Pickle.Tui.Panels.Palette;
using Terminal.Gui.App;
using Terminal.Gui.Drivers;
using Terminal.Gui.Input;

namespace Pickle.Tui.Tests;

public class PasteTests
{
    [Fact]
    public void TheFocusWorkaroundStillFindsTerminalGuisHook()
    {
        // If a Terminal.Gui upgrade renames it, pastes into panels silently go nowhere again.
        Assert.True(FocusSync.Available);
    }

    [Fact]
    public void BracketedPasteReachesThePanelsFocusedTextField()
    {
        string? text = null;
        var script = new UiScript()
            .Do("paste", app => SendRaw(app, "\u001b[200~pk vers\u001b[201~"))
            .WaitFor("pasted", app => TuiHarness.Top<PalettePanel>(app).List.FilterText.Length > 0)
            .Do("read", app => text = TuiHarness.Top<PalettePanel>(app).List.FilterText)
            .Press(Key.Esc);
        var (t, host) = TuiHarness.Start();
        using var _ = t;
        TuiHarness.Use(host, script, TimeSpan.FromSeconds(10));

        host.Show(PalettePanelPlugin.PanelId);

        script.AssertOk();
        Assert.Equal("pk vers", text);
    }

    private static void SendRaw(IApplication app, string sequence)
    {
        var input = (ITestableInput<char>)((InputProcessorImpl<char>)app.Driver!.GetInputProcessor()).InputImpl!;
        foreach (var c in sequence)
        {
            input.InjectInput(c);
        }
    }
}
