using System.Net;
using Pickle.Abstractions;
using Pickle.Abstractions.Services;
using Pickle.Core.Prompt.Segments;
using Pickle.Core.Prompt.Segments.Live;
using Pickle.Testing;
using Pickle.Testing.Fakes;

namespace Pickle.Core.Tests.Prompt.Live;

public class LiveSegmentTests
{
    private sealed class MemoryBackground : IBackgroundWork
    {
        private readonly Dictionary<string, object> _values = [];

        public bool IsPrimary => true;

        public List<BackgroundJob> Jobs { get; } = [];

        public void Register(BackgroundJob job) => Jobs.Add(job);

        public CachedValue<T>? Read<T>(string key) => _values.TryGetValue(key, out var v) ? (CachedValue<T>)v : null;

        public void Write<T>(string key, T value) => _values[key] = new CachedValue<T>(Clock, value);

        public DateTimeOffset Clock { get; set; } = DateTimeOffset.UtcNow;
    }

    private sealed class FakeWeather(WeatherReading? reading) : IWeatherProvider
    {
        public int Calls { get; private set; }

        public Task<WeatherReading?> GetAsync(WeatherSettings settings, CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(reading);
        }
    }

    private sealed class FakeMusic(NowPlaying? playing) : IMusicSource
    {
        public bool IsSupported => true;

        public int Calls { get; private set; }

        public Task<NowPlaying?> GetAsync(string player, CancellationToken cancellationToken = default)
        {
            Calls++;
            return Task.FromResult(playing);
        }
    }

    private static readonly WeatherReading Sunny = new(21.4, 19.6, 0, true, "C", 12.3, "km/h", "Paris");

    private static (TestPickle T, MemoryBackground Background) Start(Action<PickleConfig>? configure = null)
    {
        var t = TestPickle.Create(start: true, configure: configure);
        var background = new MemoryBackground();
        t.Runtime.Services.Add<IBackgroundWork>(background);
        return (t, background);
    }

    private static async Task<string?> Render(TestPickle t, IPromptSegment segment, SegmentStyle? style = null)
    {
        var output = await segment.RenderAsync(t.Runtime.CreatePromptContext(), style ?? new SegmentStyle { Type = segment.Type }, CancellationToken.None);
        return output?.Text;
    }

    [Theory]
    [InlineData("{icon} {temp}{unit}", "☀ 21°C")]
    [InlineData("{temp}° {condition}, feels {feels}°, {wind} {city}", "21° Clear, feels 20°, 12 km/h Paris")]
    [InlineData("", "☀ 21°C")]
    public void WeatherFormatsPlaceholders(string format, string expected) => Assert.Equal(expected, WeatherFormat.Render(Sunny, format));

    [Theory]
    [InlineData(0, true, "☀")]
    [InlineData(0, false, "☾")]
    [InlineData(3, true, "☁")]
    [InlineData(63, true, "🌧")]
    [InlineData(73, true, "❄")]
    [InlineData(96, true, "⛈")]
    public void WeatherCodesHaveIcons(int code, bool day, string icon) => Assert.Equal(icon, WeatherFormat.Icon(code, day));

    [Fact]
    public async Task WeatherIsHiddenUntilEnabledAndOnlyShownFromFreshCache()
    {
        var (t, background) = Start();
        using var _ = t;
        var segment = new WeatherSegment();
        background.Write(WeatherSegment.CacheKey, Sunny);

        Assert.Null(await Render(t, segment));

        t.Runtime.Config.Update(c => c.Weather.Enabled = true);
        Assert.Equal("☀ 21°C", await Render(t, segment));
        Assert.Equal("21 Paris", await Render(t, segment, new SegmentStyle { Type = "weather", Options = { ["format"] = "{temp} {city}" } }));

        background.Clock = DateTimeOffset.UtcNow.AddHours(-3);
        background.Write(WeatherSegment.CacheKey, Sunny);
        Assert.Null(await Render(t, segment));
    }

    [Fact]
    public async Task TheWeatherJobFetchesWhenStaleKeepsTheOldValueOnFailureAndSkipsWhenOff()
    {
        var (t, background) = Start(c => (c.Weather.Enabled, c.Weather.Location, c.Weather.RefreshMinutes) = (true, "Paris", 30));
        using var _ = t;
        var provider = new FakeWeather(Sunny);
        var now = DateTimeOffset.UtcNow;
        var job = WeatherJob.Create(t.Runtime, background, provider, () => now);

        await job.Run(CancellationToken.None);
        Assert.Equal(1, provider.Calls);
        Assert.Equal(21.4, background.Read<WeatherReading>(WeatherSegment.CacheKey)!.Value.Temperature);

        await job.Run(CancellationToken.None);
        Assert.Equal(1, provider.Calls);

        now = now.AddMinutes(31);
        background.Clock = now;
        var failing = WeatherJob.Create(t.Runtime, background, new FakeWeather(null), () => now);
        await failing.Run(CancellationToken.None);
        Assert.Equal(21.4, background.Read<WeatherReading>(WeatherSegment.CacheKey)!.Value.Temperature);

        t.Runtime.Config.Update(c => c.Weather.Enabled = false);
        now = now.AddHours(2);
        await job.Run(CancellationToken.None);
        Assert.Equal(1, provider.Calls);
        Assert.Equal(TimeSpan.FromMinutes(5), job.Interval);
    }

    [Fact]
    public async Task OpenMeteoGeocodesThenReadsTheForecastInTheRightUnits()
    {
        var requests = new List<string>();
        using var http = new HttpClient(new StubHandler(request =>
        {
            requests.Add(request.RequestUri!.ToString());
            return request.RequestUri.Host.StartsWith("geocoding", StringComparison.Ordinal)
                ? """{"results":[{"name":"Paris","latitude":48.85341,"longitude":2.3488}]}"""
                : """{"current":{"temperature_2m":70.2,"apparent_temperature":68.0,"is_day":0,"weather_code":61,"wind_speed_10m":8.5}}""";
        }));
        var provider = new OpenMeteoProvider(http);

        var reading = await provider.GetAsync(new WeatherSettings { Location = "Paris, France", Units = "imperial" }, CancellationToken.None);
        await provider.GetAsync(new WeatherSettings { Location = "Paris, France", Units = "imperial" }, CancellationToken.None);

        Assert.Equal(("F", "mph", "Paris", false, 61), (reading!.Unit, reading.WindUnit, reading.City, reading.IsDay, reading.Code));
        Assert.Equal(3, requests.Count);
        Assert.Contains("name=Paris", requests[0], StringComparison.Ordinal);
        Assert.Contains("latitude=48.8534", requests[1], StringComparison.Ordinal);
        Assert.Contains("temperature_unit=fahrenheit", requests[1], StringComparison.Ordinal);
    }

    [Fact]
    public async Task CoordinatesNeedNoGeocodingAndNothingIsSentForAnEmptyLocation()
    {
        var requests = new List<string>();
        using var http = new HttpClient(new StubHandler(request =>
        {
            requests.Add(request.RequestUri!.Host);
            return """{"current":{"temperature_2m":5.0,"weather_code":3}}""";
        }));
        var provider = new OpenMeteoProvider(http);

        var reading = await provider.GetAsync(new WeatherSettings { Location = "48.85, 2.35", Units = "metric" }, CancellationToken.None);
        var none = await provider.GetAsync(new WeatherSettings { Location = " " }, CancellationToken.None);

        Assert.Equal("C", reading!.Unit);
        Assert.Null(none);
        Assert.Equal(["api.open-meteo.com"], requests);
        Assert.Null(OpenMeteoProvider.ParseCoordinates("91,0"));
        Assert.Null(OpenMeteoProvider.ParseCoordinates("Paris"));
    }

    [Fact]
    public async Task MusicShowsThePlayingTrackTruncatedAndHidesWhenPausedOrStale()
    {
        var (t, background) = Start(c => c.Music.Enabled = true);
        using var _ = t;
        var segment = new MusicSegment();
        background.Write<NowPlaying?>(MusicSegment.CacheKey, new NowPlaying("A Very Long Song Title That Keeps Going", "The Band", PlaybackState.Playing, "spotify"));

        var text = await Render(t, segment, new SegmentStyle { Type = "music", Options = { ["maxLength"] = "20" } });

        Assert.Equal("♪ The Band – A Very…", text);
        Assert.True(TextWidth.VisibleWidth(text!) <= 20);

        background.Write<NowPlaying?>(MusicSegment.CacheKey, new NowPlaying("Song", "Band", PlaybackState.Paused));
        Assert.Null(await Render(t, segment));
        t.Runtime.Config.Update(c => c.Music.HideWhenPaused = false);
        Assert.Equal("♪ Band – Song", await Render(t, segment));

        background.Clock = DateTimeOffset.UtcNow.AddMinutes(-5);
        background.Write<NowPlaying?>(MusicSegment.CacheKey, new NowPlaying("Song", "Band", PlaybackState.Playing));
        Assert.Null(await Render(t, segment));
    }

    [Fact]
    public async Task MusicFormatsWithoutAnArtistCleanly()
    {
        var (t, background) = Start(c => c.Music.Enabled = true);
        using var _ = t;
        background.Write<NowPlaying?>(MusicSegment.CacheKey, new NowPlaying("Podcast Episode", string.Empty, PlaybackState.Playing));

        Assert.Equal("♪ Podcast Episode", await Render(t, new MusicSegment()));
    }

    [Fact]
    public async Task TheMusicJobPollsOnlyWhenEnabledAndThrottlesItself()
    {
        var (t, background) = Start(c => c.Music.Enabled = true);
        using var _ = t;
        var source = new FakeMusic(new NowPlaying("Song", "Band", PlaybackState.Playing));
        var now = DateTimeOffset.UtcNow;
        var job = MusicJob.Create(t.Runtime, background, source, () => now);

        await job.Run(CancellationToken.None);
        await job.Run(CancellationToken.None);
        Assert.Equal(1, source.Calls);
        Assert.Equal("Song", background.Read<NowPlaying?>(MusicSegment.CacheKey)!.Value!.Title);

        now = now.AddSeconds(20);
        background.Clock = now.AddSeconds(-20);
        await job.Run(CancellationToken.None);
        Assert.Equal(2, source.Calls);

        t.Runtime.Config.Update(c => c.Music.Enabled = false);
        now = now.AddMinutes(5);
        await job.Run(CancellationToken.None);
        Assert.Equal(2, source.Calls);
    }

    [Fact]
    public async Task PlayerctlParsesPlayersAndPrefersTheOneThatIsPlaying()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        var runner = new FakeProgramRunner().On("playerctl", "--all-players", "Paused\tvlc\tAnna\tOld song\nPlaying\tspotify\tBand\tNew song\n");
        var source = new PlayerctlMusicSource(runner);

        var playing = await source.GetAsync("auto");

        Assert.Equal(("New song", "Band", PlaybackState.Playing, "spotify"), (playing!.Title, playing.Artist, playing.State, playing.Player));
        Assert.Null(await source.GetAsync("bad;name"));
        Assert.Equal(["--all-players metadata --format {{status}}\t{{playerName}}\t{{artist}}\t{{title}}"], runner.CommandLines("playerctl"));
    }

    [Fact]
    public void OsascriptOutputIsParsed()
    {
        Assert.Equal(("Song", "Band", PlaybackState.Playing), (OsascriptMusicSource.Parse("playing\tBand\tSong\n", "spotify")!.Title, OsascriptMusicSource.Parse("playing\tBand\tSong\n", "spotify")!.Artist, OsascriptMusicSource.Parse("playing\tBand\tSong\n", "spotify")!.State));
        Assert.Null(OsascriptMusicSource.Parse(string.Empty, "music"));
    }

    [Theory]
    [InlineData(80, true, 95, "80%↑")]
    [InlineData(80, false, 95, "80%")]
    [InlineData(99, false, 100, "99%")]
    [InlineData(99, false, 95, null)]
    public void BatteryUsesTheConfiguredThresholdAndMarker(int percent, bool charging, int hideAbove, string? expected)
    {
        var battery = new BatteryStatus(percent, charging, OnAcPower: !charging);

        Assert.Equal(expected, BatterySegment.Format(battery, hideAbove));
        Assert.Equal(percent + "%⚡", charging ? BatterySegment.Format(battery, hideAbove, "⚡") : percent + "%⚡");
    }

    [Fact]
    public async Task BatteryDefaultsComeFromConfigAndThemeOptionsWin()
    {
        var (t, _) = Start(c => (c.Battery.Low, c.Battery.ChargingMarker) = (50, "+"));
        using var _ = t;
        var environment = new SegmentEnvironment
        {
            Home = () => null,
            FindRepositoryRoot = _ => null,
            GetEnvironmentVariable = _ => null,
            GetGitStatus = (_, _) => Task.FromResult<GitStatus?>(null),
            IsNodeProject = _ => false,
            GetNodeVersion = _ => Task.FromResult<string?>(null),
            GetKubeContext = () => null,
            DurationThresholdMs = () => 2000,
            GetBattery = () => new BatteryStatus(40, true, true),
        };
        var segment = new BatterySegment(environment);

        Assert.Equal("40%+", await Render(t, segment));
        Assert.Equal("40%*", await Render(t, segment, new SegmentStyle { Type = "battery", Options = { ["chargingMarker"] = "*" } }));
    }

    private sealed class StubHandler(Func<HttpRequestMessage, string> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(respond(request)) });
    }
}
