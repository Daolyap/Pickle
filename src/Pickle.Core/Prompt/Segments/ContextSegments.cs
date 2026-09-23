using System.Globalization;
using Pickle.Abstractions;

namespace Pickle.Core.Prompt.Segments;

// Small segments that only read the PromptContext.

/// <summary>"✘ 1" after a failed command, hidden on success. Option <c>symbol</c>.</summary>
public sealed class StatusSegment : IPromptSegment
{
    public string Type => "status";

    public ValueTask<PromptSegmentOutput?> RenderAsync(PromptContext context, SegmentStyle style, CancellationToken cancellationToken)
    {
        if (context.LastCommandSucceeded)
        {
            return SegmentText.Hide();
        }

        var symbol = style.Option("symbol") ?? "✘";
        return SegmentText.Show(context.LastExitCode is { } code and not 0
            ? symbol + " " + code.ToString(CultureInfo.InvariantCulture)
            : symbol);
    }
}

/// <summary>Current time. Option <c>format</c> (.NET format string, default HH:mm:ss).</summary>
public sealed class TimeSegment : IPromptSegment
{
    public const string DefaultFormat = "HH:mm:ss";

    public string Type => "time";

    public ValueTask<PromptSegmentOutput?> RenderAsync(PromptContext context, SegmentStyle style, CancellationToken cancellationToken) =>
        SegmentText.Show(Format(context.Now, style.Option("format")));

    public static string Format(DateTimeOffset now, string? format)
    {
        try
        {
            return now.ToString(format ?? DefaultFormat, CultureInfo.InvariantCulture);
        }
        catch (FormatException)
        {
            return now.ToString(DefaultFormat, CultureInfo.InvariantCulture);
        }
    }
}

/// <summary>User name.</summary>
public sealed class UserSegment : IPromptSegment
{
    public string Type => "user";

    public ValueTask<PromptSegmentOutput?> RenderAsync(PromptContext context, SegmentStyle style, CancellationToken cancellationToken) =>
        string.IsNullOrEmpty(context.UserName) ? SegmentText.Hide() : SegmentText.Show(SegmentText.Sanitize(context.UserName));
}

/// <summary>Machine name.</summary>
public sealed class HostSegment : IPromptSegment
{
    public string Type => "host";

    public ValueTask<PromptSegmentOutput?> RenderAsync(PromptContext context, SegmentStyle style, CancellationToken cancellationToken) =>
        string.IsNullOrEmpty(context.HostName) ? SegmentText.Hide() : SegmentText.Show(SegmentText.Sanitize(context.HostName));
}

/// <summary>Shown when elevated. Option <c>symbol</c> (default ⚡; plain-text themes use "ADMIN").</summary>
public sealed class AdminSegment : IPromptSegment
{
    public string Type => "admin";

    public ValueTask<PromptSegmentOutput?> RenderAsync(PromptContext context, SegmentStyle style, CancellationToken cancellationToken) =>
        context.IsAdmin ? SegmentText.Show(style.Option("symbol") ?? "⚡") : SegmentText.Hide();
}

/// <summary>Number of running background jobs, hidden when zero.</summary>
public sealed class JobsSegment : IPromptSegment
{
    public string Type => "jobs";

    public ValueTask<PromptSegmentOutput?> RenderAsync(PromptContext context, SegmentStyle style, CancellationToken cancellationToken) =>
        context.JobCount > 0 ? SegmentText.Show(context.JobCount.ToString(CultureInfo.InvariantCulture)) : SegmentText.Hide();
}

/// <summary>Static text: the style's Template (with {value} = option <c>text</c>).</summary>
public sealed class TextSegment : IPromptSegment
{
    public string Type => "text";

    public ValueTask<PromptSegmentOutput?> RenderAsync(PromptContext context, SegmentStyle style, CancellationToken cancellationToken) =>
        SegmentText.Show(style.Option("text") ?? string.Empty);
}
