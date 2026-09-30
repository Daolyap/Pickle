using Pickle.Tui.Panels.Palette;
using Terminal.Gui.App;
using Terminal.Gui.Drivers;
using Terminal.Gui.Input;
using Terminal.Gui.Testing;

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

    [Theory]
    [InlineData("bracketed paste", true)]
    [InlineData("two keys in one frame", true)]
    [InlineData("one key", false)]
    public void APasteRepaintsTheWholeScreenOnTheNextFrame(string input, bool repaints)
    {
        var requested = false;
        var script = new UiScript()
            .Do("watch", app => app.LayoutAndDrawComplete += (_, _) => requested |= app.ClearScreenNextIteration)
            .Do(input, app =>
            {
                switch (input)
                {
                    case "bracketed paste":
                        SendRaw(app, "\u001b[200~pk vers\u001b[201~");
                        break;
                    case "two keys in one frame":
                        app.InjectKey(new Key('p'));
                        app.InjectKey(new Key('k'));
                        break;
                    default:
                        app.InjectKey(new Key('p'));
                        break;
                }
            })
            .WaitFor("typed", app => TuiHarness.Top<PalettePanel>(app).List.FilterText.Length > 0)
            .Do("a few frames", _ => { })
            .Do("more frames", _ => { })
            .Press(Key.Esc);
        var (t, host) = TuiHarness.Start();
        using var _ = t;
        TuiHarness.Use(host, script, TimeSpan.FromSeconds(10));

        host.Show(PalettePanelPlugin.PanelId);

        script.AssertOk();
        Assert.Equal(repaints, requested);
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
