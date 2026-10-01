using Pickle.Abstractions.Services;
using Pickle.Windows.Music;

namespace Pickle.Windows.Tests.Music;

public class WindowsMusicSourceTests
{
    private const string Sessions = """[{"Title":"Old podcast","Artist":"Host","State":"Paused","App":"chrome.exe"},{"Title":"Song","Artist":"Band","State":"Playing","App":"Spotify.exe"},{"Title":"","Artist":"","State":"Stopped","App":"x"}]""";

    [Fact]
    public void ParsesSessionsAndDropsEmptyOnes()
    {
        var sessions = WindowsMusicSource.Parse(Sessions);

        Assert.Equal(["Old podcast", "Song"], sessions.Select(s => s.Title));
        Assert.Equal([PlaybackState.Paused, PlaybackState.Playing], sessions.Select(s => s.State));
    }

    [Theory]
    [InlineData("auto", "Song")]
    [InlineData("chrome", "Old podcast")]
    [InlineData("spotify", "Song")]
    public void PicksThePlayingSessionOrTheNamedApp(string player, string title) =>
        Assert.Equal(title, WindowsMusicSource.Pick(WindowsMusicSource.Parse(Sessions), player)!.Title);

    [Fact]
    public void NothingOpenGivesNullAndBadJsonIsIgnored()
    {
        Assert.Null(WindowsMusicSource.Pick(WindowsMusicSource.Parse("[]"), "auto"));
        Assert.Empty(WindowsMusicSource.Parse("not json"));
        Assert.Empty(WindowsMusicSource.Parse(string.Empty));
        Assert.Single(WindowsMusicSource.Parse("""{"Title":"One","Artist":"A","State":"Playing","App":"x"}"""));
    }
}
