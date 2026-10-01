using Pickle.Abstractions;
using Pickle.Abstractions.Services;
using Pickle.Core.Contracts;

namespace Pickle.Core.Prompt.Segments.Live;

/// <summary>Registers the background jobs behind the weather and music segments (they only do anything once enabled in settings).</summary>
internal sealed class SegmentDataComponent(PickleRuntime runtime) : IRuntimeComponent
{
    private static readonly HttpClient Http = CreateClient();

    public void Initialize()
    {
        runtime.CommandRegistry.Register(new WeatherCommand());
        runtime.CommandRegistry.Register(new MusicCommand());
        if (!OperatingSystem.IsWindows() && runtime.Services.Get<IMusicSource>() is null)
        {
            runtime.Services.Add(DefaultMusicSource());
        }
    }

    public void OnStarted()
    {
        if (runtime.Services.Get<IBackgroundWork>() is not { } background)
        {
            return;
        }

        background.Register(WeatherJob.Create(runtime, background, new OpenMeteoProvider(Http)));
        background.Register(MusicJob.Create(runtime, background, runtime.Services.Get<IMusicSource>() ?? DefaultMusicSource(), () => DateTimeOffset.UtcNow));
    }

    private IMusicSource DefaultMusicSource()
    {
        var runner = runtime.Services.Require<IProgramRunner>();
        return OperatingSystem.IsMacOS() ? new OsascriptMusicSource(runner) : new PlayerctlMusicSource(runner);
    }

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("Pickle/" + PickleRuntime.Version);
        return client;
    }
}
