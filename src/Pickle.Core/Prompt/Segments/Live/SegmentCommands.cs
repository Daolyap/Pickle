using Pickle.Abstractions;
using Pickle.Abstractions.Services;

namespace Pickle.Core.Prompt.Segments.Live;

/// <summary><c>pk weather</c>: turn the weather segment on for a place, off, or look at what it has.</summary>
internal sealed class WeatherCommand : PickleCommandBase
{
    public override string Name => "weather";

    public override string Description => "Set up the weather prompt segment (Open-Meteo, no account needed)";

    public override string Usage => "pk weather [now | set <place|lat,lon|auto> [--units metric|imperial] | off]";

    public override IReadOnlyList<string> Examples =>
    [
        "pk weather set Paris            enable it for Paris (sends 'Paris' to open-meteo.com)",
        "pk weather set 48.85,2.35       coordinates instead of a place name",
        "pk weather now                  the cached reading",
    ];

    protected override async Task<int> RunAsync(CommandOutput output, IReadOnlyList<string> raw, CancellationToken cancellationToken)
    {
        var args = CommandArgs.Parse(raw, "units");
        var config = output.Pickle.Config;
        switch (args.Arg(0)?.ToLowerInvariant())
        {
            case "set" when args.Positional.Count >= 2:
                var place = args.Rest(1);
                var units = args.Value("units");
                if (units is not (null or "auto" or "metric" or "imperial"))
                {
                    return UsageError(output, "--units is auto, metric or imperial.");
                }

                config.Update(c =>
                {
                    c.Weather.Enabled = true;
                    c.Weather.Location = place;
                    if (units is not null)
                    {
                        c.Weather.Units = units;
                    }
                });
                output.Success($"Weather is on for '{place}'. Add {{ \"type\": \"weather\" }} to your theme's segments to show it (the first reading arrives within a minute).");
                output.Muted("The place name (or, for 'auto', your IP address) is sent to open-meteo.com / ipwho.is. 'pk weather off' stops that.");
                return 0;
            case "off":
                config.Update(c => c.Weather.Enabled = false);
                output.Success("Weather is off.");
                return 0;
            case null or "now" or "status":
                var settings = config.Current.Weather;
                output.Line(settings.Enabled ? $"On for '{settings.Location}' (every {settings.RefreshMinutes} min, {settings.Units})." : "Off. Turn it on with: pk weather set <place>");
                if (output.Pickle.Services.Get<IBackgroundWork>()?.Read<WeatherReading>(WeatherSegment.CacheKey) is { } cached)
                {
                    output.Line($"{WeatherFormat.Render(cached.Value, "{icon} {temp}{unit} {condition}, feels {feels}°, wind {wind} {city}")} · {Display(cached.At)}");
                }
                else if (settings.Enabled)
                {
                    output.Muted("No reading yet; the background job fetches one shortly after Pickle starts (only the first running Pickle fetches).");
                }

                return 0;
            default:
                return await Task.FromResult(UsageError(output)).ConfigureAwait(false);
        }
    }

    private static string Display(DateTimeOffset at) => "updated " + at.ToLocalTime().ToString("HH:mm", System.Globalization.CultureInfo.InvariantCulture);
}

/// <summary><c>pk music</c>: turn the music segment on or off, or show what is playing.</summary>
internal sealed class MusicCommand : PickleCommandBase
{
    public override string Name => "music";

    public override string Description => "Set up the music prompt segment (what is playing)";

    public override string Usage => "pk music [now | on [--player name] | off]";

    protected override async Task<int> RunAsync(CommandOutput output, IReadOnlyList<string> raw, CancellationToken cancellationToken)
    {
        var args = CommandArgs.Parse(raw, "player");
        var config = output.Pickle.Config;
        var source = output.Pickle.Services.Get<IMusicSource>();
        switch (args.Arg(0)?.ToLowerInvariant())
        {
            case "on":
                config.Update(c =>
                {
                    c.Music.Enabled = true;
                    if (args.Value("player") is { } player)
                    {
                        c.Music.Player = player;
                    }
                });
                output.Success("The music segment is on. Add { \"type\": \"music\" } to your theme's segments to show it.");
                return 0;
            case "off":
                config.Update(c => c.Music.Enabled = false);
                output.Success("The music segment is off.");
                return 0;
            case null or "now" or "status":
                var settings = config.Current.Music;
                output.Line(settings.Enabled ? $"On (player: {settings.Player}, every {Math.Max(15, settings.PollSeconds)} s)." : "Off. Turn it on with: pk music on");
                if (source is { IsSupported: true } && await source.GetAsync(settings.Player, cancellationToken).ConfigureAwait(false) is { } playing)
                {
                    output.Object(playing);
                }
                else
                {
                    output.Muted("Nothing is playing" + (source is { IsSupported: true } ? "." : ", or this machine has no way to ask (Linux needs playerctl)."));
                }

                return 0;
            default:
                return UsageError(output);
        }
    }
}
