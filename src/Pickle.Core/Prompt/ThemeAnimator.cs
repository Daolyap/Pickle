using System.Globalization;
using Pickle.Abstractions;

namespace Pickle.Core.Prompt;

/// <summary>
/// Frames of an animated theme (<see cref="PromptTheme.Animation"/>): each frame is a copy of the theme with the
/// segment and prompt-character colors (and the prompt character) of that moment. Pure functions of the frame index,
/// so a frame always looks the same and tests can pin one.
/// </summary>
public static class ThemeAnimator
{
    private const double ShimmerWidth = 0.35;

    public static bool IsAnimated(Theme theme) =>
        theme.Prompt.Animation is { } a && (EffectOf(a) != Effect.None || a.PromptChars.Count > 1);

    public static int FrameMs(PromptAnimation animation) => Math.Clamp(animation.FrameMs, 40, 2000);

    /// <summary>The frame shown <paramref name="elapsedMs"/> milliseconds into the animation.</summary>
    public static long FrameAt(PromptAnimation animation, long elapsedMs) => Math.Max(0, elapsedMs) / FrameMs(animation);

    public static Theme Frame(Theme theme, long frame)
    {
        if (theme.Prompt.Animation is not { } animation || !IsAnimated(theme))
        {
            return theme;
        }

        var frameMs = FrameMs(animation);
        var period = Math.Max(animation.PeriodMs, frameMs * 2);
        var t = frame * frameMs % period / (double)period;
        var effect = EffectOf(animation);
        var palette = theme.Terminal;
        var stops = animation.Colors.Select(c => ToRgb(c, palette)).OfType<Rgb>().ToList();
        var prompt = theme.Prompt;
        var left = prompt.Left.Select(Copy).ToList();
        var right = prompt.Right.Select(Copy).ToList();

        // Positions along the prompt: left segments, then the prompt character, then the right segments.
        var count = left.Count + right.Count + 1;
        if (effect == Effect.Wave && stops.Count < 2)
        {
            stops = [.. left.Concat(right).Where(Takes).Select(s => ToRgb(Animated(s, animation), palette)).OfType<Rgb>()];
            if (stops.Count < 2)
            {
                effect = Effect.Rainbow;
            }
        }

        string? Color(string? original, int index)
        {
            var position = count == 1 ? 0.5 : index / (double)(count - 1);
            var phase = Wrap(t - (index * animation.Spread));
            var rgb = ToRgb(original, palette);
            var intensity = Math.Clamp(animation.Intensity, 0, 1);
            Rgb? result = effect switch
            {
                Effect.Wave => Sample(stops, phase),
                Effect.Rainbow => rgb is { } c ? c.RotateHue(phase * 360) : Rgb.FromHsl(phase * 360, 0.65, 0.62),
                Effect.Pulse when rgb is { } c => c.Mix(stops.Count > 0 ? stops[0] : c.Mix(Rgb.White, 0.5), (1 - Math.Cos(phase * 2 * Math.PI)) / 2 * intensity),
                Effect.Shimmer when rgb is { } c => c.Mix(stops.Count > 0 ? stops[0] : Rgb.White, Band(position, t) * intensity),
                _ => null,
            };
            return result?.ToHex() ?? original;
        }

        for (var i = 0; i < left.Count; i++)
        {
            Recolor(left[i], animation, color => Color(color, i));
        }

        for (var i = 0; i < right.Count; i++)
        {
            Recolor(right[i], animation, color => Color(color, left.Count + 1 + i));
        }

        var promptChar = prompt.PromptChar;
        if (animation.PromptChars.Count > 0)
        {
            var width = animation.PromptChars.Max(TextWidth.VisibleWidth);
            var current = animation.PromptChars[(int)(frame / Math.Max(1, animation.PromptCharFrames) % animation.PromptChars.Count)];
            promptChar = current + new string(' ', width - TextWidth.VisibleWidth(current));
        }

        return new Theme
        {
            Name = theme.Name,
            Description = theme.Description,
            Terminal = theme.Terminal,
            Syntax = theme.Syntax,
            Ui = theme.Ui,
            Prompt = new PromptTheme
            {
                Left = left,
                Right = right,
                Separator = prompt.Separator,
                NewlineBeforeInput = prompt.NewlineBeforeInput,
                PromptChar = promptChar,
                PromptCharColor = animation.PromptChar && effect != Effect.None ? Color(prompt.PromptCharColor, left.Count) : prompt.PromptCharColor,
                PromptCharErrorColor = prompt.PromptCharErrorColor,
                ContinuationPrompt = prompt.ContinuationPrompt,
                TransientTemplate = prompt.TransientTemplate,
                Animation = animation,
            },
        };
    }

    private enum Effect
    {
        None,
        Wave,
        Rainbow,
        Pulse,
        Shimmer,
    }

    private static Effect EffectOf(PromptAnimation animation) => animation.Effect?.Trim().ToLowerInvariant() switch
    {
        "wave" or "gradient" => Effect.Wave,
        "rainbow" or "hue" => Effect.Rainbow,
        "pulse" or "breathe" => Effect.Pulse,
        "shimmer" or "shine" => Effect.Shimmer,
        _ => Effect.None,
    };

    private static bool AnimatesBackground(SegmentStyle style, PromptAnimation animation) =>
        animation.Target?.Trim().ToLowerInvariant() switch
        {
            "background" => true,
            "foreground" => false,
            _ => PickleColor.Parse(style.Background) is not null,
        };

    private static string? Animated(SegmentStyle style, PromptAnimation animation) =>
        AnimatesBackground(style, animation) ? style.Background : style.Foreground;

    /// <summary>Status and elevation keep their warning colors; any segment can opt out with the option animate: false.</summary>
    private static bool Takes(SegmentStyle style) =>
        !style.Type.Equals("status", StringComparison.OrdinalIgnoreCase) && !style.Type.Equals("admin", StringComparison.OrdinalIgnoreCase)
        && !(style.Options.TryGetValue("animate", out var value) && value.Trim().Equals("false", StringComparison.OrdinalIgnoreCase));

    private static void Recolor(SegmentStyle style, PromptAnimation animation, Func<string?, string?> color)
    {
        if (!Takes(style))
        {
            return;
        }

        if (AnimatesBackground(style, animation))
        {
            style.Background = color(style.Background);
        }
        else
        {
            style.Foreground = color(style.Foreground);
        }
    }

    private static SegmentStyle Copy(SegmentStyle style) => new()
    {
        Type = style.Type,
        Foreground = style.Foreground,
        Background = style.Background,
        Template = style.Template,
        Icon = style.Icon,
        Options = style.Options,
    };

    private static double Wrap(double value) => value - Math.Floor(value);

    /// <summary>Brightness of a band sweeping left to right once per cycle (0 off it, 1 at its center).</summary>
    private static double Band(double position, double t)
    {
        var center = (t * (1 + (2 * ShimmerWidth))) - ShimmerWidth;
        var distance = Math.Abs(position - center) / ShimmerWidth;
        return distance >= 1 ? 0 : (1 + Math.Cos(distance * Math.PI)) / 2;
    }

    private static Rgb? Sample(List<Rgb> stops, double position)
    {
        if (stops.Count == 0)
        {
            return null;
        }

        var scaled = Wrap(position) * stops.Count;
        var index = (int)Math.Floor(scaled) % stops.Count;
        return stops[index].Mix(stops[(index + 1) % stops.Count], scaled - Math.Floor(scaled));
    }

    /// <summary>A theme color as RGB; ANSI names resolve through the theme's terminal palette.</summary>
    private static Rgb? ToRgb(string? color, TerminalPalette palette)
    {
        if (PickleColor.Parse(color) is not { } parsed)
        {
            return null;
        }

        if (parsed.IsRgb)
        {
            return new Rgb(parsed.R, parsed.G, parsed.B);
        }

        string[] named =
        [
            palette.Black, palette.Red, palette.Green, palette.Yellow, palette.Blue, palette.Purple, palette.Cyan, palette.White,
            palette.BrightBlack, palette.BrightRed, palette.BrightGreen, palette.BrightYellow, palette.BrightBlue, palette.BrightPurple, palette.BrightCyan, palette.BrightWhite,
        ];
        return parsed.AnsiIndex is >= 0 and < 16 && PickleColor.Parse(named[parsed.AnsiIndex]) is { IsRgb: true } p ? new Rgb(p.R, p.G, p.B) : null;
    }

    private readonly record struct Rgb(double R, double G, double B)
    {
        public static Rgb White => new(255, 255, 255);

        public Rgb Mix(Rgb other, double amount)
        {
            amount = Math.Clamp(amount, 0, 1);
            return new Rgb(R + ((other.R - R) * amount), G + ((other.G - G) * amount), B + ((other.B - B) * amount));
        }

        public string ToHex() => string.Create(CultureInfo.InvariantCulture, $"#{Byte(R):X2}{Byte(G):X2}{Byte(B):X2}");

        public Rgb RotateHue(double degrees)
        {
            var (h, s, l) = ToHsl();
            return FromHsl(h + degrees, s, l);
        }

        public static Rgb FromHsl(double hue, double saturation, double lightness)
        {
            hue = ((hue % 360) + 360) % 360 / 360;
            if (saturation <= 0)
            {
                return new Rgb(lightness * 255, lightness * 255, lightness * 255);
            }

            var q = lightness < 0.5 ? lightness * (1 + saturation) : lightness + saturation - (lightness * saturation);
            var p = (2 * lightness) - q;
            return new Rgb(Channel(p, q, hue + (1 / 3.0)) * 255, Channel(p, q, hue) * 255, Channel(p, q, hue - (1 / 3.0)) * 255);
        }

        private (double H, double S, double L) ToHsl()
        {
            double r = R / 255, g = G / 255, b = B / 255;
            var max = Math.Max(r, Math.Max(g, b));
            var min = Math.Min(r, Math.Min(g, b));
            var l = (max + min) / 2;
            if (max - min < 1e-9)
            {
                return (0, 0, l);
            }

            var d = max - min;
            var s = l > 0.5 ? d / (2 - max - min) : d / (max + min);
            var h = max == r ? ((g - b) / d) + (g < b ? 6 : 0) : max == g ? ((b - r) / d) + 2 : ((r - g) / d) + 4;
            return (h * 60, s, l);
        }

        private static double Channel(double p, double q, double t)
        {
            t = t < 0 ? t + 1 : t > 1 ? t - 1 : t;
            return t < 1 / 6.0 ? p + ((q - p) * 6 * t) : t < 0.5 ? q : t < 2 / 3.0 ? p + ((q - p) * ((2 / 3.0) - t) * 6) : p;
        }

        private static int Byte(double value) => (int)Math.Round(Math.Clamp(value, 0, 255));
    }
}
