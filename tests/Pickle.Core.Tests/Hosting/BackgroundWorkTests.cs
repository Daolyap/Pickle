using Pickle.Abstractions.Services;
using Pickle.Core.Hosting;
using Pickle.Testing;

namespace Pickle.Core.Tests.Hosting;

public class BackgroundWorkTests
{
    [Fact]
    public void OnlyOneInstanceIsPrimaryAndAnotherTakesOverWhenItExits()
    {
        using var t = TestPickle.Create();
        var first = new BackgroundWork(t.Runtime);
        using var second = new BackgroundWork(t.Runtime);

        Assert.True(first.TryBecomePrimary());
        Assert.False(second.TryBecomePrimary());

        first.Dispose();
        Assert.True(second.TryBecomePrimary());
        Assert.True(second.IsPrimary);
    }

    [Fact]
    public async Task JobsRunOnTheirIntervalAndOnlyInThePrimary()
    {
        using var t = TestPickle.Create();
        var now = DateTimeOffset.Parse("2026-09-30T08:00:00Z", System.Globalization.CultureInfo.InvariantCulture);
        using var primary = new BackgroundWork(t.Runtime) { Clock = () => now };
        using var other = new BackgroundWork(t.Runtime) { Clock = () => now };
        var runs = 0;
        var job = new BackgroundJob("probe", _ => { runs++; return Task.CompletedTask; }) { Interval = TimeSpan.FromHours(1), InitialDelay = TimeSpan.Zero };
        primary.Register(job);
        other.Register(job);

        await primary.RunOnceAsync(CancellationToken.None);
        await other.RunOnceAsync(CancellationToken.None);
        Assert.Equal(1, runs);

        now += TimeSpan.FromMinutes(30);
        await primary.RunOnceAsync(CancellationToken.None);
        Assert.Equal(1, runs);

        now += TimeSpan.FromMinutes(31);
        await primary.RunOnceAsync(CancellationToken.None);
        Assert.Equal(2, runs);
    }

    [Fact]
    public async Task ARunOnceJobIsSkippedWhenAnotherInstanceJustRanIt()
    {
        using var t = TestPickle.Create();
        var now = DateTimeOffset.Parse("2026-09-30T08:00:00Z", System.Globalization.CultureInfo.InvariantCulture);
        var runs = 0;
        BackgroundJob Job() => new("sync", _ => { runs++; return Task.CompletedTask; }) { InitialDelay = TimeSpan.Zero, MinimumGap = TimeSpan.FromMinutes(10) };

        using (var earlier = new BackgroundWork(t.Runtime) { Clock = () => now })
        {
            earlier.Register(Job());
            await earlier.RunOnceAsync(CancellationToken.None);
            await earlier.RunOnceAsync(CancellationToken.None);
        }

        Assert.Equal(1, runs);

        now += TimeSpan.FromMinutes(5);
        using (var next = new BackgroundWork(t.Runtime) { Clock = () => now })
        {
            next.Register(Job());
            await next.RunOnceAsync(CancellationToken.None);
            now += TimeSpan.FromMinutes(20);
            await next.RunOnceAsync(CancellationToken.None);
        }

        Assert.Equal(1, runs);

        using var later = new BackgroundWork(t.Runtime) { Clock = () => now };
        later.Register(Job());
        await later.RunOnceAsync(CancellationToken.None);
        Assert.Equal(2, runs);
    }

    [Fact]
    public async Task AFailingJobIsLoggedAndWaitsItsInterval()
    {
        using var t = TestPickle.Create();
        using var work = new BackgroundWork(t.Runtime);
        var runs = 0;
        work.Register(new BackgroundJob("broken", _ => { runs++; throw new InvalidOperationException("boom"); }) { Interval = TimeSpan.FromHours(1), InitialDelay = TimeSpan.Zero });

        await work.RunOnceAsync(CancellationToken.None);
        await work.RunOnceAsync(CancellationToken.None);

        Assert.Equal(1, runs);
    }

    [Fact]
    public void CachedValuesAreSharedBetweenInstances()
    {
        using var t = TestPickle.Create();
        using var writer = new BackgroundWork(t.Runtime);
        using var reader = new BackgroundWork(t.Runtime);

        Assert.Null(reader.Read<List<string>>("winget-upgrades"));
        writer.Write("winget-upgrades", new List<string> { "Git.Git", "7zip.7zip" });

        var cached = reader.Read<List<string>>("winget-upgrades");
        Assert.NotNull(cached);
        Assert.Equal(["Git.Git", "7zip.7zip"], cached.Value);
        Assert.True(DateTimeOffset.UtcNow - cached.At < TimeSpan.FromMinutes(1));
        Assert.Throws<ArgumentException>(() => writer.Write("../escape", 1));
        Assert.Same(t.Runtime.Background, t.Runtime.Services.Get<IBackgroundWork>());
    }
}
