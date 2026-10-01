using Pickle.Abstractions;
using Pickle.Modules.Themes;
using Pickle.Testing;

namespace Pickle.Modules.Tests.Themes;

public sealed class ThemePackTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("pickle-themepack").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    [Fact]
    public void ThePackShipsTwelveThemes() => Assert.Equal(12, ThemePacksModule.ThemeNames.Count);

    [Fact]
    public void InstallCopiesMissingThemesAndKeepsEditedOnesUnlessForced()
    {
        var first = ThemePacksModule.Install(_dir, overwrite: false);
        File.WriteAllText(Path.Combine(_dir, "monokai.json"), "{ \"name\": \"monokai\", \"edited\": true }");

        var second = ThemePacksModule.Install(_dir, overwrite: false);
        Assert.Equal(12, first.Count);
        Assert.Empty(second);
        Assert.Contains("edited", File.ReadAllText(Path.Combine(_dir, "monokai.json")), StringComparison.Ordinal);

        var forced = ThemePacksModule.Install(_dir, overwrite: true);
        Assert.Equal(12, forced.Count);
        Assert.DoesNotContain("edited", File.ReadAllText(Path.Combine(_dir, "monokai.json")), StringComparison.Ordinal);
    }

    [Fact]
    public void RemoveDeletesOnlyThemesThatStillMatchTheOriginal()
    {
        ThemePacksModule.Install(_dir, overwrite: false);
        File.AppendAllText(Path.Combine(_dir, "kanagawa.json"), " ");

        var removed = ThemePacksModule.Remove(_dir);

        Assert.Equal(11, removed.Count);
        Assert.True(File.Exists(Path.Combine(_dir, "kanagawa.json")));
        Assert.False(File.Exists(Path.Combine(_dir, "zenburn.json")));
    }

    [Fact]
    public void LoadingTheModuleMakesTheThemesAvailableAndUsable()
    {
        using var t = TestPickle.Create(start: true, modules: OptionalModules.All, machineModules: ["themes"]);

        foreach (var name in ThemePacksModule.ThemeNames)
        {
            Assert.Contains(name, t.Runtime.Themes.Available);
            Assert.NotNull(t.Runtime.Themes.Load(name));
        }

        t.Runtime.Themes.Apply("kanagawa");
        Assert.Equal("kanagawa", t.Runtime.Themes.Current.Name);
        Assert.Equal("#1F1F28", t.Runtime.Themes.Current.Terminal.Background);
    }

    [Fact]
    public void EveryThemeHasReadableColorsAndAWellFormedPrompt()
    {
        using var t = TestPickle.Create(start: true, modules: OptionalModules.All, machineModules: ["themes"]);

        foreach (var name in ThemePacksModule.ThemeNames)
        {
            var theme = t.Runtime.Themes.Load(name)!;
            Assert.Equal(name, theme.Name);
            Assert.False(string.IsNullOrWhiteSpace(theme.Description));
            Assert.True(Contrast(theme.Terminal.Foreground, theme.Terminal.Background) >= 4.5, $"{name}: foreground on background {Contrast(theme.Terminal.Foreground, theme.Terminal.Background):0.0}");
            Assert.True(Contrast(theme.Ui.Accent, theme.Ui.PanelBackground) >= 3.0, $"{name}: accent on panel {Contrast(theme.Ui.Accent, theme.Ui.PanelBackground):0.0}");
            Assert.True(Contrast(theme.Ui.MenuSelectedForeground, theme.Ui.MenuSelectedBackground) >= 4.5, $"{name}: selected menu item");
            Assert.True(Contrast(theme.Syntax.Comment!, theme.Terminal.Background) >= 2.0, $"{name}: comments");
            Assert.NotEmpty(theme.Prompt.Left);
            Assert.All(theme.Prompt.Left.Concat(theme.Prompt.Right), s => Assert.False(string.IsNullOrEmpty(s.Type)));
        }
    }

    private static double Contrast(string foreground, string background)
    {
        static double Luminance(string hex)
        {
            var c = PickleColor.Parse(hex)!.Value;
            static double Channel(int v)
            {
                var s = v / 255.0;
                return s <= 0.03928 ? s / 12.92 : Math.Pow((s + 0.055) / 1.055, 2.4);
            }

            return (0.2126 * Channel(c.R)) + (0.7152 * Channel(c.G)) + (0.0722 * Channel(c.B));
        }

        var (a, b) = (Luminance(foreground), Luminance(background));
        return (Math.Max(a, b) + 0.05) / (Math.Min(a, b) + 0.05);
    }
}
