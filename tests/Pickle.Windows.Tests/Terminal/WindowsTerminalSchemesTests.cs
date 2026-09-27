using Pickle.Windows.Terminal;

namespace Pickle.Windows.Tests.Terminal;

public sealed class WindowsTerminalSchemesTests
{
    [Fact]
    public void BuiltInSchemesAreAllComplete()
    {
        var names = new WindowsTerminalSchemes(null).Schemes().Select(s => s.Name).ToList();
        Assert.Equal(
            ["Campbell", "Campbell Powershell", "Vintage", "One Half Dark", "One Half Light", "Solarized Dark", "Solarized Light", "Tango Dark", "Tango Light"],
            names);
    }

    [Fact]
    public void UserSchemesComeFirstAndReplaceBuiltInsOfTheSameName()
    {
        var root = Directory.CreateTempSubdirectory("pickle-wt-schemes").FullName;
        var settings = Path.Combine(root, "settings.json");
        File.WriteAllText(settings, """
            // comments and trailing commas, like the real file
            {
              "schemes": [
                { "name": "Campbell", "background": "#111111", "foreground": "#EEEEEE",
                  "black": "#000000", "red": "#AA0000", "green": "#00AA00", "yellow": "#AAAA00", "blue": "#0000AA", "purple": "#AA00AA", "cyan": "#00AAAA", "white": "#AAAAAA",
                  "brightBlack": "#555555", "brightRed": "#FF5555", "brightGreen": "#55FF55", "brightYellow": "#FFFF55", "brightBlue": "#5555FF", "brightPurple": "#FF55FF", "brightCyan": "#55FFFF", "brightWhite": "#FFFFFF", },
              ],
            }
            """);
        var locations = new WindowsTerminalLocations(Path.Combine(root, "fragments"), [settings, Path.Combine(root, "missing.json")]);

        var schemes = new WindowsTerminalSchemes(locations).Schemes();
        Assert.Equal("Campbell", schemes[0].Name);
        Assert.Equal("#111111", schemes[0].Palette.Background);
        Assert.Single(schemes, s => s.Name == "Campbell");
        Assert.Contains(schemes, s => s.Name == "Tango Light");
    }

    [Fact]
    public void AnUnreadableSettingsFileIsSkipped()
    {
        var root = Directory.CreateTempSubdirectory("pickle-wt-schemes").FullName;
        var settings = Path.Combine(root, "settings.json");
        File.WriteAllText(settings, "{ not json");
        var schemes = new WindowsTerminalSchemes(new WindowsTerminalLocations(root, [settings])).Schemes();
        Assert.Equal(9, schemes.Count);
    }
}
