using Pickle.Tui.Panels.Settings;
using Terminal.Gui.Input;
using Terminal.Gui.Views;

namespace Pickle.Tui.Tests;

public class SettingsPanelTests
{
    [Fact]
    public void CategoriesAndFieldsComeFromTheConfigClasses()
    {
        Assert.Equal(
            ["Theme", "Editor", "History", "Prompt", "Translation", "Shell", "Plugins", "Sync", "Terminal", "Winget", "Key bindings"],
            SettingsModel.Categories);

        var (t, _) = TuiHarness.Start();
        using var _ = t;
        var fields = SettingsModel.Fields(t.Runtime).ToDictionary(f => f.Path);
        Assert.Equal(SettingKind.Bool, fields["editor.autosuggestions"].Kind);
        Assert.Equal(SettingKind.Integer, fields["history.maxEntries"].Kind);
        Assert.Equal(SettingKind.Number, fields["terminal.fontSize"].Kind);
        Assert.True(fields["terminal.fontSize"].Nullable);
        Assert.Equal(SettingKind.Choice, fields["editor.bellStyle"].Kind);
        Assert.Equal(["none", "folder", "git"], fields["sync.backend"].Choices);
        Assert.Equal(SettingKind.List, fields["translation.disabled"].Kind);
        Assert.Contains("pickle", fields["theme"].Choices);
        Assert.Equal("Completion menu max rows", fields["editor.completionMenuMaxRows"].Label);
    }

    [Fact]
    public void TogglingABoolSavesTheConfigImmediately()
    {
        var script = new UiScript()
            .Do("focused checkbox", app => Assert.IsType<CheckBox>(TuiHarness.Top<SettingsPanel>(app).MostFocused))
            .Press(Key.Space)
            .WaitFor("saved", app => TuiHarness.Top<SettingsPanel>(app).StatusText.Contains("saved editor.autosuggestions", StringComparison.Ordinal))
            .Press(Key.Esc);
        var (t, host) = TuiHarness.Start(script);
        using var _ = t;

        host.Show(SettingsPanelPlugin.PanelId, "editor.autosuggestions");

        script.AssertOk();
        Assert.False(t.Runtime.Config.Current.Editor.Autosuggestions);
        Assert.Contains("\"autosuggestions\": false", File.ReadAllText(t.Paths.ConfigFile), StringComparison.Ordinal);
    }

    [Fact]
    public void TextFieldsValidateAndSaveOnEnter()
    {
        var script = new UiScript()
            .Do("type bad value", app =>
            {
                var field = Assert.IsType<TextField>(TuiHarness.Top<SettingsPanel>(app).EditorFor("history.maxEntries"));
                field.Text = "lots";
            })
            .Press(Key.Enter)
            .Do("error shown", app => Assert.Contains("whole number", TuiHarness.Top<SettingsPanel>(app).StatusText, StringComparison.Ordinal))
            .Do("type good value", app => ((TextField)TuiHarness.Top<SettingsPanel>(app).EditorFor("history.maxEntries")!).Text = "1234")
            .Press(Key.Enter)
            .Press(Key.Esc);
        var (t, host) = TuiHarness.Start(script);
        using var _ = t;

        host.Show(SettingsPanelPlugin.PanelId, "history.maxEntries");

        script.AssertOk();
        Assert.Equal(1234, t.Runtime.Config.Current.History.MaxEntries);
    }

    [Fact]
    public void ChoicesAndThemesSaveAndThemeChangesApplyLive()
    {
        var script = new UiScript()
            .Do("pick visual bell", app => ((DropDownList)TuiHarness.Top<SettingsPanel>(app).EditorFor("editor.bellStyle")!).Text = "visual")
            .Do("theme category", app => TuiHarness.Top<SettingsPanel>(app).ShowCategory("Theme"))
            .Do("pick theme", app => ((DropDownList)TuiHarness.Top<SettingsPanel>(app).EditorFor("theme")!).Text = "minty")
            .Press(Key.Esc);
        var (t, host) = TuiHarness.Start(script);
        using var _ = t;
        File.WriteAllText(Path.Combine(t.Paths.ThemesDir, "minty.json"), """{ "name": "minty" }""");

        host.Show(SettingsPanelPlugin.PanelId, "editor");

        script.AssertOk();
        Assert.Equal("visual", t.Runtime.Config.Current.Editor.BellStyle);
        Assert.Equal("minty", t.Runtime.Themes.Current.Name);
    }

    [Fact]
    public void KeyBindingsCanBeAddedAndUnbound()
    {
        var script = new UiScript()
            .Do("key bindings", app => TuiHarness.Top<SettingsPanel>(app).ShowCategory(SettingsModel.KeyBindingsCategory))
            .Do("bind", app => TuiHarness.Top<SettingsPanel>(app).SetBinding("ctrl+shift+k", "clear-line"))
            .Do("listed", app => Assert.Contains(TuiHarness.Top<SettingsPanel>(app).BindingRows(), r => r is { Chord: "Ctrl+Shift+K", Action: "clear-line", Custom: true }))
            .Do("unbind F1", app => TuiHarness.Top<SettingsPanel>(app).RemoveBinding("F1"))
            .Press(Key.Esc);
        var (t, host) = TuiHarness.Start(script);
        using var _ = t;

        host.Show(SettingsPanelPlugin.PanelId, "Key bindings");

        script.AssertOk();
        Assert.Equal("clear-line", t.Runtime.KeyBindings.Bindings["Ctrl+Shift+K"]);
        Assert.False(t.Runtime.KeyBindings.Bindings.ContainsKey("F1"));
        Assert.Equal("clear-line", t.Runtime.Config.Current.KeyBindings["Ctrl+Shift+K"]);
        Assert.Equal("none", t.Runtime.Config.Current.KeyBindings["F1"]);
    }

    [Theory]
    [InlineData(SettingKind.Integer, "12", "12")]
    [InlineData(SettingKind.Number, "12.5", "12.5")]
    [InlineData(SettingKind.Text, "true", "\"true\"")]
    [InlineData(SettingKind.List, "ls, cat ,", "[\"ls\",\"cat\"]")]
    public void ConvertsInputToJson(SettingKind kind, string input, string json)
    {
        var field = new SettingField("X", "x.y", "Y", kind, false, []);
        Assert.Null(SettingsModel.TryConvert(field, input, out var actual));
        Assert.Equal(json, actual);
    }

    [Fact]
    public void KeyHintsConvertKeysToChords()
    {
        Assert.Equal("Ctrl+R", Widgets.KeyHints.ToChord(Key.R.WithCtrl));
        Assert.Equal("Alt+G", Widgets.KeyHints.ToChord(Key.G.WithAlt));
        Assert.Equal("F5", Widgets.KeyHints.ToChord(Key.F5));
        Assert.Equal("Ctrl+Shift+UpArrow", Widgets.KeyHints.ToChord(Key.CursorUp.WithCtrl.WithShift));
    }
}
