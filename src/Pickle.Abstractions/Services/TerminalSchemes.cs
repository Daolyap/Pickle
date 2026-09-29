using System.Text.Json;
using System.Text.Json.Nodes;

namespace Pickle.Abstractions.Services;

/// <summary>A terminal color scheme (Windows Terminal's format: name, background, foreground, black … brightWhite).</summary>
public sealed record TerminalScheme(string Name, TerminalPalette Palette);

/// <summary>Color schemes defined in the terminal's own settings (Windows Terminal's settings.json), for <c>pk theme import</c>.</summary>
public interface ITerminalSchemeSource
{
    IReadOnlyList<TerminalScheme> Schemes();
}

public static class TerminalSchemes
{
    private static readonly JsonDocumentOptions Jsonc = new() { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true };

    private static readonly string[] Colors =
    [
        "background", "foreground", "black", "red", "green", "yellow", "blue", "purple", "cyan", "white",
        "brightBlack", "brightRed", "brightGreen", "brightYellow", "brightBlue", "brightPurple", "brightCyan", "brightWhite",
    ];

    /// <summary>
    /// Schemes in <paramref name="json"/>: one scheme object, an array of them, or a settings file with a "schemes" array
    /// (comments and trailing commas allowed). Entries missing any of the 16 colors, background or foreground are skipped.
    /// </summary>
    /// <exception cref="JsonException">The text isn't JSON.</exception>
    public static IReadOnlyList<TerminalScheme> Parse(string json)
    {
        var root = JsonNode.Parse(json, documentOptions: Jsonc);
        var candidates = root switch
        {
            JsonArray array => array,
            JsonObject obj when obj["schemes"] is JsonArray schemes => schemes,
            JsonObject obj => [obj.DeepClone()],
            _ => [],
        };

        var result = new List<TerminalScheme>();
        foreach (var node in candidates)
        {
            if (node is JsonObject scheme && Read(scheme) is { } parsed)
            {
                result.Add(parsed);
            }
        }

        return result;
    }

    private static TerminalScheme? Read(JsonObject scheme)
    {
        string? Color(string key) =>
            scheme.FirstOrDefault(p => p.Key.Equals(key, StringComparison.OrdinalIgnoreCase)).Value is JsonValue value
            && value.TryGetValue<string>(out var text) && PickleColor.Parse(text) is { IsRgb: true } c
                ? c.ToHex()
                : null;

        if (Colors.Any(key => Color(key) is null))
        {
            return null;
        }

        var name = scheme["name"] is JsonValue n && n.TryGetValue<string>(out var s) && !string.IsNullOrWhiteSpace(s) ? s.Trim() : "Imported";
        var palette = new TerminalPalette
        {
            Background = Color("background")!,
            Foreground = Color("foreground")!,
            CursorColor = Color("cursorColor") ?? Color("foreground")!,
            SelectionBackground = Color("selectionBackground") ?? Color("brightBlack")!,
            Black = Color("black")!,
            Red = Color("red")!,
            Green = Color("green")!,
            Yellow = Color("yellow")!,
            Blue = Color("blue")!,
            Purple = Color("purple")!,
            Cyan = Color("cyan")!,
            White = Color("white")!,
            BrightBlack = Color("brightBlack")!,
            BrightRed = Color("brightRed")!,
            BrightGreen = Color("brightGreen")!,
            BrightYellow = Color("brightYellow")!,
            BrightBlue = Color("brightBlue")!,
            BrightPurple = Color("brightPurple")!,
            BrightCyan = Color("brightCyan")!,
            BrightWhite = Color("brightWhite")!,
        };
        return new TerminalScheme(name, palette);
    }
}
