namespace Pickle.Abstractions.Services;

public enum PlaybackState
{
    Stopped,
    Paused,
    Playing,
}

/// <summary>What a media player is playing right now.</summary>
public sealed record NowPlaying(string Title, string Artist, PlaybackState State, string? Player = null);

/// <summary>Reads the current track from the platform's media sessions (Windows SMTC, MPRIS on Linux, Music/Spotify on macOS).</summary>
public interface IMusicSource
{
    bool IsSupported { get; }

    /// <summary>The track of <paramref name="player"/> ("auto": whichever is playing), or null when nothing is open.</summary>
    Task<NowPlaying?> GetAsync(string player, CancellationToken cancellationToken = default);
}
