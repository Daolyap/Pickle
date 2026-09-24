using System.Text;
using Pickle.Abstractions;

namespace Pickle.Core.Prompt;

/// <summary>A segment after templating, with its final colors.</summary>
public readonly record struct RenderedSegment(string Text, string? Foreground, string? Background);

/// <summary>Joins rendered segments with the theme's separator style. Pure: no I/O, no state.</summary>
public static class PromptLayout
{
    // Nerd Font / Powerline glyphs (private use area).
    public const string PowerlineRight = "";
    public const string PowerlineLeft = "";
    public const string RoundRight = "";
    public const string RoundLeft = "";
    public const string SlantRight = "";
    public const string SlantLeft = "";

    /// <summary>Left prompt segments: blocks flow left to right, separators point right.</summary>
    public static string Left(IReadOnlyList<RenderedSegment> segments, SeparatorStyle style)
    {
        if (segments.Count == 0)
        {
            return string.Empty;
        }

        if (Glyphs(style) is not { } glyphs)
        {
            return Simple(segments, style);
        }

        var sb = new StringBuilder();
        if (glyphs.LeftCap is { } cap)
        {
            sb.Append(Ansi.Colorize(cap, segments[0].Background));
        }

        for (var i = 0; i < segments.Count; i++)
        {
            var s = segments[i];
            sb.Append(Ansi.Colorize(s.Text, s.Foreground, s.Background));
            var next = i + 1 < segments.Count ? segments[i + 1].Background : null;
            sb.Append(Transition(glyphs.Right, s.Background, next));
        }

        return sb.ToString();
    }

    /// <summary>Right prompt segments: separators point left, a round style closes with a cap.</summary>
    public static string Right(IReadOnlyList<RenderedSegment> segments, SeparatorStyle style)
    {
        if (segments.Count == 0)
        {
            return string.Empty;
        }

        if (Glyphs(style) is not { } glyphs)
        {
            return Simple(segments, style);
        }

        var sb = new StringBuilder();
        for (var i = 0; i < segments.Count; i++)
        {
            var s = segments[i];
            var previous = i > 0 ? segments[i - 1].Background : null;
            sb.Append(Transition(glyphs.Left, s.Background, previous));
            sb.Append(Ansi.Colorize(s.Text, s.Foreground, s.Background));
        }

        if (glyphs.RightCap is { } cap)
        {
            sb.Append(Ansi.Colorize(cap, segments[^1].Background));
        }

        return sb.ToString();
    }

    private static string Simple(IReadOnlyList<RenderedSegment> segments, SeparatorStyle style) =>
        string.Join(style == SeparatorStyle.None ? string.Empty : " ", segments.Select(s => Ansi.Colorize(s.Text, s.Foreground, s.Background)));

    /// <summary>A separator glyph drawn in the block's color over the neighbor's background.</summary>
    private static string Transition(string glyph, string? blockBackground, string? neighborBackground) =>
        PickleColor.Parse(blockBackground) is null
            ? (PickleColor.Parse(neighborBackground) is null ? " " : Ansi.Colorize(" ", null, neighborBackground))
            : Ansi.Colorize(glyph, blockBackground, neighborBackground);

    private static (string Right, string Left, string? LeftCap, string? RightCap)? Glyphs(SeparatorStyle style) => style switch
    {
        SeparatorStyle.Powerline => (PowerlineRight, PowerlineLeft, null, null),
        SeparatorStyle.Round => (RoundRight, RoundLeft, RoundLeft, RoundRight),
        SeparatorStyle.Slant => (SlantRight, SlantLeft, null, null),
        _ => null,
    };
}
