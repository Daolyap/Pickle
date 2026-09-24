using System.Diagnostics;
using Pickle.Abstractions;
using Pickle.Core.History;
using Pickle.Testing;

namespace Pickle.Core.Tests.History;

public class HistoryAutosuggestTests
{
    private static readonly DateTimeOffset T0 = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void SuggestsTheMostRecentPrefixMatch()
    {
        var index = Index(
            Entry("git status", minutes: 1),
            Entry("git stash", minutes: 2),
            Entry("git log", minutes: 3));
        Assert.Equal("git log", index.Suggest("git", cwd: null));
        Assert.Equal("git stash", index.Suggest("git st", cwd: null));
        Assert.Null(index.Suggest("svn", cwd: null));
    }

    [Fact]
    public void PrefersCommandsRunInTheCurrentDirectory()
    {
        var index = Index(
            Entry("npm run build", minutes: 1, cwd: "/src/app"),
            Entry("npm run test", minutes: 2, cwd: "/src/lib"));
        Assert.Equal("npm run build", index.Suggest("npm", "/src/app"));
        Assert.Equal("npm run test", index.Suggest("npm", "/src/lib"));
        Assert.Equal("npm run test", index.Suggest("npm", "/elsewhere"));
    }

    [Fact]
    public void RemembersEveryDirectoryACommandRanIn()
    {
        var index = Index(
            Entry("make all", minutes: 1, cwd: "/a"),
            Entry("make all", minutes: 2, cwd: "/b"),
            Entry("make clean", minutes: 3, cwd: "/c"));
        Assert.Equal("make all", index.Suggest("make", "/a"));
    }

    [Fact]
    public void PrefersSuccessfulCommands()
    {
        var index = Index(
            Entry("dotnet test", minutes: 1, success: true),
            Entry("dotnet tset", minutes: 2, success: false));
        Assert.Equal("dotnet test", index.Suggest("dotnet t", cwd: null));
        Assert.Equal("dotnet tset", index.Suggest("dotnet ts", cwd: null));
    }

    [Fact]
    public void FallsBackToCaseInsensitiveAndKeepsWhatWasTyped()
    {
        var index = Index(Entry("Get-ChildItem -Force", minutes: 1));
        Assert.Equal("get-childItem -Force", index.Suggest("get-child", cwd: null));
        Assert.Equal("Get-ChildItem -Force", index.Suggest("Get-Child", cwd: null));
    }

    [Fact]
    public void CaseSensitiveMatchesWinOverNewerCaseInsensitiveOnes()
    {
        var index = Index(
            Entry("ls -la", minutes: 1),
            Entry("LS -Recurse", minutes: 2));
        Assert.Equal("ls -la", index.Suggest("ls", cwd: null));
    }

    [Fact]
    public void SkipsExactMatches()
    {
        var index = Index(Entry("ls", minutes: 1), Entry("LS", minutes: 2));
        Assert.Null(index.Suggest("ls", cwd: null));
    }

    [Fact]
    public void ReaddingACommandMovesItToTheFront()
    {
        var index = Index(
            Entry("cd src", minutes: 1),
            Entry("cd docs", minutes: 2),
            Entry("cd src", minutes: 3));
        Assert.Equal("cd src", index.Suggest("cd ", cwd: null));
        Assert.Equal(2, index.Count);
    }

    [Fact]
    public void ProviderUsesTheLiveHistoryStore()
    {
        using var t = TestPickle.Create();
        var history = t.Runtime.History;
        history.Add(new HistoryEntry("Write-Output one", DateTimeOffset.Now, "/x"));
        history.CompleteLast(true, 1);
        Assert.Equal("Write-Output one", t.Runtime.Autosuggest.Suggest("Write", "/x"));

        history.Add(new HistoryEntry("Write-Output two", DateTimeOffset.Now, "/x"));
        Assert.Equal("Write-Output two", t.Runtime.Autosuggest.Suggest("Write", "/x"));
        Assert.Null(t.Runtime.Autosuggest.Suggest(string.Empty, "/x"));
    }

    [Fact]
    public void ProviderRespectsTheAutosuggestionsSetting()
    {
        using var t = TestPickle.Create(configure: c => c.Editor.Autosuggestions = false);
        t.Runtime.History.Add(new HistoryEntry("Write-Output one", DateTimeOffset.Now));
        Assert.Null(t.Runtime.Autosuggest.Suggest("Write", "/x"));
    }

    [Fact]
    public void SuggestsQuicklyOnFiftyThousandEntries()
    {
        var index = new HistoryIndex();
        for (var i = 0; i < 50_000; i++)
        {
            index.Add(new HistoryEntry($"command-{i % 20_000} --flag value{i}", T0.AddSeconds(i), $"/dir/{i % 50}", i % 7 != 0));
        }

        index.Suggest("zzz", "/dir/1");
        var best = long.MaxValue;
        // Best of many: the suite runs in parallel, so any single run can lose the CPU.
        for (var run = 0; run < 20; run++)
        {
            var sw = Stopwatch.StartNew();
            Assert.Null(index.Suggest("no-such-prefix", "/dir/3"));
            Assert.NotNull(index.Suggest("Command-1", "/dir/3"));
            sw.Stop();
            best = Math.Min(best, sw.ElapsedTicks);
        }

        Assert.True(TimeSpan.FromTicks(best * TimeSpan.TicksPerSecond / Stopwatch.Frequency).TotalMilliseconds < 5, $"best run took {best * 1000.0 / Stopwatch.Frequency:F2} ms");
    }

    private static HistoryIndex Index(params HistoryEntry[] entries)
    {
        var index = new HistoryIndex();
        foreach (var entry in entries)
        {
            index.Add(entry);
        }

        return index;
    }

    private static HistoryEntry Entry(string command, int minutes, string? cwd = null, bool? success = true) =>
        new(command, T0.AddMinutes(minutes), cwd, success);
}
