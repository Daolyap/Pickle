using Pickle.Abstractions;
using Pickle.Abstractions.Services;

namespace Pickle.Core.Prompt.Segments.Live;

/// <summary>
/// The weather, from the cache the background job fills (never a network call from the prompt). Hidden until
/// <c>weather.enabled</c> is on and a reading exists, and again when the reading is older than three refresh periods.
/// </summary>
public sealed class WeatherSegment : IPromptSegment
{
    public const string CacheKey = "weather";

    public string Type => "weather";

    public ValueTask<PromptSegmentOutput?> RenderAsync(PromptContext context, SegmentStyle style, CancellationToken cancellationToken)
    {
        if (context.Pickle is not { } pickle || pickle.Config.Current.Weather is not { Enabled: true } settings
            || pickle.Services.Get<IBackgroundWork>()?.Read<WeatherReading>(CacheKey) is not { } cached)
        {
            return SegmentText.Hide();
        }

        var maxAge = TimeSpan.FromMinutes(Math.Max(5, settings.RefreshMinutes) * 3);
        if (context.Now - cached.At > maxAge)
        {
            return SegmentText.Hide();
        }

        var format = style.Options.TryGetValue("format", out var custom) && custom.Length > 0 ? custom : settings.Format;
        return SegmentText.Show(SegmentText.Sanitize(WeatherFormat.Render(cached.Value, format)));
    }
}

/// <summary>The background job: fetch when the cached reading is older than <c>weather.refreshMinutes</c>; keep the old one when a fetch fails.</summary>
internal static class WeatherJob
{
    public static BackgroundJob Create(IPickleContext pickle, IBackgroundWork background, IWeatherProvider provider, Func<DateTimeOffset>? now = null)
    {
        now ??= () => DateTimeOffset.UtcNow;
        return new BackgroundJob(WeatherSegment.CacheKey, async cancellationToken =>
        {
            var settings = pickle.Config.Current.Weather;
            if (!settings.Enabled || string.IsNullOrWhiteSpace(settings.Location))
            {
                return;
            }

            if (background.Read<WeatherReading>(WeatherSegment.CacheKey) is { } last && now() - last.At < TimeSpan.FromMinutes(Math.Max(5, settings.RefreshMinutes)))
            {
                return;
            }

            try
            {
                if (await provider.GetAsync(settings, cancellationToken).ConfigureAwait(false) is { } reading)
                {
                    background.Write(WeatherSegment.CacheKey, reading);
                }
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException or InvalidOperationException)
            {
                pickle.Log.Debug("weather", "refresh failed: " + ex.Message);
            }
        })
        {
            Interval = TimeSpan.FromMinutes(5),
            InitialDelay = TimeSpan.FromSeconds(3),
        };
    }
}
