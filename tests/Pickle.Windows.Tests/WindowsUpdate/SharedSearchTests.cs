using Pickle.Abstractions.Services;
using Pickle.Windows.WindowsUpdate;

namespace Pickle.Windows.Tests.WindowsUpdate;

public class SharedSearchTests
{
    private static readonly TimeSpan Long = TimeSpan.FromSeconds(30);

    [Fact]
    public async Task AGivenUpSearchIsJoinedInsteadOfStartedAgain()
    {
        var searches = new SharedSearch<string>();
        var release = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        Action<WindowsUpdateProgress>? report = null;
        var starts = 0;
        Task<string> Start(Action<WindowsUpdateProgress> r)
        {
            Interlocked.Increment(ref starts);
            report = r;
            return release.Task;
        }

        using var ctrlC = new CancellationTokenSource();
        var first = searches.RunAsync("IsInstalled=0", Start, null, Long, ctrlC.Token);
        await ctrlC.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first);
        Assert.Equal(1, searches.RunningCount);

        var seen = new List<string>();
        var second = searches.RunAsync("IsInstalled=0", Start, new SyncProgress<WindowsUpdateProgress>(p => seen.Add(p.Stage)), Long, CancellationToken.None);
        await Task.Run(() => report!(new WindowsUpdateProgress("Reading", "update 1 of 1", 0)));
        release.SetResult("found");

        Assert.Equal("found", await second.WaitAsync(Long));
        Assert.Equal(1, starts);
        Assert.Equal(["Waiting", "Reading"], seen);

        await WaitUntilAsync(() => searches.RunningCount == 0);
        release = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        release.SetResult("again");
        Assert.Equal("again", await searches.RunAsync("IsInstalled=0", Start, null, Long, CancellationToken.None));
        Assert.Equal(2, starts);
    }

    [Fact]
    public async Task DifferentCriteriaRunSeparatelyAndTimeoutsStopWaiting()
    {
        var searches = new SharedSearch<string>();
        var never = new TaskCompletionSource<string>();
        var starts = 0;

        var slow = searches.RunAsync("a", _ =>
        {
            Interlocked.Increment(ref starts);
            return never.Task;
        }, null, TimeSpan.FromMilliseconds(100), CancellationToken.None);
        var quick = searches.RunAsync("b", _ =>
        {
            Interlocked.Increment(ref starts);
            return Task.FromResult("b");
        }, null, Long, CancellationToken.None);

        Assert.Equal("b", await quick);
        await Assert.ThrowsAsync<TimeoutException>(() => slow);
        Assert.Equal(2, starts);
    }

    [Fact]
    public async Task AStartThatThrowsFailsTheCallerAndIsForgotten()
    {
        var searches = new SharedSearch<string>();
        await Assert.ThrowsAsync<InvalidOperationException>(() => searches.RunAsync("a", _ => throw new InvalidOperationException("no agent"), null, Long, CancellationToken.None));
        await WaitUntilAsync(() => searches.RunningCount == 0);
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        for (var i = 0; i < 200 && !condition(); i++)
        {
            await Task.Delay(10);
        }

        Assert.True(condition());
    }
}
