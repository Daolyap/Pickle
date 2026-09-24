using Pickle.Abstractions;
using Terminal.Gui.Drawing;
using Terminal.Gui.Views;

namespace Pickle.Tui.Tests;

public class PanelStyleTests
{
    private static Theme CustomTheme() => new()
    {
        Name = "custom",
        Ui = new UiColors
        {
            PanelBackground = "#101112",
            PanelForeground = "#E0E1E2",
            MenuSelectedBackground = "#00FF00",
            MenuSelectedForeground = "#000000",
            MatchHighlight = "yellow",
            Accent = "default",
        },
        Terminal = new TerminalPalette { Yellow = "#ABCDEF" },
    };

    [Fact]
    public void CreatesSchemesFromThemeUiColors()
    {
        var schemes = PanelStyle.For(CustomTheme());

        Assert.Equal(new Color(0xE0, 0xE1, 0xE2), schemes.Base.Normal.Foreground);
        Assert.Equal(new Color(0x10, 0x11, 0x12), schemes.Base.Normal.Background);
        Assert.Equal(new Color(0x00, 0xFF, 0x00), schemes.List.Focus.Background);
        Assert.Equal(new Color(0x00, 0x00, 0x00), schemes.Selected.Foreground);

        // Named colors resolve through the theme's terminal palette; "default" falls back to the built-in value.
        Assert.Equal(new Color(0xAB, 0xCD, 0xEF), schemes.Match.Foreground);
        Assert.Equal(PanelStyle.ToColor(new UiColors().Accent, CustomTheme(), Color.None), schemes.Accent.Foreground);
    }

    [Fact]
    public void SchemesAreCachedPerThemeInstance()
    {
        var theme = CustomTheme();
        Assert.Same(PanelStyle.For(theme), PanelStyle.For(theme));
        Assert.NotSame(PanelStyle.For(theme), PanelStyle.For(CustomTheme()));
    }

    [Fact]
    public void ApplyStylesWindowListsAndInputsWithoutGlobalState()
    {
        var theme = CustomTheme();
        var schemes = PanelStyle.For(theme);
        using var window = new Window();
        var list = new ListView();
        var field = new TextField();
        var label = new Label();
        var mine = new ListView();
        var custom = new Scheme(new Terminal.Gui.Drawing.Attribute(Color.Red, Color.Blue));
        mine.SetScheme(custom);
        window.Add(list, field, label, mine);

        PanelStyle.Apply(window, theme);

        Assert.Equal(schemes.Base, window.GetScheme());
        Assert.Equal(schemes.List, list.GetScheme());
        Assert.Equal(schemes.Input, field.GetScheme());
        Assert.Equal(schemes.Base.Normal, label.GetScheme().Normal);
        Assert.Equal(custom, mine.GetScheme());

        // A theme change re-applies to the views styled by PanelStyle.
        var other = CustomTheme();
        other.Ui.MenuBackground = "#222222";
        PanelStyle.Apply(window, other);
        Assert.Equal(PanelStyle.For(other).List, list.GetScheme());
        Assert.Equal(custom, mine.GetScheme());
    }

    [Fact]
    public void ToColorHandlesHexNamesAndDefaults()
    {
        var theme = new Theme();
        Assert.Equal(new Color(0x61, 0xAF, 0xEF), PanelStyle.ToColor("#61AFEF", theme, Color.None));
        Assert.Equal(PanelStyle.ToColor(theme.Terminal.BrightBlack, theme, Color.None), PanelStyle.ToColor("brightBlack", theme, Color.None));
        Assert.Equal(new Color(1, 2, 3), PanelStyle.ToColor(null, theme, new Color(1, 2, 3)));
        Assert.Equal(new Color(1, 2, 3), PanelStyle.ToColor("not-a-color", theme, new Color(1, 2, 3)));
    }
}
