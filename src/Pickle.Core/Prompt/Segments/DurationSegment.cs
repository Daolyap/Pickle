using System.Globalization;
using Pickle.Abstractions;

namespace Pickle.Core.Prompt.Segments;

/// <summary>
/// How long the last command took, when at least prompt.durationThresholdMs (option <c>thresholdMs</c> overrides).
/// </summary>
public sealed class DurationSegment(SegmentEnvironment environment) : IPromptSegment
{
    public string Type => "duration";

    public ValueTask<PromptSegmentOutput?> RenderAsync(PromptContext context, SegmentStyle style, CancellationToken cancellationToken)
    {
        var threshold = style.OptionInt("thresholdMs", environment.DurationThresholdMs());
        return context.LastCommandDuration is { } duration && duration.TotalMilliseconds >= threshold
            ? SegmentText.Show(Humanize(duration))
            : SegmentText.Hide();
    }

    /// <summary>350ms · 2.3s · 1m 04s · 1h 02m · 2d 03h.</summary>
    public static string Humanize(TimeSpan duration)
    {
        var c = CultureInfo.InvariantCulture;
        if (duration < TimeSpan.Zero)
        {
            duration = TimeSpan.Zero;
        }

        if (duration.TotalSeconds < 1)
        {
            return ((int)duration.TotalMilliseconds).ToString(c) + "ms";
        }

        if (duration.TotalMinutes < 1)
        {
            // Truncate rather than round so 59.96s never prints as "60.0s".
            return (Math.Floor(duration.TotalSeconds * 10) / 10).ToString("0.0", c) + "s";
        }

        if (duration.TotalHours < 1)
        {
            return string.Create(c, $"{duration.Minutes}m {duration.Seconds:00}s");
        }

        if (duration.TotalDays < 1)
        {
            return string.Create(c, $"{duration.Hours}h {duration.Minutes:00}m");
        }

        return string.Create(c, $"{(int)duration.TotalDays}d {duration.Hours:00}h");
    }
}
