using System.Diagnostics;
using System.Globalization;
using Pickle.Abstractions;
using Pickle.Core.Contracts;
using Pickle.Core.Prompt.Segments;

namespace Pickle.Core.Prompt;

/// <summary>Turns a theme + context into prompt text using a segment lookup and a <see cref="SegmentCache"/>.</summary>
internal sealed class PromptComposer(Func<string, IPromptSegment?> lookup, SegmentCache cache, CancellationToken lifetime)
{
    private static readonly string[] ProtectedTypes = ["cwd", "admin", "status", "text"];

    public PromptRender Compose(PromptContext context, Theme theme, int timeoutMs)
    {
        timeoutMs = Math.Max(0, timeoutMs);
        var deadline = Stopwatch.GetTimestamp() + (long)(timeoutMs * (Stopwatch.Frequency / 1000.0));
        var leftSlots = Begin(theme.Prompt.Left, context);
        var rightSlots = Begin(theme.Prompt.Right, context);
        var left = Collect(leftSlots, deadline, timeoutMs);
        var right = Collect(rightSlots, deadline, timeoutMs);

        var prompt = theme.Prompt;
        var promptChar = PromptChar(context, theme);
        var leftText = ComposeLeft(left, prompt, promptChar);
        while (context.TerminalWidth > 0 && WidestLine(leftText) >= context.TerminalWidth && DropOne(left))
        {
            leftText = ComposeLeft(left, prompt, promptChar);
        }

        var rightText = PromptLayout.Right([.. right.Select(r => r.Segment)], prompt.Separator);
        if (rightText.Length == 0 || !Fits(leftText, rightText, context.TerminalWidth))
        {
            rightText = null;
        }

        return new PromptRender(leftText, rightText, Ansi.Colorize(prompt.ContinuationPrompt, theme.Ui.Muted));
    }

    public void Prefetch(PromptContext context, Theme theme)
    {
        Begin(theme.Prompt.Left, context);
        Begin(theme.Prompt.Right, context);
    }

    public string ComposeTransient(PromptContext context, Theme theme)
    {
        var template = string.IsNullOrEmpty(theme.Prompt.TransientTemplate) ? "{promptChar} " : theme.Prompt.TransientTemplate;
        var result = template.Replace("{promptChar}", PromptChar(context, theme), StringComparison.Ordinal);
        if (result.Contains("{time}", StringComparison.Ordinal))
        {
            result = result.Replace("{time}", Ansi.Colorize(context.Now.ToString("HH:mm:ss", CultureInfo.InvariantCulture), theme.Ui.Muted), StringComparison.Ordinal);
        }

        if (result.Contains("{cwd}", StringComparison.Ordinal))
        {
            var style = theme.Prompt.Left.Concat(theme.Prompt.Right).FirstOrDefault(s => s.Type.Equals("cwd", StringComparison.OrdinalIgnoreCase));
            var cwd = lookup("cwd") is CwdSegment segment
                ? segment.Render(context.Cwd, style ?? new SegmentStyle { Type = "cwd" })
                : SegmentText.Sanitize(context.Cwd);
            result = result.Replace("{cwd}", Ansi.Colorize(cwd, style?.Foreground), StringComparison.Ordinal);
        }

        return result;
    }

    public static string PromptChar(PromptContext context, Theme theme) =>
        Ansi.Colorize(theme.Prompt.PromptChar, context.LastCommandSucceeded ? theme.Prompt.PromptCharColor : theme.Prompt.PromptCharErrorColor);

    /// <summary>Applies a segment template: {value} is the segment text, {icon} the style's icon (dropped with its space when unset).</summary>
    public static string ApplyTemplate(string? template, string value, string? icon)
    {
        var hasIcon = !string.IsNullOrEmpty(icon);
        template ??= hasIcon ? "{icon} {value}" : "{value}";
        if (!hasIcon)
        {
            template = template
                .Replace("{icon} ", string.Empty, StringComparison.Ordinal)
                .Replace(" {icon}", string.Empty, StringComparison.Ordinal)
                .Replace("{icon}", string.Empty, StringComparison.Ordinal);
        }

        return template.Replace("{icon}", icon, StringComparison.Ordinal).Replace("{value}", value, StringComparison.Ordinal);
    }

    private static string ComposeLeft(List<(SegmentStyle Style, RenderedSegment Segment)> left, PromptTheme prompt, string promptChar)
    {
        var segments = PromptLayout.Left([.. left.Select(l => l.Segment)], prompt.Separator);
        if (promptChar.Length == 0)
        {
            return segments.Length > 0 && prompt.Separator != SeparatorStyle.None ? segments + " " : segments;
        }

        if (prompt.NewlineBeforeInput)
        {
            return (segments.Length > 0 ? segments + "\n" : string.Empty) + promptChar + " ";
        }

        var gap = segments.Length > 0 && prompt.Separator != SeparatorStyle.None ? " " : string.Empty;
        return segments + gap + promptChar + " ";
    }

    /// <summary>On narrow terminals, drop the last informational segment (never the directory, static text, elevation or failure marker).</summary>
    private static bool DropOne(List<(SegmentStyle Style, RenderedSegment Segment)> left)
    {
        for (var i = left.Count - 1; i >= 0; i--)
        {
            if (!ProtectedTypes.Contains(left[i].Style.Type, StringComparer.OrdinalIgnoreCase))
            {
                left.RemoveAt(i);
                return true;
            }
        }

        return false;
    }

    private static int WidestLine(string text) => text.Split('\n').Max(TextWidth.VisibleWidth);

    private static bool Fits(string left, string right, int width)
    {
        if (width <= 0)
        {
            return true;
        }

        var lastLine = left[(left.LastIndexOf('\n') + 1)..];

        // One column of breathing room plus one for the cursor.
        return TextWidth.VisibleWidth(lastLine) + TextWidth.VisibleWidth(right) + 2 <= width;
    }

    private List<(SegmentStyle Style, SegmentCache.Slot Slot)> Begin(IEnumerable<SegmentStyle> styles, PromptContext context)
    {
        var slots = new List<(SegmentStyle, SegmentCache.Slot)>();
        foreach (var style in styles)
        {
            if (lookup(style.Type) is not { } segment)
            {
                continue;
            }

            var slot = cache.Begin(Key(style, context.Cwd), () => segment.RenderAsync(context, style, lifetime));
            slots.Add((style, slot));
        }

        return slots;
    }

    private List<(SegmentStyle Style, RenderedSegment Segment)> Collect(List<(SegmentStyle Style, SegmentCache.Slot Slot)> slots, long deadline, int timeoutMs)
    {
        var rendered = new List<(SegmentStyle, RenderedSegment)>(slots.Count);
        foreach (var (style, slot) in slots)
        {
            if (cache.Collect(slot, deadline, timeoutMs) is not { } output)
            {
                continue;
            }

            var text = ApplyTemplate(style.Template, output.Text, style.Icon);
            if (text.Length > 0)
            {
                rendered.Add((style, new RenderedSegment(text, output.Foreground ?? style.Foreground, output.Background ?? style.Background)));
            }
        }

        return rendered;
    }

    private static string Key(SegmentStyle style, string cwd)
    {
        var options = style.Options.Count == 0
            ? string.Empty
            : string.Join(';', style.Options.OrderBy(o => o.Key, StringComparer.OrdinalIgnoreCase).Select(o => o.Key + "=" + o.Value));
        return style.Type.ToLowerInvariant() + "\u0001" + cwd + "\u0001" + options;
    }
}
