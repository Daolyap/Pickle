using Pickle.Abstractions;
using Pickle.Abstractions.Services;

namespace Pickle.Core.Prompt.Segments.Live;

/// <summary>What is playing, from the cache the background job fills. Hidden while paused when <c>music.hideWhenPaused</c> is on, and when nothing is open.</summary>
public sealed class MusicSegment : IPromptSegment
{
    public const string CacheKey = "music";

    public string Type => "music";

    public ValueTask<PromptSegmentOutput?> RenderAsync(PromptContext context, SegmentStyle style, CancellationToken cancellationToken)
    {
        if (context.Pickle is not { } pickle || pickle.Config.Current.Music is not { Enabled: true } settings
            || pickle.Services.Get<IBackgroundWork>()?.Read<NowPlaying?>(CacheKey) is not { Value: { } playing } cached)
        {
            return SegmentText.Hide();
        }

        if (context.Now - cached.At > TimeSpan.FromSeconds(Math.Max(15, settings.PollSeconds) * 4)
            || (playing.State != PlaybackState.Playing && settings.HideWhenPaused))
        {
            return SegmentText.Hide();
        }

        var format = style.Options.TryGetValue("format", out var custom) && custom.Length > 0 ? custom : settings.Format;
        var text = Render(playing, format);
        var max = style.OptionInt("maxLength", settings.MaxLength);
        return SegmentText.Show(SegmentText.Sanitize(TextWidth.Truncate(text, Math.Max(10, max))));
    }

    public static string Render(NowPlaying playing, string format)
    {
        var template = string.IsNullOrWhiteSpace(format) ? "♪ {artist} – {title}" : format;
        if (playing.Artist.Length == 0)
        {
            template = template.Replace("{artist} – ", string.Empty, StringComparison.Ordinal).Replace("{artist} - ", string.Empty, StringComparison.Ordinal).Replace("{artist}", string.Empty, StringComparison.Ordinal);
        }

        return template
            .Replace("{artist}", playing.Artist, StringComparison.Ordinal)
            .Replace("{title}", playing.Title, StringComparison.Ordinal)
            .Replace("{player}", playing.Player ?? string.Empty, StringComparison.Ordinal)
            .Replace("{state}", playing.State.ToString().ToLowerInvariant(), StringComparison.Ordinal)
            .Trim();
    }
}

internal static class MusicJob
{
    public static BackgroundJob Create(IPickleContext pickle, IBackgroundWork background, IMusicSource source, Func<DateTimeOffset>? now = null)
    {
        now ??= () => DateTimeOffset.UtcNow;
        return new BackgroundJob(MusicSegment.CacheKey, async cancellationToken =>
        {
            var settings = pickle.Config.Current.Music;
            if (!settings.Enabled || !source.IsSupported)
            {
                return;
            }

            if (background.Read<NowPlaying?>(MusicSegment.CacheKey) is { } last && now() - last.At < TimeSpan.FromSeconds(Math.Max(15, settings.PollSeconds) - 1))
            {
                return;
            }

            try
            {
                background.Write<NowPlaying?>(MusicSegment.CacheKey, await source.GetAsync(settings.Player, cancellationToken).ConfigureAwait(false));
            }
            catch (Exception ex) when (ex is InvalidOperationException or TaskCanceledException or IOException)
            {
                pickle.Log.Debug("music", "poll failed: " + ex.Message);
            }
        })
        {
            Interval = TimeSpan.FromSeconds(15),
            InitialDelay = TimeSpan.FromSeconds(5),
        };
    }
}
