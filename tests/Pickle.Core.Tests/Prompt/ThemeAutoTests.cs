using Pickle.Abstractions;
using Pickle.Core.Prompt;
using Pickle.Testing;

namespace Pickle.Core.Tests.Prompt;

[Collection(ProcessWideStateCollection.Name)]
public class ThemeAutoTests
{
    [Fact]
    public void AutoShowsTheLightOrDarkThemeAndSwitchesAtTheNextPrompt()
    {
        using var t = TestPickle.Create(configure: c => c.Theme = "auto");
        var themes = t.Runtime.ThemeProvider;
        var light = false;
        themes.PrefersLight = () => light;
        themes.RefreshAppearance();
        Assert.True(themes.FollowsSystem);
        Assert.Equal("pickle", themes.Current.Name);

        var changes = new List<string>();
        themes.ThemeChanged += (_, theme) => changes.Add(theme.Name);
        light = true;
        var engine = (PromptEngine)t.Runtime.Prompt;
        engine.Initialize();
        engine.Render(t.Runtime.CreatePromptContext());
        Assert.Equal("solarized-light", themes.Current.Name);
        Assert.Equal(["solarized-light"], changes);

        // Unchanged appearance: no event.
        engine.Render(t.Runtime.CreatePromptContext());
        Assert.Single(changes);
        Assert.Equal("auto", t.Runtime.Config.Current.Theme);
    }

    [Fact]
    public void UnknownAppearanceIsDarkAndLightAndDarkThemesAreConfigurable()
    {
        using var t = TestPickle.Create(configure: c =>
        {
            c.Theme = "auto";
            c.LightTheme = "rose-pine";
            c.DarkTheme = "nord";
        });
        var themes = t.Runtime.ThemeProvider;
        themes.PrefersLight = () => null;
        themes.RefreshAppearance();
        Assert.Equal("nord", themes.Current.Name);

        themes.PrefersLight = () => true;
        themes.RefreshAppearance();
        Assert.Equal("rose-pine", themes.Current.Name);

        t.Runtime.Config.Update(c => c.LightTheme = "dracula");
        Assert.Equal("dracula", themes.Current.Name);
    }

    [Fact]
    public void ApplyingAutoSavesItAndAThemeNameEndsIt()
    {
        using var t = TestPickle.Create();
        var themes = t.Runtime.ThemeProvider;
        themes.PrefersLight = () => true;

        themes.Apply("auto");
        Assert.Equal("auto", t.Runtime.Config.Current.Theme);
        Assert.Equal("solarized-light", themes.Current.Name);

        themes.Apply("gruvbox");
        Assert.False(themes.FollowsSystem);
        themes.RefreshAppearance();
        Assert.Equal("gruvbox", themes.Current.Name);
    }

    [Fact]
    public void ElevatedSessionsKeepTheAdminTheme()
    {
        using var t = TestPickle.Create(configure: c => c.Theme = "auto", elevated: true);
        var themes = t.Runtime.ThemeProvider;
        themes.PrefersLight = () => true;
        themes.RefreshAppearance();
        Assert.Equal("admin", themes.Current.Name);
    }

    [Fact]
    public void PkThemeAutoSetsBothThemesAndTheListShowsIt()
    {
        using var t = TestPickle.Create(width: 140, height: 60, start: true);
        t.Runtime.ThemeProvider.PrefersLight = () => false;

        t.Run("pk theme auto catppuccin tokyo-night");
        Assert.Equal("auto", t.Runtime.Config.Current.Theme);
        Assert.Equal("catppuccin", t.Runtime.Config.Current.LightTheme);
        Assert.Equal("tokyo-night", t.Runtime.Themes.Current.Name);
        Assert.Contains("Now showing tokyo-night", t.Terminal.GetScreenText(), StringComparison.Ordinal);

        t.Run("pk theme list");
        Assert.Contains("● auto", t.Terminal.GetScreenText(), StringComparison.Ordinal);
        Assert.Contains("catppuccin in light mode, tokyo-night in dark mode (dark now)", t.Terminal.GetScreenText(), StringComparison.Ordinal);

        Assert.ThrowsAny<Exception>(() => t.Run("pk theme auto nope nord"));
        Assert.Equal("catppuccin", t.Runtime.Config.Current.LightTheme);
    }

    [Theory]
    [InlineData("15;0", false)]
    [InlineData("0;15", true)]
    [InlineData("default;default;7", true)]
    [InlineData("12;8", false)]
    [InlineData("", null)]
    [InlineData(null, null)]
    [InlineData("junk", null)]
    public void ColorFgBgTellsTheTerminalBackground(string? value, bool? light) =>
        Assert.Equal(light, SystemAppearance.FromColorFgBg(value));
}
