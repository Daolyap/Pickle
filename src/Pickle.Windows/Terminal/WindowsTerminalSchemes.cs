using System.Text.Json;
using Pickle.Abstractions.Services;

namespace Pickle.Windows.Terminal;

/// <summary>
/// Color schemes for <c>pk theme import</c>: the user's own from Windows Terminal's settings.json files, then the ones
/// built into Windows Terminal (which settings.json doesn't list).
/// </summary>
public sealed class WindowsTerminalSchemes(WindowsTerminalLocations? locations) : ITerminalSchemeSource
{
    // Windows Terminal's defaults.json schemes (github.com/microsoft/terminal, MIT).
    internal const string BuiltInJson = """
        [
          { "name": "Campbell", "background": "#0C0C0C", "foreground": "#CCCCCC", "cursorColor": "#FFFFFF", "selectionBackground": "#FFFFFF",
            "black": "#0C0C0C", "red": "#C50F1F", "green": "#13A10E", "yellow": "#C19C00", "blue": "#0037DA", "purple": "#881798", "cyan": "#3A96DD", "white": "#CCCCCC",
            "brightBlack": "#767676", "brightRed": "#E74856", "brightGreen": "#16C60C", "brightYellow": "#F9F1A5", "brightBlue": "#3B78FF", "brightPurple": "#B4009E", "brightCyan": "#61D6D6", "brightWhite": "#F2F2F2" },
          { "name": "Campbell Powershell", "background": "#012456", "foreground": "#CCCCCC", "cursorColor": "#FFFFFF", "selectionBackground": "#FFFFFF",
            "black": "#0C0C0C", "red": "#C50F1F", "green": "#13A10E", "yellow": "#C19C00", "blue": "#0037DA", "purple": "#881798", "cyan": "#3A96DD", "white": "#CCCCCC",
            "brightBlack": "#767676", "brightRed": "#E74856", "brightGreen": "#16C60C", "brightYellow": "#F9F1A5", "brightBlue": "#3B78FF", "brightPurple": "#B4009E", "brightCyan": "#61D6D6", "brightWhite": "#F2F2F2" },
          { "name": "Vintage", "background": "#000000", "foreground": "#C0C0C0", "cursorColor": "#FFFFFF", "selectionBackground": "#FFFFFF",
            "black": "#000000", "red": "#800000", "green": "#008000", "yellow": "#808000", "blue": "#000080", "purple": "#800080", "cyan": "#008080", "white": "#C0C0C0",
            "brightBlack": "#808080", "brightRed": "#FF0000", "brightGreen": "#00FF00", "brightYellow": "#FFFF00", "brightBlue": "#0000FF", "brightPurple": "#FF00FF", "brightCyan": "#00FFFF", "brightWhite": "#FFFFFF" },
          { "name": "One Half Dark", "background": "#282C34", "foreground": "#DCDFE4", "cursorColor": "#FFFFFF", "selectionBackground": "#FFFFFF",
            "black": "#282C34", "red": "#E06C75", "green": "#98C379", "yellow": "#E5C07B", "blue": "#61AFEF", "purple": "#C678DD", "cyan": "#56B6C2", "white": "#DCDFE4",
            "brightBlack": "#5A6374", "brightRed": "#E06C75", "brightGreen": "#98C379", "brightYellow": "#E5C07B", "brightBlue": "#61AFEF", "brightPurple": "#C678DD", "brightCyan": "#56B6C2", "brightWhite": "#DCDFE4" },
          { "name": "One Half Light", "background": "#FAFAFA", "foreground": "#383A42", "cursorColor": "#4F525D", "selectionBackground": "#4F525D",
            "black": "#383A42", "red": "#E45649", "green": "#50A14F", "yellow": "#C18301", "blue": "#0184BC", "purple": "#A626A4", "cyan": "#0997B3", "white": "#FAFAFA",
            "brightBlack": "#4F525D", "brightRed": "#DF6C75", "brightGreen": "#98C379", "brightYellow": "#E4C07A", "brightBlue": "#61AFEF", "brightPurple": "#C577DD", "brightCyan": "#56B5C1", "brightWhite": "#FFFFFF" },
          { "name": "Solarized Dark", "background": "#002B36", "foreground": "#839496", "cursorColor": "#FFFFFF", "selectionBackground": "#FFFFFF",
            "black": "#002B36", "red": "#DC322F", "green": "#859900", "yellow": "#B58900", "blue": "#268BD2", "purple": "#D33682", "cyan": "#2AA198", "white": "#EEE8D5",
            "brightBlack": "#073642", "brightRed": "#CB4B16", "brightGreen": "#586E75", "brightYellow": "#657B83", "brightBlue": "#839496", "brightPurple": "#6C71C4", "brightCyan": "#93A1A1", "brightWhite": "#FDF6E3" },
          { "name": "Solarized Light", "background": "#FDF6E3", "foreground": "#657B83", "cursorColor": "#002B36", "selectionBackground": "#073642",
            "black": "#002B36", "red": "#DC322F", "green": "#859900", "yellow": "#B58900", "blue": "#268BD2", "purple": "#D33682", "cyan": "#2AA198", "white": "#EEE8D5",
            "brightBlack": "#073642", "brightRed": "#CB4B16", "brightGreen": "#586E75", "brightYellow": "#657B83", "brightBlue": "#839496", "brightPurple": "#6C71C4", "brightCyan": "#93A1A1", "brightWhite": "#FDF6E3" },
          { "name": "Tango Dark", "background": "#000000", "foreground": "#D3D7CF", "cursorColor": "#FFFFFF", "selectionBackground": "#FFFFFF",
            "black": "#000000", "red": "#CC0000", "green": "#4E9A06", "yellow": "#C4A000", "blue": "#3465A4", "purple": "#75507B", "cyan": "#06989A", "white": "#D3D7CF",
            "brightBlack": "#555753", "brightRed": "#EF2929", "brightGreen": "#8AE234", "brightYellow": "#FCE94F", "brightBlue": "#729FCF", "brightPurple": "#AD7FA8", "brightCyan": "#34E2E2", "brightWhite": "#EEEEEC" },
          { "name": "Tango Light", "background": "#FFFFFF", "foreground": "#555753", "cursorColor": "#000000", "selectionBackground": "#555753",
            "black": "#000000", "red": "#CC0000", "green": "#4E9A06", "yellow": "#C4A000", "blue": "#3465A4", "purple": "#75507B", "cyan": "#06989A", "white": "#D3D7CF",
            "brightBlack": "#555753", "brightRed": "#EF2929", "brightGreen": "#8AE234", "brightYellow": "#FCE94F", "brightBlue": "#729FCF", "brightPurple": "#AD7FA8", "brightCyan": "#34E2E2", "brightWhite": "#EEEEEC" }
        ]
        """;

    private static readonly Lazy<IReadOnlyList<TerminalScheme>> BuiltIn = new(() => TerminalSchemes.Parse(BuiltInJson));

    public static WindowsTerminalSchemes ForCurrentUser() => new(WindowsTerminalLocations.ForCurrentUser());

    public IReadOnlyList<TerminalScheme> Schemes()
    {
        var result = new List<TerminalScheme>();
        foreach (var file in locations?.SettingsFiles ?? [])
        {
            try
            {
                if (File.Exists(file))
                {
                    result.AddRange(TerminalSchemes.Parse(File.ReadAllText(file)));
                }
            }
            catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
            {
                // An unreadable or half-written settings.json just contributes nothing.
            }
        }

        result.AddRange(BuiltIn.Value);
        return [.. result.DistinctBy(s => s.Name, StringComparer.OrdinalIgnoreCase)];
    }
}
