using Pickle.Abstractions;
using Pickle.Tui.Panels.Palette;
using Terminal.Gui.Input;

namespace Pickle.Tui.Tests;

public class PalettePanelTests
{
    [Fact]
    public void CollectsPanelsCommandsActionsThemesAndSettingsWithChords()
    {
        var (t, _) = TuiHarness.Start();
        using var _ = t;

        var items = PaletteItem.Collect(t.Runtime);

        Assert.Contains(items, i => i is { Kind: PaletteKind.Panel, Target: "files", Chord: "Ctrl+T" });
        Assert.DoesNotContain(items, i => i is { Kind: PaletteKind.Panel, Target: "palette" });
        Assert.Contains(items, i => i is { Kind: PaletteKind.Command, Title: "pk version" });
        Assert.Contains(items, i => i is { Kind: PaletteKind.Action, Target: EditorActionNames.AcceptLine, Chord: "Enter" });
        Assert.Contains(items, i => i is { Kind: PaletteKind.Theme, Target: "pickle", Chord: "current" });
        Assert.Contains(items, i => i is { Kind: PaletteKind.Setting, Target: "editor.autosuggestions", Chord: "true" });
    }

    [Fact]
    public void FilteringAndEnterOnACommandReplacesTheInput()
    {
        var script = new UiScript()
            .Type("pk vers")
            .WaitFor("filtered", app => TuiHarness.Top<PalettePanel>(app).List.Selected?.Title == "pk version")
            .Press(Key.Enter);
        var (t, host) = TuiHarness.Start(script);
        using var _ = t;

        var result = host.Show(PalettePanelPlugin.PanelId);

        script.AssertOk();
        Assert.Equal(new PanelResult(PanelResultKind.ReplaceInput, "pk version "), result);
    }

    [Fact]
    public void SelectingAPanelOpensIt()
    {
        var script = new UiScript()
            .Type("about pickle")
            .WaitFor("filtered", app => TuiHarness.Top<PalettePanel>(app).List.Selected?.Target == "about")
            .Press(Key.Enter)
            .WaitFor("about open", app => app.TopRunnableView is PanelWindow { PanelTitle: "About Pickle" })
            .Press(Key.Esc);
        var (t, host) = TuiHarness.Start(script);
        using var _ = t;

        Assert.Null(host.Show(PalettePanelPlugin.PanelId));
        script.AssertOk();
    }

    [Fact]
    public void SelectingAThemeSwitchesItLive()
    {
        var script = new UiScript()
            .Type("minty")
            .WaitFor("filtered", app => TuiHarness.Top<PalettePanel>(app).List.Selected is { Kind: PaletteKind.Theme, Target: "minty" })
            .Press(Key.Enter);
        var (t, host) = TuiHarness.Start(script);
        using var _ = t;
        File.WriteAllText(Path.Combine(t.Paths.ThemesDir, "minty.json"), """{ "name": "minty", "ui": { "accent": "#00FFAA" } }""");

        host.Show(PalettePanelPlugin.PanelId);

        script.AssertOk();
        Assert.Equal("minty", t.Runtime.Themes.Current.Name);
        Assert.Equal("minty", t.Runtime.Config.Current.Theme);
    }

    [Fact]
    public async Task EditorActionRunsAgainstTheBufferAfterThePaletteCloses()
    {
        var script = new UiScript()
            .Type("shout-input")
            .WaitFor("filtered", app => TuiHarness.Top<PalettePanel>(app).List.Selected?.Target == "shout-input")
            .Press(Key.Enter);
        var (t, host) = TuiHarness.Start(script);
        using var _ = t;
        var buffer = new FakeBuffer(host) { Text = "some input" };
        t.Runtime.KeyBindings.RegisterAction("shout-input", "Uppercase the input", (b, _) =>
        {
            b.Replace(b.Text.ToUpperInvariant(), 0);
            return ValueTask.CompletedTask;
        });

        await t.Runtime.KeyBindings.GetAction(EditorActionNames.CommandPalette)!.Handler(buffer, CancellationToken.None);

        script.AssertOk();
        Assert.Equal(["palette"], buffer.PanelsShown);
        Assert.Equal("SOME INPUT", buffer.Text);
    }

    internal sealed class FakeBuffer(IPanelHost host) : IEditorBuffer
    {
        public string Text { get; set; } = string.Empty;

        public int Cursor { get; set; }

        public string? Suggestion => null;

        public List<string> PanelsShown { get; } = [];

        public PanelResult? LastResult { get; private set; }

        public void Insert(string text)
        {
            Text = Text.Insert(Cursor, text);
            Cursor += text.Length;
        }

        public void Replace(string text, int cursor)
        {
            Text = text;
            Cursor = Math.Clamp(cursor, 0, text.Length);
        }

        public void Accept()
        {
        }

        public void Redraw()
        {
        }

        public void OpenOverlay(IEditorOverlay overlay)
        {
        }

        public void CloseOverlay()
        {
        }

        public void ShowPanel(string panelId, string? argument = null)
        {
            PanelsShown.Add(panelId);
            LastResult = host.Show(panelId, argument, Text);
            if (LastResult is { Kind: PanelResultKind.InsertText } insert)
            {
                Insert(insert.Text);
            }
        }
    }
}
