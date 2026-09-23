using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using Pickle.Abstractions;

namespace Pickle.Windows.Terminal;

/// <summary>
/// Builds the Windows Terminal JSON fragment: one "Pickle" profile plus a "Pickle" color scheme generated from the
/// theme's <see cref="TerminalPalette"/>.
/// </summary>
/// <remarks>
/// Terminal derives the GUID of a fragment profile as UUIDv5(UUIDv5({f65ddb7e-…}, app name), profile name) over
/// UTF-16LE bytes. The fragment states that same GUID explicitly, so the id is stable whether Terminal honours the
/// explicit value or recomputes it, and <c>defaultProfile</c> can reference it.
/// </remarks>
public static class WindowsTerminalFragment
{
    public const string AppName = "Pickle";
    public const string ProfileName = "Pickle";
    public const string SchemeName = "Pickle";

    /// <summary>Terminal's namespace for profiles created by fragments and plugins.</summary>
    public static readonly Guid FragmentNamespace = new("f65ddb7e-706b-4499-8a50-40313caf510a");

    private static readonly JsonSerializerOptions WriteOptions = new()
    {
        WriteIndented = true,
        IndentSize = 2,
        NewLine = "\n",
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private static readonly (string Key, Func<TerminalPalette, string> Get)[] SchemeColors =
    [
        ("background", p => p.Background),
        ("foreground", p => p.Foreground),
        ("cursorColor", p => p.CursorColor),
        ("selectionBackground", p => p.SelectionBackground),
        ("black", p => p.Black),
        ("red", p => p.Red),
        ("green", p => p.Green),
        ("yellow", p => p.Yellow),
        ("blue", p => p.Blue),
        ("purple", p => p.Purple),
        ("cyan", p => p.Cyan),
        ("white", p => p.White),
        ("brightBlack", p => p.BrightBlack),
        ("brightRed", p => p.BrightRed),
        ("brightGreen", p => p.BrightGreen),
        ("brightYellow", p => p.BrightYellow),
        ("brightBlue", p => p.BrightBlue),
        ("brightPurple", p => p.BrightPurple),
        ("brightCyan", p => p.BrightCyan),
        ("brightWhite", p => p.BrightWhite),
    ];

    // Index order of PickleColor's named colors, for resolving "red" etc. against the palette itself.
    private static readonly Func<TerminalPalette, string>[] NamedSlots =
    [
        p => p.Black, p => p.Red, p => p.Green, p => p.Yellow, p => p.Blue, p => p.Purple, p => p.Cyan, p => p.White,
        p => p.BrightBlack, p => p.BrightRed, p => p.BrightGreen, p => p.BrightYellow, p => p.BrightBlue, p => p.BrightPurple, p => p.BrightCyan, p => p.BrightWhite,
    ];

    public static Guid ProfileGuid { get; } = GenerateProfileGuid(AppName, ProfileName);

    /// <summary>The GUID as Terminal writes it: braces, lowercase.</summary>
    public static string ProfileGuidString => ProfileGuid.ToString("B");

    public static Guid GenerateProfileGuid(string appName, string profileName) =>
        Uuid5(Uuid5(FragmentNamespace, Encoding.Unicode.GetBytes(appName)), Encoding.Unicode.GetBytes(profileName));

    public static string Build(string executablePath, TerminalSettings settings, TerminalPalette palette)
    {
        var profile = new JsonObject
        {
            ["guid"] = ProfileGuidString,
            ["name"] = ProfileName,
            ["commandline"] = "\"" + executablePath.Trim().Trim('"') + "\"",
            ["hidden"] = false,
            ["colorScheme"] = SchemeName,
        };

        var font = new JsonObject();
        if (!string.IsNullOrWhiteSpace(settings.FontFace))
        {
            font["face"] = settings.FontFace.Trim();
        }

        if (settings.FontSize is { } size && size > 0)
        {
            font["size"] = Math.Round(size, 1);
        }

        if (font.Count > 0)
        {
            profile["font"] = font;
        }

        if (settings.Opacity is { } opacity)
        {
            profile["opacity"] = Math.Clamp(opacity, 0, 100);
        }

        profile["useAcrylic"] = settings.UseAcrylic;
        if (!string.IsNullOrWhiteSpace(settings.CursorShape))
        {
            profile["cursorShape"] = settings.CursorShape.Trim();
        }

        if (!string.IsNullOrWhiteSpace(settings.Padding))
        {
            profile["padding"] = settings.Padding.Trim();
        }

        if (!string.IsNullOrWhiteSpace(settings.BackgroundImage))
        {
            profile["backgroundImage"] = settings.BackgroundImage.Trim();
            if (settings.BackgroundImageOpacity is { } imageOpacity)
            {
                profile["backgroundImageOpacity"] = Math.Round(Math.Clamp(imageOpacity, 0, 1), 2);
            }
        }

        var scheme = new JsonObject { ["name"] = SchemeName };
        var fallback = new TerminalPalette();
        foreach (var (key, get) in SchemeColors)
        {
            scheme[key] = Hex(get(palette), palette, get(fallback));
        }

        var root = new JsonObject
        {
            ["profiles"] = new JsonArray(profile),
            ["schemes"] = new JsonArray(scheme),
        };
        return root.ToJsonString(WriteOptions) + "\n";
    }

    /// <summary>The executable path from a fragment's Pickle profile commandline, or null.</summary>
    public static string? ReadExecutable(string fragmentJson)
    {
        try
        {
            var root = JsonNode.Parse(fragmentJson, documentOptions: new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
            var commandline = root?["profiles"]?.AsArray()
                .FirstOrDefault(p => p?["guid"]?.GetValue<string>() == ProfileGuidString)?["commandline"]?.GetValue<string>();
            if (string.IsNullOrWhiteSpace(commandline))
            {
                return null;
            }

            commandline = commandline.Trim();
            if (commandline.StartsWith('"'))
            {
                var close = commandline.IndexOf('"', 1);
                return close > 1 ? commandline[1..close] : null;
            }

            var space = commandline.IndexOf(' ', StringComparison.Ordinal);
            return space > 0 ? commandline[..space] : commandline;
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException)
        {
            return null;
        }
    }

    /// <summary>Windows Terminal only accepts #RRGGBB; named colors resolve through the palette itself.</summary>
    private static string Hex(string value, TerminalPalette palette, string fallback)
    {
        for (var depth = 0; depth < 3; depth++)
        {
            switch (PickleColor.Parse(value))
            {
                case { IsRgb: true } rgb:
                    return rgb.ToHex();
                case { } named:
                    value = NamedSlots[named.AnsiIndex](palette);
                    continue;
                default:
                    return fallback;
            }
        }

        return fallback;
    }

    // RFC 4122 name-based UUID (SHA-1). SHA-1 is what the version-5 algorithm specifies; nothing security-related.
    private static Guid Uuid5(Guid ns, byte[] name)
    {
        var buffer = new byte[16 + name.Length];
        ns.TryWriteBytes(buffer, bigEndian: true, out _);
        name.CopyTo(buffer, 16);
#pragma warning disable CA5350
        var hash = SHA1.HashData(buffer);
#pragma warning restore CA5350
        hash[6] = (byte)((hash[6] & 0x0F) | 0x50);
        hash[8] = (byte)((hash[8] & 0x3F) | 0x80);
        return new Guid(hash.AsSpan(0, 16), bigEndian: true);
    }
}
