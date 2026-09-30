using Pickle.Abstractions;
using Pickle.Abstractions.Services;
using Pickle.Core.Hosting;
using Pickle.Core.Update;
using Pickle.Testing;

namespace Pickle.Core.Tests.Hosting;

public class StartupNoticesTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-09-30T08:00:00Z", System.Globalization.CultureInfo.InvariantCulture);

    [Fact]
    public void NothingCachedMeansNoNotices()
    {
        using var t = TestPickle.Create();
        Assert.Empty(StartupNotices.Lines(t.Runtime, Now));
    }

    [Fact]
    public void FreshWingetUpgradesAreCountedWithTheKeyThatOpensThePanel()
    {
        using var t = TestPickle.Create(start: true);
        t.Runtime.Background.Clock = () => Now - TimeSpan.FromHours(3);
        t.Runtime.Background.Write(WingetCache.UpgradesKey, new List<WingetPackage>
        {
            new("Git.Git", "Git", "2.45.1", "2.46.0", "winget"),
            new("7zip.7zip", "7-Zip", "23.01", "24.08", "winget"),
        });

        Assert.Equal(["2 app upgrades available · Alt+W"], StartupNotices.Lines(t.Runtime, Now));
        Assert.Empty(StartupNotices.Lines(t.Runtime, Now + TimeSpan.FromDays(3)));
    }

    [Fact]
    public void AnOlderOrSameReleaseIsNotAnnouncedAndTheSettingTurnsItOff()
    {
        using var t = TestPickle.Create();
        t.Runtime.Background.Write(UpdateCheck.Key, new UpdateCheck.LatestRelease(PickleRuntime.Version, "https://example.test"));
        Assert.Empty(StartupNotices.Lines(t.Runtime, Now));

        t.Runtime.Background.Write(UpdateCheck.Key, new UpdateCheck.LatestRelease("99.1.0", "https://example.test"));
        Assert.Single(StartupNotices.Lines(t.Runtime, Now));

        using var off = TestPickle.Create(configure: c => c.Shell.CheckForUpdates = false);
        off.Runtime.Background.Write(UpdateCheck.Key, new UpdateCheck.LatestRelease("99.1.0", "https://example.test"));
        Assert.Empty(StartupNotices.Lines(off.Runtime, Now));
    }
}
