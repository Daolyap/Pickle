using Pickle.Abstractions.Services;
using Pickle.Core.Git;

namespace Pickle.Core.Tests.Git;

public class GitParserTests
{
    private const string Oid = "1234567890abcdef1234567890abcdef12345678";

    [Fact]
    public void StatusParsesBranchHeadersAndEveryEntryKind()
    {
        var output = string.Join('\0',
            "# branch.oid " + Oid,
            "# branch.head feature/x",
            "# branch.upstream origin/feature/x",
            "# branch.ab +2 -1",
            "# stash 3",
            "1 .M N... 100644 100644 100644 aaaa bbbb src/file one.cs",
            "1 A. N... 000000 100644 100644 0000 cccc added.txt",
            "1 D. N... 100644 000000 000000 dddd 0000 gone.txt",
            "1 MT N... 100644 100644 120000 aaaa bbbb both.txt",
            "2 R. N... 100644 100644 100644 eeee eeee R100 new name.txt",
            "old name.txt",
            "u UU N... 100644 100644 100644 100644 h1 h2 h3 conflict.txt",
            "? untracked dir/",
            "! ignored.log",
            string.Empty);

        var status = GitStatusParser.Parse(output, "/repo");

        Assert.Equal("/repo", status.Root);
        Assert.Equal("feature/x", status.Branch);
        Assert.Equal("origin/feature/x", status.Upstream);
        Assert.Equal(2, status.Ahead);
        Assert.Equal(1, status.Behind);
        Assert.False(status.IsDetached);
        Assert.Equal(Oid, status.HeadSha);
        Assert.Equal(3, status.StashCount);

        Assert.Equal(
        [
            new GitStatusEntry("src/file one.cs", null, GitChangeKind.None, GitChangeKind.Modified),
            new GitStatusEntry("added.txt", null, GitChangeKind.Added, GitChangeKind.None),
            new GitStatusEntry("gone.txt", null, GitChangeKind.Deleted, GitChangeKind.None),
            new GitStatusEntry("both.txt", null, GitChangeKind.Modified, GitChangeKind.TypeChanged),
            new GitStatusEntry("new name.txt", "old name.txt", GitChangeKind.Renamed, GitChangeKind.None),
            new GitStatusEntry("conflict.txt", null, GitChangeKind.None, GitChangeKind.Unmerged),
            new GitStatusEntry("untracked dir/", null, GitChangeKind.Untracked, GitChangeKind.Untracked),
            new GitStatusEntry("ignored.log", null, GitChangeKind.Ignored, GitChangeKind.Ignored),
        ],
        status.Entries);

        Assert.Equal(4, status.StagedCount);
        Assert.Equal(1, status.UntrackedCount);
        Assert.Equal(1, status.ConflictCount);
    }

    [Fact]
    public void StatusParsesDetachedAndInitialHeads()
    {
        var detached = GitStatusParser.Parse("# branch.oid " + Oid + "\0# branch.head (detached)\0", "/r");
        Assert.True(detached.IsDetached);
        Assert.Null(detached.Branch);
        Assert.Equal(Oid, detached.HeadSha);
        Assert.True(detached.IsClean);

        var initial = GitStatusParser.Parse("# branch.oid (initial)\0# branch.head main\0? a.txt\0", "/r");
        Assert.False(initial.IsDetached);
        Assert.Equal("main", initial.Branch);
        Assert.Null(initial.HeadSha);
        Assert.Null(initial.Upstream);
        Assert.Equal(0, initial.Ahead);
        Assert.Single(initial.Entries);
    }

    [Fact]
    public void StatusToleratesGarbage()
    {
        var status = GitStatusParser.Parse("x\0# branch.ab nonsense\01 M\0\0", "/r");
        Assert.Empty(status.Entries);
        Assert.Equal(0, status.Ahead);
    }

    [Fact]
    public void LogParsesGraphPrefixesAndSkipsConnectorLines()
    {
        const char f = '\x1f';
        const char r = '\x1e';
        var output = string.Join('\n',
            $"*   {f}aaaa1111{f}aaaa{f}Ann{f}2024-05-01T10:00:00+02:00{f}Merge branch 'x'{f}HEAD -> main, origin/main, tag: v1{r}",
            "|\\  ",
            $"| * {f}bbbb2222{f}bbbb{f}Bob{f}2024-04-30T09:00:00Z{f}Feature{f}{r}",
            $"* | {f}cccc3333{f}cccc{f}Ann{f}2024-04-29T08:00:00+00:00{f}Main work{f}{r}",
            "|/  ",
            $"* {f}dddd4444{f}dddd{f}Ann{f}2024-04-28T08:00:00+00:00{f}Root: first{f}{r}",
            string.Empty);

        var commits = GitOutputParsers.ParseLog(output);

        Assert.Equal(4, commits.Count);
        Assert.Equal("*", commits[0].Graph);
        Assert.Equal("| *", commits[1].Graph);
        Assert.Equal("* |", commits[2].Graph);
        Assert.Equal("aaaa1111", commits[0].Sha);
        Assert.Equal("aaaa", commits[0].ShortSha);
        Assert.Equal("Ann", commits[0].Author);
        Assert.Equal(new DateTimeOffset(2024, 5, 1, 10, 0, 0, TimeSpan.FromHours(2)), commits[0].Date);
        Assert.Equal("Merge branch 'x'", commits[0].Subject);
        Assert.Equal(["HEAD -> main", "origin/main", "tag: v1"], commits[0].Refs);
        Assert.Empty(commits[1].Refs);
        Assert.Equal("Root: first", commits[3].Subject);
    }

    [Fact]
    public void LogWithoutGraphHasEmptyGraph()
    {
        const char f = '\x1f';
        var commits = GitOutputParsers.ParseLog($"{f}sha{f}s{f}A{f}2024-01-01T00:00:00Z{f}subject{f}\x1e\n");
        Assert.Equal(string.Empty, Assert.Single(commits).Graph);
    }

    [Fact]
    public void BranchesParseLocalRemoteCurrentAndSkipSymrefs()
    {
        const char f = '\x1f';
        var output = string.Join('\n',
            $"refs/heads/main{f}*{f}origin/main{f}2024-05-01T10:00:00+02:00{f}{f}Initial commit",
            $"refs/heads/feature/a{f} {f}{f}2024-05-02T10:00:00Z{f}{f}",
            $"refs/remotes/origin/HEAD{f} {f}{f}2024-05-01T10:00:00Z{f}refs/remotes/origin/main{f}Initial commit",
            $"refs/remotes/origin/main{f} {f}{f}2024-05-01T10:00:00Z{f}{f}Initial commit",
            string.Empty);

        var branches = GitOutputParsers.ParseBranches(output);

        Assert.Equal(3, branches.Count);
        Assert.Equal(new GitBranch("main", true, false, "origin/main", "Initial commit", new DateTimeOffset(2024, 5, 1, 10, 0, 0, TimeSpan.FromHours(2))), branches[0]);
        Assert.Equal("feature/a", branches[1].Name);
        Assert.False(branches[1].IsCurrent);
        Assert.Null(branches[1].Upstream);
        Assert.Null(branches[1].LastCommitSubject);
        Assert.True(branches[2].IsRemote);
        Assert.Equal("origin/main", branches[2].Name);
    }

    [Fact]
    public void StashesParseIndexNameAndMessage()
    {
        var stashes = GitOutputParsers.ParseStashes("stash@{0}\x1fOn main: wip\nstash@{1}\x1fWIP on main: abc123 msg\n");
        Assert.Equal([new GitStash(0, "stash@{0}", "On main: wip"), new GitStash(1, "stash@{1}", "WIP on main: abc123 msg")], stashes);
    }

    [Theory]
    [InlineData("main", true)]
    [InlineData("feature/x-1", true)]
    [InlineData("übung", true)]
    [InlineData("-f", false)]
    [InlineData("a..b", false)]
    [InlineData("a b", false)]
    [InlineData("a~1", false)]
    [InlineData("x.lock", false)]
    [InlineData("a/.hidden", false)]
    [InlineData("/lead", false)]
    [InlineData("trail/", false)]
    [InlineData("dot.", false)]
    [InlineData("a@{1}", false)]
    [InlineData("@", false)]
    [InlineData("HEAD", false)]
    [InlineData("a\\b", false)]
    [InlineData("a:b", false)]
    [InlineData("", false)]
    public void BranchNamesAreValidated(string name, bool valid) => Assert.Equal(valid, GitRefNames.IsValidBranchName(name));

    [Theory]
    [InlineData("origin/main", true)]
    [InlineData("HEAD~2", true)]
    [InlineData("v1.0", true)]
    [InlineData("--orphan", false)]
    [InlineData("a b", false)]
    [InlineData("a\nb", false)]
    public void RevisionsAreValidated(string revision, bool valid) => Assert.Equal(valid, GitRefNames.IsSafeRevision(revision));

    [Fact]
    public void QuotesPathsLikeGit()
    {
        Assert.Equal("a/plain name.txt", UntrackedDiff.Quote("a/plain name.txt"));
        Assert.Equal("\"a/tab\\there\"", UntrackedDiff.Quote("a/tab\there"));
        Assert.Equal("\"a/q\\\"uote\"", UntrackedDiff.Quote("a/q\"uote"));
    }

    [Fact]
    public void OperationDetectionReadsGitDirMarkers()
    {
        var dir = Path.Combine(Path.GetTempPath(), "pickle-git-tests", Guid.NewGuid().ToString("N")[..12]);
        Directory.CreateDirectory(dir);
        try
        {
            Assert.Null(GitStatusParser.DetectOperation(dir));
            File.WriteAllText(Path.Combine(dir, "BISECT_LOG"), string.Empty);
            Assert.Equal("bisect", GitStatusParser.DetectOperation(dir));
            File.WriteAllText(Path.Combine(dir, "REVERT_HEAD"), string.Empty);
            Assert.Equal("revert", GitStatusParser.DetectOperation(dir));
            File.WriteAllText(Path.Combine(dir, "CHERRY_PICK_HEAD"), string.Empty);
            Assert.Equal("cherry-pick", GitStatusParser.DetectOperation(dir));
            File.WriteAllText(Path.Combine(dir, "MERGE_HEAD"), string.Empty);
            Assert.Equal("merge", GitStatusParser.DetectOperation(dir));
            Directory.CreateDirectory(Path.Combine(dir, "rebase-apply"));
            File.WriteAllText(Path.Combine(dir, "rebase-apply", "applying"), string.Empty);
            Assert.Equal("am", GitStatusParser.DetectOperation(dir));
            Directory.CreateDirectory(Path.Combine(dir, "rebase-merge"));
            Assert.Equal("rebase", GitStatusParser.DetectOperation(dir));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void GitDirFollowsGitFileForWorktrees()
    {
        var dir = Path.Combine(Path.GetTempPath(), "pickle-git-tests", Guid.NewGuid().ToString("N")[..12]);
        var worktree = Path.Combine(dir, "wt");
        var sub = Path.Combine(worktree, "src", "deep");
        Directory.CreateDirectory(sub);
        try
        {
            File.WriteAllText(Path.Combine(worktree, ".git"), "gitdir: ../main/.git/worktrees/wt\n");
            Assert.Equal(worktree, GitStatusParser.FindRoot(sub));
            Assert.Equal(Path.GetFullPath(Path.Combine(dir, "main", ".git", "worktrees", "wt")), GitStatusParser.ResolveGitDir(worktree));
            Assert.Null(GitStatusParser.FindRoot(Path.Combine(dir, "missing")));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void LocateIgnoresRelativePathEntries()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Windows falls back to the Git for Windows install location.");
        Assert.Null(GitProcess.Locate("." + Path.PathSeparator + "relative/bin"));
    }
}
