using Pickle.Abstractions;
using Pickle.Abstractions.Services;
using Pickle.Core.Prompt;
using Pickle.Testing;

namespace Pickle.Core.Tests.Prompt;

[Collection(ProcessWideStateCollection.Name)]
public class ThemeImportTests
{
    private const string OneHalfDark = """
        { "name": "One Half Dark", "background": "#282c34", "foreground": "#DCDFE4",
          "black": "#282C34", "red": "#E06C75", "green": "#98C379", "yellow": "#E5C07B", "blue": "#61AFEF", "purple": "#C678DD", "cyan": "#56B6C2", "white": "#DCDFE4",
          "brightBlack": "#5A6374", "brightRed": "#E06C75", "brightGreen": "#98C379", "brightYellow": "#E5C07B", "brightBlue": "#61AFEF", "brightPurple": "#C678DD", "brightCyan": "#56B6C2", "brightWhite": "#DCDFE4" }
        """;

    private const string Neon = """
        { "name": "Neon Night", "background": "#000010", "foreground": "#F0F0FF", "cursorColor": "#FF00FF", "selectionBackground": "#303060",
          "black": "#101020", "red": "#FF0040", "green": "#00FF80", "yellow": "#FFE000", "blue": "#0080FF", "purple": "#C000FF", "cyan": "#00E0FF", "white": "#E0E0F0",
          "brightBlack": "#505070", "brightRed": "#FF4070", "brightGreen": "#60FFB0", "brightYellow": "#FFF060", "brightBlue": "#60B0FF", "brightPurple": "#E060FF", "brightCyan": "#60F0FF", "brightWhite": "#FFFFFF" }
        """;

    [Fact]
    public void ParsesOneSchemeArraysAndSettingsFilesWithComments()
    {
        var one = Assert.Single(TerminalSchemes.Parse(OneHalfDark));
        Assert.Equal("One Half Dark", one.Name);
        Assert.Equal("#282C34", one.Palette.Background);
        Assert.Equal("#DCDFE4", one.Palette.CursorColor);
        Assert.Equal("#5A6374", one.Palette.SelectionBackground);

        Assert.Equal(["One Half Dark", "Neon Night"], TerminalSchemes.Parse($"[{OneHalfDark}, {Neon}]").Select(s => s.Name));

        var settings = $$"""
            // Windows Terminal settings
            {
              "profiles": { "list": [] },
              "schemes": [ {{Neon}}, { "name": "Broken", "background": "#000000" }, ],
            }
            """;
        Assert.Equal(["Neon Night"], TerminalSchemes.Parse(settings).Select(s => s.Name));
        Assert.Empty(TerminalSchemes.Parse("""{ "profiles": {} }"""));
    }

    [Fact]
    public void KeepsTheLayoutAndMovesItsColorsToTheSchemesSlots()
    {
        var scheme = TerminalSchemes.Parse(Neon)[0];
        var layout = PromptRenderTests.LoadTheme("powerline");
        var theme = ThemeImport.Create(scheme, layout, "neon-night");

        Assert.Equal("neon-night", theme.Name);
        Assert.Same(scheme.Palette, theme.Terminal);
        Assert.Contains("Neon Night", theme.Description, StringComparison.Ordinal);
        Assert.Equal(layout.Prompt.Separator, theme.Prompt.Separator);
        Assert.Equal(layout.Prompt.Left.Select(s => s.Type), theme.Prompt.Left.Select(s => s.Type));

        var cwd = theme.Prompt.Left.Single(s => s.Type == "cwd");
        var git = theme.Prompt.Left.Single(s => s.Type == "git");
        Assert.Equal(scheme.Palette.Blue, cwd.Background);
        Assert.Equal(scheme.Palette.Green, git.Background);
        Assert.Equal(scheme.Palette.Background, cwd.Foreground);
        Assert.Equal(scheme.Palette.Green, theme.Prompt.PromptCharColor);
        Assert.Equal(scheme.Palette.Red, theme.Prompt.PromptCharErrorColor);

        Assert.Equal(scheme.Palette.Green, theme.Syntax.Command);
        Assert.Equal(scheme.Palette.Background, theme.Ui.PanelBackground);
        Assert.NotEqual(scheme.Palette.Background, theme.Ui.MenuBackground);
        Assert.Equal("#61AFEF", layout.Prompt.Left.Single(s => s.Type == "cwd").Background);
    }

    [Fact]
    public void AnimatedLayoutsKeepMovingInTheSchemesColors()
    {
        var scheme = TerminalSchemes.Parse(Neon)[0];
        var theme = ThemeImport.Create(scheme, PromptRenderTests.LoadTheme("aurora"), "neon-aurora");
        Assert.True(ThemeAnimator.IsAnimated(theme));
        Assert.All(theme.Prompt.Animation!.Colors, c => Assert.Contains(c, new[]
        {
            scheme.Palette.Green, scheme.Palette.Cyan, scheme.Palette.Blue, scheme.Palette.Purple, scheme.Palette.BrightPurple,
            scheme.Palette.BrightGreen, scheme.Palette.BrightCyan, scheme.Palette.BrightBlue,
        }));
    }

    [Theory]
    [InlineData("One Half Dark", "one-half-dark")]
    [InlineData("  Tango: Light! ", "tango-light")]
    [InlineData("---", "imported")]
    [InlineData("Dracula+", "dracula")]
    public void ThemeNamesComeFromSchemeNames(string scheme, string expected) => Assert.Equal(expected, ThemeImport.ThemeName(scheme));

    [Fact]
    public void ImportsFromAFileRelativeToTheShellsLocation()
    {
        using var t = TestPickle.Create(width: 160, height: 40, start: true);
        var folder = Directory.CreateTempSubdirectory("pickle-scheme").FullName;
        File.WriteAllText(Path.Combine(folder, "neon.json"), Neon);
        t.Run($"Set-Location -LiteralPath '{folder}'");

        t.Run("pk theme import neon.json --layout catppuccin");
        var file = Path.Combine(t.Paths.ThemesDir, "neon-night.json");
        Assert.True(File.Exists(file));
        Assert.Contains("Imported \"Neon Night\" as neon-night (catppuccin layout)", t.Terminal.GetScreenText(), StringComparison.Ordinal);

        var loaded = t.Runtime.ThemeProvider.Load("neon-night")!;
        Assert.Equal("#000010", loaded.Terminal.Background);
        Assert.True(loaded.Prompt.NewlineBeforeInput);
        t.Run("pk theme set neon-night");
        Assert.Equal("neon-night", t.Runtime.Themes.Current.Name);

        Assert.ThrowsAny<Exception>(() => t.Run("pk theme import neon.json"));
        t.Run("pk theme import neon.json --force --layout minimal");
        Assert.False(t.Runtime.ThemeProvider.Load("neon-night")!.Prompt.NewlineBeforeInput);
    }

    [Fact]
    public void ImportsByNameFromTheTerminalsSchemesAndListsThem()
    {
        using var t = TestPickle.Create(width: 160, height: 40, start: true);
        t.Runtime.ServiceRegistry.Add<ITerminalSchemeSource>(new Schemes([.. TerminalSchemes.Parse($"[{OneHalfDark}, {Neon}]")]));

        t.Run("pk theme import --list");
        Assert.Contains("One Half Dark", t.Terminal.GetScreenText(), StringComparison.Ordinal);
        Assert.Contains("Neon Night", t.Terminal.GetScreenText(), StringComparison.Ordinal);

        t.Run("pk theme import 'one half dark' --name ohd");
        Assert.Equal("#282C34", t.Runtime.ThemeProvider.Load("ohd")!.Terminal.Background);
        Assert.ThrowsAny<Exception>(() => t.Run("pk theme import 'No Such Scheme'"));
        Assert.ThrowsAny<Exception>(() => t.Run("pk theme import 'Neon Night' --name ../evil"));
        Assert.False(File.Exists(Path.Combine(t.Paths.ThemesDir, "..", "evil.json")));
    }

    [Fact]
    public void AFileWithSeveralSchemesNeedsOnePicked()
    {
        using var t = TestPickle.Create(width: 160, height: 40, start: true);
        var file = Path.Combine(Directory.CreateTempSubdirectory("pickle-scheme").FullName, "many.json");
        File.WriteAllText(file, $"[{OneHalfDark}, {Neon}]");

        Assert.ThrowsAny<Exception>(() => t.Run($"pk theme import '{file}'"));
        t.Run($"pk theme import '{file}' --scheme 'neon night'");
        Assert.NotNull(t.Runtime.ThemeProvider.Load("neon-night"));
    }

    private sealed class Schemes(IReadOnlyList<TerminalScheme> schemes) : ITerminalSchemeSource
    {
        IReadOnlyList<TerminalScheme> ITerminalSchemeSource.Schemes() => schemes;
    }
}
