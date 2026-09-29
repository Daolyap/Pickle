using Pickle.Abstractions;
using Pickle.Testing;
using Pickle.Tui.Panels.Themes;
using Pickle.Tui.Widgets;
using Terminal.Gui.Input;
using Terminal.Gui.Views;

namespace Pickle.Tui.Tests.Themes;

public class ThemeGalleryTests
{
    [Fact]
    public void PreviewShowsTheThemeInItsOwnColorsAndAnimatedThemesMove()
    {
        using var t = TestPickle.Create(start: true);
        var previewer = t.Runtime.Services.Require<IThemePreviewer>();
        var aurora = t.Runtime.Themes.Load("aurora")!;

        var first = ThemeGalleryPanel.PreviewLines(aurora, previewer, 80, 0);
        var later = ThemeGalleryPanel.PreviewLines(aurora, previewer, 80, 25);
        Assert.StartsWith("aurora  ✦ animated", TextWidth.StripAnsi(first[0]), StringComparison.Ordinal);
        Assert.Contains(first, l => TextWidth.StripAnsi(l).Contains("Get-ChildItem -Path 'src'", StringComparison.Ordinal));
        Assert.Contains(first, l => TextWidth.StripAnsi(l).Contains("main", StringComparison.Ordinal));
        Assert.NotEqual(first, later);

        var nord = t.Runtime.Themes.Load("nord")!;
        Assert.Equal(
            ThemeGalleryPanel.PreviewLines(nord, previewer, 80, null),
            ThemeGalleryPanel.PreviewLines(nord, previewer, 80, null));
        Assert.DoesNotContain("animated", TextWidth.StripAnsi(ThemeGalleryPanel.PreviewLines(nord, previewer, 80, null)[0]), StringComparison.Ordinal);
    }

    [Fact]
    public void FilteringSelectsAThemeAndEnterAppliesIt()
    {
        var script = new UiScript()
            .WaitFor("listed", app => TuiHarness.Top<ThemeGalleryPanel>(app).List.TotalCount >= 18)
            .Do("current first", app => Assert.Equal("pickle", TuiHarness.Top<ThemeGalleryPanel>(app).List.Selected?.Name))
            .Type("nord")
            .WaitFor("nord selected", app => TuiHarness.Top<ThemeGalleryPanel>(app).List.Selected?.Name == "nord")
            .Do("nord colors", app => Assert.Equal("#2E3440", TuiHarness.Top<ThemeGalleryPanel>(app).Preview.Palette.Background))
            .Press(Key.Enter);
        var (t, host) = TuiHarness.Start(script);
        using var _ = t;
        new ThemeGalleryPanelPlugin().Initialize(t.Runtime);

        var result = host.Show(ThemeGalleryPanel.PanelId);

        script.AssertOk();
        Assert.Null(result);
        Assert.Equal("nord", t.Runtime.Themes.Current.Name);
        Assert.Equal("nord", t.Runtime.Config.Current.Theme);
    }

    [Fact]
    public void TheSelectedAnimatedThemeMovesAndF2MakesItTheLightTheme()
    {
        IReadOnlyList<string>? before = null;
        var script = new UiScript()
            .WaitFor("listed", app => TuiHarness.Top<ThemeGalleryPanel>(app).List.TotalCount >= 18)
            .Type("synthwave")
            .WaitFor("selected", app => TuiHarness.Top<ThemeGalleryPanel>(app).List.Selected?.Name == "synthwave")
            .Do("tick", app =>
            {
                var panel = TuiHarness.Top<ThemeGalleryPanel>(app);
                before = panel.Preview.Lines;
                panel.Tick(1500);
                Assert.NotEqual(before, panel.Preview.Lines);
            })
            .Press(Key.F2)
            .WaitFor("info", app => app.TopRunnableView is Dialog)
            .Press(Key.Enter)
            .WaitFor("back", app => app.TopRunnableView is ThemeGalleryPanel)
            .Press(Key.Esc);
        var (t, host) = TuiHarness.Start(script);
        using var _ = t;
        new ThemeGalleryPanelPlugin().Initialize(t.Runtime);

        host.Show(ThemeGalleryPanel.PanelId);

        script.AssertOk();
        Assert.Equal("synthwave", t.Runtime.Config.Current.LightTheme);
        Assert.Equal("pickle", t.Runtime.Themes.Current.Name);
    }

    [Fact]
    public void AnsiRunsUseThePalette()
    {
        var palette = new TerminalPalette { Foreground = "#EEEEEE", Background = "#101010", Red = "#AA0000", BrightBlue = "#5555FF" };
        var runs = AnsiView.Parse("a\u001b[31mb\u001b[1;38;2;1;2;3;48;5;12mc\u001b[0m\u001b[Kd\u001b]0;title\u0007", palette);

        Assert.Equal(["a", "b", "c", "d"], runs.Select(r => r.Text).Take(4));
        Assert.Equal("#EEEEEE", runs[0].Foreground.ToHex());
        Assert.Equal("#101010", runs[0].Background.ToHex());
        Assert.Equal("#AA0000", runs[1].Foreground.ToHex());
        Assert.Equal(("#010203", "#5555FF", true), (runs[2].Foreground.ToHex(), runs[2].Background.ToHex(), runs[2].Bold));
        Assert.Equal(("#EEEEEE", false), (runs[3].Foreground.ToHex(), runs[3].Bold));
    }
}
