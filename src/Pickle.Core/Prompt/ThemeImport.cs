using System.Text;
using Pickle.Abstractions;
using Pickle.Abstractions.Services;

namespace Pickle.Core.Prompt;

/// <summary>
/// Builds a Pickle theme from a terminal color scheme: the scheme becomes the palette, syntax and UI colors come from
/// its ANSI slots, and the prompt keeps a layout theme's segments with each color moved to the scheme's color in the
/// slot the layout's own palette used for it (a blue block stays the scheme's blue).
/// </summary>
public static class ThemeImport
{
    public static Theme Create(TerminalScheme scheme, Theme layout, string name)
    {
        var p = scheme.Palette;
        var dark = Luminance(p.Background) < 0.5;
        var slots = Slots(layout.Terminal);
        var target = Slots(p);

        string? Map(string? color)
        {
            // Named colors already follow the terminal palette; only RGB values are moved.
            if (PickleColor.Parse(color) is not { IsRgb: true } rgb)
            {
                return color;
            }

            var nearest = 0;
            var best = double.MaxValue;
            for (var i = 0; i < slots.Length; i++)
            {
                if (PickleColor.Parse(slots[i]) is { IsRgb: true } slot && Distance(rgb, slot) is var d && d < best)
                {
                    (nearest, best) = (i, d);
                }
            }

            return target[nearest];
        }

        SegmentStyle Remap(SegmentStyle s) => new()
        {
            Type = s.Type,
            Foreground = Map(s.Foreground),
            Background = Map(s.Background),
            Template = s.Template,
            Icon = s.Icon,
            Options = new Dictionary<string, string>(s.Options, StringComparer.OrdinalIgnoreCase),
        };

        var prompt = layout.Prompt;
        return new Theme
        {
            Name = name,
            Description = $"Imported from the terminal color scheme \"{scheme.Name}\" with the {layout.Name} layout.",
            Terminal = p,
            Syntax = new SyntaxColors
            {
                Default = "default",
                Command = p.Green,
                UnknownCommand = p.Red,
                Parameter = p.BrightBlack,
                String = p.Yellow,
                Number = p.BrightPurple,
                Variable = p.Cyan,
                Operator = p.BrightBlack,
                Keyword = p.Purple,
                Comment = p.BrightBlack,
                Type = p.Blue,
                Member = p.Foreground,
                Error = p.BrightRed,
                Suggestion = p.BrightBlack,
                SelectionBackground = p.SelectionBackground,
                Translated = p.BrightBlack,
            },
            Ui = new UiColors
            {
                Accent = p.Green,
                Muted = p.BrightBlack,
                Success = p.Green,
                Warning = p.Yellow,
                Error = p.Red,
                Info = p.Cyan,
                PanelBackground = p.Background,
                PanelForeground = p.Foreground,
                PanelBorder = p.SelectionBackground,
                HighlightBackground = p.SelectionBackground,
                HighlightForeground = dark ? p.BrightWhite : p.Black,
                MenuBackground = Mix(p.Background, p.Foreground, 0.06),
                MenuForeground = p.Foreground,
                MenuSelectedBackground = p.Green,
                MenuSelectedForeground = p.Background,
                MenuDescription = p.BrightBlack,
                MatchHighlight = p.Yellow,
            },
            Prompt = new PromptTheme
            {
                Left = [.. prompt.Left.Select(Remap)],
                Right = [.. prompt.Right.Select(Remap)],
                Separator = prompt.Separator,
                NewlineBeforeInput = prompt.NewlineBeforeInput,
                PromptChar = prompt.PromptChar,
                PromptCharColor = Map(prompt.PromptCharColor),
                PromptCharErrorColor = Map(prompt.PromptCharErrorColor),
                ContinuationPrompt = prompt.ContinuationPrompt,
                TransientTemplate = prompt.TransientTemplate,
                Animation = prompt.Animation is { } a
                    ? new PromptAnimation
                    {
                        Effect = a.Effect,
                        Colors = [.. a.Colors.Select(c => Map(c) ?? c)],
                        FrameMs = a.FrameMs,
                        PeriodMs = a.PeriodMs,
                        Spread = a.Spread,
                        Intensity = a.Intensity,
                        Target = a.Target,
                        PromptChar = a.PromptChar,
                        PromptChars = [.. a.PromptChars],
                        PromptCharFrames = a.PromptCharFrames,
                    }
                    : null,
            },
        };
    }

    /// <summary>A theme file name from a scheme name: "One Half Dark" → "one-half-dark".</summary>
    public static string ThemeName(string schemeName)
    {
        var sb = new StringBuilder();
        foreach (var c in schemeName.Trim().ToLowerInvariant())
        {
            if (char.IsAsciiLetterOrDigit(c) || c is '_' or '.')
            {
                sb.Append(c);
            }
            else if (sb.Length > 0 && sb[^1] != '-')
            {
                sb.Append('-');
            }
        }

        var name = sb.ToString().Trim('-', '.');
        return name.Length == 0 ? "imported" : name[..Math.Min(name.Length, 64)];
    }

    // Background and foreground first, so text colors match them rather than black or white.
    private static string[] Slots(TerminalPalette t) =>
    [
        t.Background, t.Foreground, t.Black, t.Red, t.Green, t.Yellow, t.Blue, t.Purple, t.Cyan, t.White,
        t.BrightBlack, t.BrightRed, t.BrightGreen, t.BrightYellow, t.BrightBlue, t.BrightPurple, t.BrightCyan, t.BrightWhite,
    ];

    // "Redmean" weighted RGB distance: close to perceived difference without a color-space conversion.
    private static double Distance(PickleColor a, PickleColor b)
    {
        var r = (a.R + b.R) / 2.0;
        double dr = a.R - b.R, dg = a.G - b.G, db = a.B - b.B;
        return ((2 + (r / 256)) * dr * dr) + (4 * dg * dg) + ((2 + ((255 - r) / 256)) * db * db);
    }

    private static double Luminance(string color) =>
        PickleColor.Parse(color) is { IsRgb: true } c ? ((0.2126 * c.R) + (0.7152 * c.G) + (0.0722 * c.B)) / 255 : 0;

    private static string Mix(string from, string to, double amount)
    {
        if (PickleColor.Parse(from) is not { IsRgb: true } a || PickleColor.Parse(to) is not { IsRgb: true } b)
        {
            return from;
        }

        static byte Channel(byte x, byte y, double t) => (byte)Math.Round(x + ((y - x) * t));
        return PickleColor.FromRgb(Channel(a.R, b.R, amount), Channel(a.G, b.G, amount), Channel(a.B, b.B, amount)).ToHex();
    }
}
