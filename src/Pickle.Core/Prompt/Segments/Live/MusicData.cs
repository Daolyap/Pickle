using System.Text.RegularExpressions;
using Pickle.Abstractions;
using Pickle.Abstractions.Services;

namespace Pickle.Core.Prompt.Segments.Live;

/// <summary>Linux: MPRIS players through <c>playerctl</c>.</summary>
public sealed partial class PlayerctlMusicSource(IProgramRunner runner) : IMusicSource
{
    public bool IsSupported => OperatingSystem.IsLinux() && runner.Find("playerctl") is not null;

    public async Task<NowPlaying?> GetAsync(string player, CancellationToken cancellationToken = default)
    {
        var arguments = new List<string>();
        if (!player.Equals("auto", StringComparison.OrdinalIgnoreCase))
        {
            if (!PlayerName().IsMatch(player))
            {
                return null;
            }

            arguments.Add("--player=" + player);
        }
        else
        {
            arguments.Add("--all-players");
        }

        arguments.AddRange(["metadata", "--format", "{{status}}\t{{playerName}}\t{{artist}}\t{{title}}"]);
        var result = await runner.RunAsync("playerctl", arguments, new ProgramRunOptions { Timeout = TimeSpan.FromSeconds(3) }, cancellationToken).ConfigureAwait(false);
        return result.Success ? Pick(Parse(result.StdOut)) : null;
    }

    public static IReadOnlyList<NowPlaying> Parse(string output) =>
        [.. output.Replace("\r", string.Empty, StringComparison.Ordinal).Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(l => l.Split('\t'))
            .Where(p => p.Length >= 4 && p[3].Length > 0)
            .Select(p => new NowPlaying(p[3], p[2], State(p[0]), p[1]))];

    /// <summary>The first player that is playing, else the first paused one.</summary>
    public static NowPlaying? Pick(IReadOnlyList<NowPlaying> players) =>
        players.FirstOrDefault(p => p.State == PlaybackState.Playing) ?? players.FirstOrDefault();

    internal static PlaybackState State(string text) => text.Trim().ToLowerInvariant() switch
    {
        "playing" => PlaybackState.Playing,
        "paused" => PlaybackState.Paused,
        _ => PlaybackState.Stopped,
    };

    [GeneratedRegex(@"^[A-Za-z0-9._-]{1,64}$")]
    private static partial Regex PlayerName();
}

/// <summary>macOS: Spotify and Music through <c>osascript</c> (fixed scripts, nothing from the user is put into them).</summary>
public sealed class OsascriptMusicSource(IProgramRunner runner) : IMusicSource
{
    private static readonly Dictionary<string, string> Scripts = new(StringComparer.OrdinalIgnoreCase)
    {
        ["spotify"] = "if application \"Spotify\" is running then tell application \"Spotify\" to return (player state as text) & \"\\t\" & (artist of current track) & \"\\t\" & (name of current track)",
        ["music"] = "if application \"Music\" is running then tell application \"Music\" to return (player state as text) & \"\\t\" & (artist of current track) & \"\\t\" & (name of current track)",
    };

    public bool IsSupported => OperatingSystem.IsMacOS() && runner.Find("osascript") is not null;

    public async Task<NowPlaying?> GetAsync(string player, CancellationToken cancellationToken = default)
    {
        var names = player.Equals("auto", StringComparison.OrdinalIgnoreCase) ? ["spotify", "music"] : Scripts.ContainsKey(player) ? new[] { player.ToLowerInvariant() } : [];
        NowPlaying? fallback = null;
        foreach (var name in names)
        {
            var result = await runner.RunAsync("osascript", ["-e", Scripts[name]], new ProgramRunOptions { Timeout = TimeSpan.FromSeconds(3) }, cancellationToken).ConfigureAwait(false);
            if (!result.Success || Parse(result.StdOut, name) is not { } playing)
            {
                continue;
            }

            if (playing.State == PlaybackState.Playing)
            {
                return playing;
            }

            fallback ??= playing;
        }

        return fallback;
    }

    public static NowPlaying? Parse(string output, string player)
    {
        var parts = output.Trim().Split('\t');
        return parts.Length >= 3 && parts[2].Length > 0
            ? new NowPlaying(parts[2], parts[1], PlayerctlMusicSource.State(parts[0]), player)
            : null;
    }
}
