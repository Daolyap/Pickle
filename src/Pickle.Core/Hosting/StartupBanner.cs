using System.Text;
using Pickle.Abstractions;

namespace Pickle.Core.Hosting;

/// <summary>
/// The interactive start banner (<c>shell.bannerStyle</c>): a pickle and the wordmark in the theme's greens, with an
/// optional ~0.5 s shine that sweeps across once. Any key ends the animation early (and is left for the editor).
/// Narrow or non-ANSI terminals get the one-line banner.
/// </summary>
internal static class StartupBanner
{
    private const int Gap = 2;
    private const int FrameCount = 14;
    private const int FrameDelayMs = 35;

    private static readonly string[] Pickle =
    [
        "       ▄▄▄ ",
        "     ▄█▓▒▓█",
        "   ▄█▒▓▒▓█▀",
        "  █▓▒▓▒▓█▀ ",
        " █▒▓▒▓█▀   ",
        "  ▀▀▀▀     ",
    ];

    private static readonly string[][] Letters =
    [
        ["██████╗ ", "██╔══██╗", "██████╔╝", "██╔═══╝ ", "██║     ", "╚═╝     "],
        ["██╗", "██║", "██║", "██║", "██║", "╚═╝"],
        [" ██████╗", "██╔════╝", "██║     ", "██║     ", "╚██████╗", " ╚═════╝"],
        ["██╗  ██╗", "██║ ██╔╝", "█████╔╝ ", "██╔═██╗ ", "██║  ██╗", "╚═╝  ╚═╝"],
        ["██╗     ", "██║     ", "██║     ", "██║     ", "███████╗", "╚══════╝"],
        ["███████╗", "██╔════╝", "█████╗  ", "██╔══╝  ", "███████╗", "╚══════╝"],
    ];

    internal static readonly string[] Art = BuildArt();

    internal static int ArtWidth { get; } = Art.Max(TextWidth.VisibleWidth);

    public static void Write(PickleRuntime runtime, Action<int>? sleep = null)
    {
        var terminal = runtime.Terminal;
        var theme = runtime.Themes.Current;
        var style = runtime.Config.Current.Shell.BannerStyle?.Trim().ToLowerInvariant();
        var info = InfoLine(theme, terminal.Width);
        if (style == "line" || !terminal.SupportsAnsi || terminal.Width < ArtWidth + 2 || terminal.Height < Art.Length + 4)
        {
            terminal.Write(info + "\r\n");
            return;
        }

        var palette = Palette(theme);
        var frames = style == "art" || palette is null ? 1 : FrameCount;
        sleep ??= Thread.Sleep;
        terminal.Write(Ansi.HideCursor);
        try
        {
            for (var frame = 0; frame < frames; frame++)
            {
                var last = frame == frames - 1 || (frame > 0 && terminal.KeyAvailable);
                var sb = new StringBuilder();
                sb.Append(Ansi.BeginSynchronizedUpdate);
                if (frame > 0)
                {
                    sb.Append(Ansi.CursorUp(Art.Length)).Append('\r');
                }

                // The shine starts left of the art and leaves it on the right; the last frame has none.
                var shine = last ? double.NaN : -8 + ((ArtWidth + 16) * frame / (double)(frames - 1));
                for (var row = 0; row < Art.Length; row++)
                {
                    AppendRow(sb, Art[row], row, shine, palette, theme.Ui.Accent);
                    sb.Append(Ansi.Reset).Append("\r\n");
                }

                sb.Append(Ansi.EndSynchronizedUpdate);
                terminal.Write(sb.ToString());
                terminal.Flush();
                if (last)
                {
                    break;
                }

                sleep(FrameDelayMs);
            }
        }
        finally
        {
            terminal.Write(Ansi.ShowCursor);
        }

        terminal.Write(info + "\r\n");
    }

    /// <summary>The art in its resting gradient (no animation) with a left margin, or null when it doesn't fit.</summary>
    internal static IReadOnlyList<string>? Logo(Theme theme, int width, int indent = 2)
    {
        if (width < ArtWidth + indent + 1)
        {
            return null;
        }

        var palette = Palette(theme);
        var margin = new string(' ', indent);
        return [.. Art.Select((text, row) =>
        {
            var sb = new StringBuilder(margin);
            AppendRow(sb, text, row, double.NaN, palette, theme.Ui.Accent);
            return sb.Append(Ansi.Reset).ToString();
        })];
    }

    /// <summary>"🥒 Pickle x · PowerShell y · F1 commands · pk help", dropping trailing parts that don't fit.</summary>
    private static string InfoLine(Theme theme, int width)
    {
        var head = "🥒 Pickle " + PickleRuntime.Version;
        var rest = new StringBuilder();
        foreach (var part in new[] { "PowerShell " + PickleRuntime.PowerShellVersion, "F1 commands", "pk help" })
        {
            if (TextWidth.VisibleWidth(head) + rest.Length + 5 + part.Length >= width)
            {
                break;
            }

            rest.Append("  ·  ").Append(part);
        }

        return Ansi.Colorize(head, theme.Ui.Accent, bold: true) + Ansi.Colorize(rest.ToString(), theme.Ui.Muted);
    }

    private static string[] BuildArt()
    {
        var rows = new string[Pickle.Length];
        for (var row = 0; row < rows.Length; row++)
        {
            rows[row] = (Pickle[row] + new string(' ', Gap) + string.Concat(Letters.Select(l => l[row]))).TrimEnd();
        }

        return rows;
    }

    /// <summary>Dark and light ends of the gradient, from the theme's greens; null when the theme uses named colors.</summary>
    private static (PickleColor Dark, PickleColor Light)? Palette(Theme theme)
    {
        var dark = PickleColor.Parse(theme.Terminal.Green) ?? PickleColor.Parse(theme.Ui.Accent);
        var light = PickleColor.Parse(theme.Terminal.BrightGreen) ?? PickleColor.Parse(theme.Ui.Success);
        return dark is { IsRgb: true } d && light is { IsRgb: true } l ? (d, l) : null;
    }

    private static void AppendRow(StringBuilder sb, string text, int row, double shine, (PickleColor Dark, PickleColor Light)? palette, string accent)
    {
        if (palette is not { } p)
        {
            sb.Append(Ansi.Style(accent)).Append(text);
            return;
        }

        var column = 0;
        string? current = null;
        foreach (var rune in text.EnumerateRunes())
        {
            if (rune.Value == ' ')
            {
                sb.Append(' ');
                column++;
                continue;
            }

            // Diagonal gradient (top-left dark → bottom-right light) plus a bright band around the shine column.
            var t = Math.Clamp(((column + (row * 2)) / (double)(ArtWidth + 10)) + 0.15, 0, 1);
            var glow = double.IsNaN(shine) ? 0 : Math.Max(0, 1 - (Math.Abs(column - (row * 1.5) - shine) / 4));
            var color = Mix(Mix(p.Dark, p.Light, t), PickleColor.FromRgb(255, 255, 240), glow * 0.85);
            var style = Ansi.Style(Hex(color));
            if (style != current)
            {
                sb.Append(style);
                current = style;
            }

            sb.Append(rune.ToString());
            column++;
        }
    }

    private static PickleColor Mix(PickleColor a, PickleColor b, double t) => PickleColor.FromRgb(
        (byte)Math.Round(a.R + ((b.R - a.R) * t)),
        (byte)Math.Round(a.G + ((b.G - a.G) * t)),
        (byte)Math.Round(a.B + ((b.B - a.B) * t)));

    private static string Hex(PickleColor c) => $"#{c.R:X2}{c.G:X2}{c.B:X2}";
}
