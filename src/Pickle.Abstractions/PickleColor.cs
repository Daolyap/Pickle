using System.Globalization;

namespace Pickle.Abstractions;

/// <summary>Parses theme color strings and produces ANSI SGR sequences.</summary>
public readonly record struct PickleColor
{
    private static readonly string[] Names =
    [
        "black", "red", "green", "yellow", "blue", "purple", "cyan", "white",
        "brightBlack", "brightRed", "brightGreen", "brightYellow", "brightBlue", "brightPurple", "brightCyan", "brightWhite",
    ];

    private PickleColor(int ansiIndex, byte r, byte g, byte b, bool isRgb)
    {
        AnsiIndex = ansiIndex;
        R = r;
        G = g;
        B = b;
        IsRgb = isRgb;
    }

    /// <summary>0-15 for named colors, -1 for RGB.</summary>
    public int AnsiIndex { get; }
    public byte R { get; }
    public byte G { get; }
    public byte B { get; }
    public bool IsRgb { get; }

    public static PickleColor FromRgb(byte r, byte g, byte b) => new(-1, r, g, b, true);

    public static PickleColor FromAnsi(int index) => new(index, 0, 0, 0, false);

    /// <summary>Returns null for null/empty/"default"/unparseable input.</summary>
    public static PickleColor? Parse(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Equals("default", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        value = value.Trim();
        if (value[0] == '#')
        {
            var hex = value[1..];
            if (hex.Length == 3)
            {
                hex = string.Concat(hex[0], hex[0], hex[1], hex[1], hex[2], hex[2]);
            }

            if (hex.Length == 6 && int.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var rgb))
            {
                return FromRgb((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb);
            }

            return null;
        }

        var normalized = value.Replace("-", string.Empty, StringComparison.Ordinal).Replace("_", string.Empty, StringComparison.Ordinal);
        if (normalized.Equals("magenta", StringComparison.OrdinalIgnoreCase))
        {
            normalized = "purple";
        }
        else if (normalized.Equals("brightMagenta", StringComparison.OrdinalIgnoreCase))
        {
            normalized = "brightPurple";
        }
        else if (normalized.Equals("gray", StringComparison.OrdinalIgnoreCase) || normalized.Equals("grey", StringComparison.OrdinalIgnoreCase))
        {
            normalized = "brightBlack";
        }

        for (var i = 0; i < Names.Length; i++)
        {
            if (Names[i].Equals(normalized, StringComparison.OrdinalIgnoreCase))
            {
                return FromAnsi(i);
            }
        }

        return null;
    }

    public string ToForegroundSgr() => IsRgb
        ? $"38;2;{R};{G};{B}"
        : (AnsiIndex < 8 ? (30 + AnsiIndex).ToString(CultureInfo.InvariantCulture) : (90 + AnsiIndex - 8).ToString(CultureInfo.InvariantCulture));

    public string ToBackgroundSgr() => IsRgb
        ? $"48;2;{R};{G};{B}"
        : (AnsiIndex < 8 ? (40 + AnsiIndex).ToString(CultureInfo.InvariantCulture) : (100 + AnsiIndex - 8).ToString(CultureInfo.InvariantCulture));

    public string ToHex() => IsRgb ? $"#{R:X2}{G:X2}{B:X2}" : Names[AnsiIndex];

    public override string ToString() => ToHex();
}

/// <summary>Helpers for building ANSI strings from theme colors.</summary>
public static class Ansi
{
    public const string Esc = "\u001b";
    public const string Reset = "\u001b[0m";
    public const string Bold = "\u001b[1m";
    public const string Dim = "\u001b[2m";
    public const string Italic = "\u001b[3m";
    public const string Underline = "\u001b[4m";
    public const string Reverse = "\u001b[7m";
    public const string ClearToEndOfLine = "\u001b[K";
    public const string ClearToEndOfScreen = "\u001b[J";
    public const string HideCursor = "\u001b[?25l";
    public const string ShowCursor = "\u001b[?25h";
    public const string BeginSynchronizedUpdate = "\u001b[?2026h";
    public const string EndSynchronizedUpdate = "\u001b[?2026l";

    public static string Style(string? foreground = null, string? background = null, bool bold = false, bool dim = false, bool italic = false, bool underline = false)
    {
        var parts = new List<string>(6);
        if (bold)
        {
            parts.Add("1");
        }

        if (dim)
        {
            parts.Add("2");
        }

        if (italic)
        {
            parts.Add("3");
        }

        if (underline)
        {
            parts.Add("4");
        }

        if (PickleColor.Parse(foreground) is { } fg)
        {
            parts.Add(fg.ToForegroundSgr());
        }

        if (PickleColor.Parse(background) is { } bg)
        {
            parts.Add(bg.ToBackgroundSgr());
        }

        return parts.Count == 0 ? string.Empty : $"{Esc}[{string.Join(';', parts)}m";
    }

    public static string Colorize(string text, string? foreground, string? background = null, bool bold = false)
    {
        var style = Style(foreground, background, bold);
        return style.Length == 0 ? text : style + text + Reset;
    }

    public static string CursorUp(int n) => n <= 0 ? string.Empty : $"{Esc}[{n}A";
    public static string CursorDown(int n) => n <= 0 ? string.Empty : $"{Esc}[{n}B";
    public static string CursorForward(int n) => n <= 0 ? string.Empty : $"{Esc}[{n}C";
    public static string CursorBack(int n) => n <= 0 ? string.Empty : $"{Esc}[{n}D";
    public static string CursorToColumn(int column1Based) => $"{Esc}[{column1Based}G";
    public static string CursorTo(int row1Based, int column1Based) => $"{Esc}[{row1Based};{column1Based}H";
    public static string SetTitle(string title) => $"{Esc}]0;{title}\u0007";
}
