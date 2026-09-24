using Pickle.Abstractions.Services;

namespace Pickle.Core.Tests.Git;

/// <summary>GitService against real temporary repositories (skipped when git isn't installed).</summary>
public class GitServiceTests
{
    public GitServiceTests() => Assert.SkipUnless(TempRepo.GitInstalled, "git is not installed");

    [Fact]
    public async Task OutsideARepositoryStatusIsNull()
    {
        using var repo = TempRepo.Create(init: false);
        Assert.Null(await repo.Git.FindRepositoryRootAsync(repo.Root));
        Assert.Null(await repo.Git.GetStatusAsync(repo.Root));
        Assert.Null(await repo.Git.GetStatusAsync(Path.Combine(repo.Root, "does-not-exist")));
    }

    [Fact]
    public async Task FindsTheRootFromASubdirectory()
    {
        using var repo = TempRepo.Create();
        repo.Write("src/deep/file.txt", "x\n");
        var sub = Path.Combine(repo.Root, "src", "deep");
        Assert.Equal(repo.Root, await repo.Git.FindRepositoryRootAsync(sub));
        Assert.Equal(repo.Root, await repo.Git.FindRepositoryRootAsync(Path.Combine(sub, "file.txt")));
        var status = await repo.Git.GetStatusAsync(sub);
        Assert.NotNull(status);
        Assert.Equal(repo.Root, status.Root);
        Assert.Equal("main", status.Branch);
        Assert.Null(status.HeadSha);
        Assert.Equal("src/", Assert.Single(status.Entries).Path);
    }

    [Fact]
    public void StatusReportsModifiedAddedDeletedRenamedAndUntracked()
    {
        using var repo = TempRepo.Create();
        repo.Write("mod.txt", "one\n");
        repo.Write("del.txt", "gone soon\n");
        repo.Write("old.txt", "rename me please, enough content to detect a rename\n");
        repo.Write("both.txt", "both\n");
        repo.CommitAll("init");

        repo.Write("mod.txt", "two\n");
        File.Delete(Path.Combine(repo.Root, "del.txt"));
        repo.Run("mv", "old.txt", "new.txt");
        repo.Write("added.txt", "added\n");
        repo.Run("add", "added.txt");
        repo.Write("both.txt", "staged\n");
        repo.Run("add", "both.txt");
        repo.Write("both.txt", "staged then edited\n");
        repo.Write("untracked.txt", "?\n");

        var status = repo.Status();
        var byPath = status.Entries.ToDictionary(e => e.Path);

        Assert.Equal("main", status.Branch);
        Assert.NotNull(status.HeadSha);
        Assert.Null(status.Operation);
        Assert.Equal(new GitStatusEntry("mod.txt", null, GitChangeKind.None, GitChangeKind.Modified), byPath["mod.txt"]);
        Assert.Equal(new GitStatusEntry("del.txt", null, GitChangeKind.None, GitChangeKind.Deleted), byPath["del.txt"]);
        Assert.Equal(new GitStatusEntry("new.txt", "old.txt", GitChangeKind.Renamed, GitChangeKind.None), byPath["new.txt"]);
        Assert.Equal(new GitStatusEntry("added.txt", null, GitChangeKind.Added, GitChangeKind.None), byPath["added.txt"]);
        Assert.Equal(new GitStatusEntry("both.txt", null, GitChangeKind.Modified, GitChangeKind.Modified), byPath["both.txt"]);
        Assert.True(byPath["untracked.txt"].IsUntracked);
        Assert.Equal(3, status.StagedCount);
        Assert.Equal(3, status.UnstagedCount);
        Assert.Equal(1, status.UntrackedCount);
    }

    [Fact]
    public void MergeConflictIsReportedWithTheMergeOperation()
    {
        using var repo = CreateDivergedRepo();
        Assert.False(repo.TryRun("merge", "other").Success);

        var status = repo.Status();
        var entry = Assert.Single(status.Entries);
        Assert.Equal("c.txt", entry.Path);
        Assert.True(entry.IsConflicted);
        Assert.False(entry.IsStaged);
        Assert.Equal(1, status.ConflictCount);
        Assert.Equal("merge", status.Operation);
    }

    [Theory]
    [InlineData("rebase")]
    [InlineData("cherry-pick")]
    [InlineData("revert")]
    public void InProgressOperationsAreDetected(string operation)
    {
        using var repo = CreateDivergedRepo();
        var result = operation switch
        {
            "rebase" => repo.TryRun("rebase", "other"),
            "cherry-pick" => repo.TryRun("cherry-pick", "other"),
            _ => RevertConflict(repo),
        };
        Assert.False(result.Success);
        Assert.Equal(operation, repo.Status().Operation);
    }

    [Fact]
    public void BisectIsDetected()
    {
        using var repo = TempRepo.Create();
        repo.Write("a.txt", "1\n");
        repo.CommitAll("one");
        repo.Run("bisect", "start");
        Assert.Equal("bisect", repo.Status().Operation);
    }

    [Fact]
    public async Task WorktreesResolveTheirOwnGitDirectory()
    {
        using var repo = CreateDivergedRepo();
        var worktree = Path.Combine(repo.BaseDirectory, "wt");
        repo.Run("worktree", "add", "-q", "-b", "wt", worktree, "main");
        Assert.True(File.Exists(Path.Combine(worktree, ".git")));

        var status = await repo.Git.GetStatusAsync(worktree);
        Assert.NotNull(status);
        Assert.Equal(worktree, status.Root);
        Assert.Equal("wt", status.Branch);
        Assert.Null(status.Operation);

        Assert.False((await repo.Git.RunAsync(worktree, ["merge", "other"])).Success);
        Assert.Equal("merge", (await repo.Git.GetStatusAsync(worktree))!.Operation);
        Assert.Null(repo.Status().Operation);
    }

    [Fact]
    public async Task AheadBehindFetchPullAndPushAgainstABareRemote()
    {
        using var repo = TempRepo.Create();
        var bare = Path.Combine(repo.BaseDirectory, "remote.git");
        var clone = Path.Combine(repo.BaseDirectory, "clone");
        repo.RunIn(repo.BaseDirectory, "init", "-q", "--bare", "--initial-branch=main", bare);
        repo.Write("a.txt", "1\n");
        repo.CommitAll("one");
        repo.Run("remote", "add", "origin", bare);

        var push = await repo.Git.PushAsync(repo.Root, setUpstream: true);
        Assert.True(push.Success, push.Error);
        var status = repo.Status();
        Assert.Equal("origin/main", status.Upstream);
        Assert.Equal((0, 0), (status.Ahead, status.Behind));

        repo.RunIn(repo.BaseDirectory, "clone", "-q", bare, clone);
        File.WriteAllText(Path.Combine(clone, "b.txt"), "from clone\n");
        repo.RunIn(clone, "add", "-A");
        repo.RunIn(clone, "commit", "-q", "-m", "two");
        repo.RunIn(clone, "push", "-q");

        Assert.True((await repo.Git.FetchAsync(repo.Root)).Success);
        Assert.Equal((0, 1), (repo.Status().Ahead, repo.Status().Behind));

        var pull = await repo.Git.PullAsync(repo.Root);
        Assert.True(pull.Success, pull.Error);
        Assert.True(repo.Exists("b.txt"));
        Assert.Equal(0, repo.Status().Behind);

        repo.Write("c.txt", "local\n");
        repo.CommitAll("three");
        File.WriteAllText(Path.Combine(clone, "d.txt"), "remote\n");
        repo.RunIn(clone, "add", "-A");
        repo.RunIn(clone, "commit", "-q", "-m", "four");
        repo.RunIn(clone, "push", "-q");
        Assert.True((await repo.Git.FetchAsync(repo.Root)).Success);
        status = repo.Status();
        Assert.Equal((1, 1), (status.Ahead, status.Behind));

        var rejected = await repo.Git.PushAsync(repo.Root);
        Assert.False(rejected.Success);
        Assert.NotEmpty(rejected.Error);

        var branches = await repo.Git.GetBranchesAsync(repo.Root);
        Assert.Contains(branches, b => b is { Name: "origin/main", IsRemote: true });
        Assert.Contains(branches, b => b is { Name: "main", IsCurrent: true, Upstream: "origin/main" });
        Assert.DoesNotContain(branches, b => b.Name.EndsWith("HEAD", StringComparison.Ordinal));
        Assert.DoesNotContain(await repo.Git.GetBranchesAsync(repo.Root, includeRemote: false), b => b.IsRemote);
    }

    [Fact]
    public async Task CheckoutCreatesTrackingBranchForAUniqueRemoteBranch()
    {
        using var repo = TempRepo.Create();
        var bare = Path.Combine(repo.BaseDirectory, "remote.git");
        repo.RunIn(repo.BaseDirectory, "init", "-q", "--bare", "--initial-branch=main", bare);
        repo.Write("a.txt", "1\n");
        repo.CommitAll("one");
        repo.Run("remote", "add", "origin", bare);
        repo.Run("push", "-q", "origin", "main:topic");
        repo.Run("fetch", "-q", "origin");

        var result = await repo.Git.CheckoutAsync(repo.Root, "topic");
        Assert.True(result.Success, result.Error);
        var status = repo.Status();
        Assert.Equal("topic", status.Branch);
        Assert.Equal("origin/topic", status.Upstream);
    }

    [Fact]
    public async Task StashCountAndStashOperations()
    {
        using var repo = TempRepo.Create();
        repo.Write("a.txt", "1\n");
        repo.CommitAll("one");

        repo.Write("a.txt", "2\n");
        Assert.True((await repo.Git.StashPushAsync(repo.Root, "first\nline", includeUntracked: false)).Success);
        Assert.Equal("1\n", repo.Read("a.txt"));

        repo.Write("u.txt", "untracked\n");
        Assert.True((await repo.Git.StashPushAsync(repo.Root, null, includeUntracked: true)).Success);
        Assert.False(repo.Exists("u.txt"));

        var status = repo.Status();
        Assert.Equal(2, status.StashCount);
        Assert.True(status.IsClean);

        var stashes = await repo.Git.GetStashesAsync(repo.Root);
        Assert.Equal(2, stashes.Count);
        Assert.Equal(0, stashes[0].Index);
        Assert.Equal("stash@{0}", stashes[0].Name);
        Assert.Contains("WIP on main", stashes[0].Message, StringComparison.Ordinal);
        Assert.Equal(1, stashes[1].Index);
        Assert.Contains("first line", stashes[1].Message, StringComparison.Ordinal);

        Assert.True((await repo.Git.StashApplyAsync(repo.Root, 0, pop: false)).Success);
        Assert.True(repo.Exists("u.txt"));
        Assert.Equal(2, repo.Status().StashCount);

        Assert.True((await repo.Git.StashDropAsync(repo.Root, 0)).Success);
        Assert.Equal(1, repo.Status().StashCount);

        File.Delete(Path.Combine(repo.Root, "u.txt"));
        Assert.True((await repo.Git.StashApplyAsync(repo.Root, 0, pop: true)).Success);
        Assert.Equal("2\n", repo.Read("a.txt"));
        Assert.Equal(0, repo.Status().StashCount);
        Assert.Empty(await repo.Git.GetStashesAsync(repo.Root));
        Assert.False((await repo.Git.StashDropAsync(repo.Root, 5)).Success);
        Assert.False((await repo.Git.StashApplyAsync(repo.Root, -1, pop: false)).Success);
    }

    [Fact]
    public async Task StageUnstageAndDiscard()
    {
        using var repo = TempRepo.Create();
        repo.Write("a.txt", "one\n");
        repo.CommitAll("init");
        repo.Write("a.txt", "two\n");
        repo.Write("new.txt", "new\n");
        repo.Write("dir/x.txt", "x\n");
        repo.Write("dir/y.txt", "y\n");

        Assert.True((await repo.Git.StageAsync(repo.Root, ["a.txt"])).Success);
        Assert.Contains(new GitStatusEntry("a.txt", null, GitChangeKind.Modified, GitChangeKind.None), repo.Status().Entries);

        Assert.True((await repo.Git.UnstageAsync(repo.Root, ["a.txt"])).Success);
        Assert.Contains(new GitStatusEntry("a.txt", null, GitChangeKind.None, GitChangeKind.Modified), repo.Status().Entries);

        var discard = await repo.Git.DiscardAsync(repo.Root, ["a.txt", "new.txt", "dir/"]);
        Assert.True(discard.Success, discard.Error);
        Assert.Equal("one\n", repo.Read("a.txt"));
        Assert.False(repo.Exists("new.txt"));
        Assert.False(Directory.Exists(Path.Combine(repo.Root, "dir")));
        Assert.True(repo.Status().IsClean);

        repo.Write("b.txt", "b\n");
        Assert.True((await repo.Git.StageAsync(repo.Root, ["."])).Success);
        Assert.Equal(1, repo.Status().StagedCount);
        Assert.True((await repo.Git.UnstageAsync(repo.Root, ["."])).Success);
        Assert.Equal(0, repo.Status().StagedCount);

        Assert.False((await repo.Git.StageAsync(repo.Root, [])).Success);
        Assert.True((await repo.Git.DiscardAsync(repo.Root, ["nothing-here.txt"])).Success);
    }

    [Fact]
    public async Task UnstageWorksBeforeTheFirstCommit()
    {
        using var repo = TempRepo.Create();
        repo.Write("a.txt", "a\n");
        Assert.True((await repo.Git.StageAsync(repo.Root, ["a.txt"])).Success);
        Assert.Equal(GitChangeKind.Added, Assert.Single(repo.Status().Entries).IndexStatus);
        var result = await repo.Git.UnstageAsync(repo.Root, ["a.txt"]);
        Assert.True(result.Success, result.Error);
        Assert.True(Assert.Single(repo.Status().Entries).IsUntracked);
    }

    [Fact]
    public async Task PathsAreLiteralNotPathspecMagicOrGlobs()
    {
        using var repo = TempRepo.Create();
        var names = new List<string> { "br[1].txt", "br1.txt", "sp ace.txt", "ünï cødé.txt" };
        if (!OperatingSystem.IsWindows())
        {
            names.Add(":colon.txt");
        }

        foreach (var name in names)
        {
            repo.Write(name, name + "\n");
        }

        var stage = await repo.Git.StageAsync(repo.Root, [.. names.Where(n => n != "br1.txt")]);
        Assert.True(stage.Success, stage.Error);
        var byPath = repo.Status().Entries.ToDictionary(e => e.Path);
        Assert.True(byPath["br1.txt"].IsUntracked);
        Assert.Equal(GitChangeKind.Added, byPath["br[1].txt"].IndexStatus);
        Assert.Equal(GitChangeKind.Added, byPath["ünï cødé.txt"].IndexStatus);
        Assert.Equal(GitChangeKind.Added, byPath["sp ace.txt"].IndexStatus);
    }

    [Fact]
    public async Task DiffsForWorktreeIndexAndUntrackedFiles()
    {
        using var repo = TempRepo.Create();
        repo.Write("a.txt", "one\ntwo\n");
        repo.CommitAll("init");
        repo.Write("a.txt", "one\n2\n");

        var unstaged = await repo.Git.GetDiffAsync(repo.Root, "a.txt", staged: false);
        Assert.Contains("diff --git a/a.txt b/a.txt", unstaged, StringComparison.Ordinal);
        Assert.Contains("\n-two\n", unstaged, StringComparison.Ordinal);
        Assert.Contains("\n+2\n", unstaged, StringComparison.Ordinal);
        Assert.Equal(string.Empty, await repo.Git.GetDiffAsync(repo.Root, "a.txt", staged: true));

        await repo.Git.StageAsync(repo.Root, ["a.txt"]);
        Assert.Contains("\n+2\n", await repo.Git.GetDiffAsync(repo.Root, "a.txt", staged: true), StringComparison.Ordinal);
        Assert.Contains("\n+2\n", await repo.Git.GetDiffAsync(repo.Root, null, staged: true), StringComparison.Ordinal);

        repo.Write("u dir/new file.txt", "x\r\ny");
        var untracked = await repo.Git.GetDiffAsync(repo.Root, "u dir/", staged: false);
        Assert.Equal(
            "diff --git a/u dir/new file.txt b/u dir/new file.txt\nnew file mode 100644\n--- /dev/null\n+++ b/u dir/new file.txt\n@@ -0,0 +1,2 @@\n+x\r\n+y\n\\ No newline at end of file\n",
            untracked);

        var apply = await repo.Git.ApplyPatchToIndexAsync(repo.Root, untracked, reverse: false);
        Assert.True(apply.Success, apply.Error);
        var entry = repo.Status().Entries.Single(e => e.Path == "u dir/new file.txt");
        Assert.Equal(GitChangeKind.Added, entry.IndexStatus);
        Assert.Equal(GitChangeKind.None, entry.WorktreeStatus);

        var reverse = await repo.Git.ApplyPatchToIndexAsync(repo.Root, untracked, reverse: true);
        Assert.True(reverse.Success, reverse.Error);
        Assert.True(repo.Status().Entries.Single(e => e.Path.StartsWith("u dir", StringComparison.Ordinal)).IsUntracked);

        File.WriteAllBytes(Path.Combine(repo.Root, "bin.dat"), [1, 0, 2, 3]);
        Assert.Contains("Binary files /dev/null and b/bin.dat differ", await repo.Git.GetDiffAsync(repo.Root, "bin.dat", staged: false), StringComparison.Ordinal);
        repo.Write("empty.txt", string.Empty);
        Assert.Equal("diff --git a/empty.txt b/empty.txt\nnew file mode 100644\n", await repo.Git.GetDiffAsync(repo.Root, "empty.txt", staged: false));
        Assert.True((await repo.Git.ApplyPatchToIndexAsync(repo.Root, "diff --git a/empty.txt b/empty.txt\nnew file mode 100644\n", reverse: false)).Success);
        Assert.Equal(GitChangeKind.Added, repo.Status().Entries.Single(e => e.Path == "empty.txt").IndexStatus);
    }

    [Fact]
    public async Task ApplyRejectsEmptyAndNonUtf8Patches()
    {
        using var repo = TempRepo.Create();
        Assert.False((await repo.Git.ApplyPatchToIndexAsync(repo.Root, "  ", reverse: false)).Success);
        var mangled = await repo.Git.ApplyPatchToIndexAsync(repo.Root, "diff --git a/x b/x\n+caf�\n", reverse: false);
        Assert.False(mangled.Success);
        Assert.Contains("UTF-8", mangled.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CommitAndAmend()
    {
        using var repo = TempRepo.Create();
        repo.Write("a.txt", "a\n");
        await repo.Git.StageAsync(repo.Root, ["a.txt"]);

        Assert.False((await repo.Git.CommitAsync(repo.Root, "   ")).Success);
        var commit = await repo.Git.CommitAsync(repo.Root, "Subject ünïcødé\r\n\r\n# not a comment\nBody line");
        Assert.True(commit.Success, commit.Error);

        var log = await repo.Git.GetLogAsync(repo.Root);
        var head = Assert.Single(log);
        Assert.Equal("Subject ünïcødé", head.Subject);
        Assert.Equal("Pickle Test", head.Author);
        Assert.Contains("HEAD -> main", head.Refs);
        Assert.Equal("Subject ünïcødé\n\n# not a comment\nBody line\n", repo.Run("log", "-1", "--format=%B").TrimEnd('\n') + "\n");

        var nothing = await repo.Git.CommitAsync(repo.Root, "nothing staged");
        Assert.False(nothing.Success);
        Assert.False(string.IsNullOrWhiteSpace(nothing.Output + nothing.Error));

        repo.Write("b.txt", "b\n");
        await repo.Git.StageAsync(repo.Root, ["b.txt"]);
        Assert.True((await repo.Git.CommitAsync(repo.Root, "Amended", amend: true)).Success);
        log = await repo.Git.GetLogAsync(repo.Root);
        Assert.Equal("Amended", Assert.Single(log).Subject);

        Assert.True((await repo.Git.CommitAsync(repo.Root, string.Empty, amend: true)).Success);
        Assert.Equal("Amended", Assert.Single(await repo.Git.GetLogAsync(repo.Root)).Subject);
        Assert.True(repo.Status().IsClean);
    }

    [Fact]
    public async Task BranchCreateCheckoutAndDelete()
    {
        using var repo = TempRepo.Create();
        repo.Write("a.txt", "a\n");
        repo.CommitAll("init");

        Assert.True((await repo.Git.CheckoutAsync(repo.Root, "feature/one", create: true)).Success);
        Assert.Equal("feature/one", repo.Status().Branch);
        var branches = await repo.Git.GetBranchesAsync(repo.Root);
        Assert.Equal(["feature/one", "main"], branches.Select(b => b.Name).Order(StringComparer.Ordinal));
        Assert.True(branches.Single(b => b.Name == "feature/one").IsCurrent);
        Assert.Equal("init", branches[0].LastCommitSubject);
        Assert.NotNull(branches[0].LastCommitDate);

        repo.Write("f.txt", "f\n");
        repo.CommitAll("feature work");
        Assert.True((await repo.Git.CheckoutAsync(repo.Root, "main")).Success);
        Assert.Equal("main", repo.Status().Branch);

        var refused = await repo.Git.DeleteBranchAsync(repo.Root, "feature/one");
        Assert.False(refused.Success);
        Assert.True((await repo.Git.DeleteBranchAsync(repo.Root, "feature/one", force: true)).Success);
        Assert.Equal(["main"], (await repo.Git.GetBranchesAsync(repo.Root)).Select(b => b.Name));

        Assert.False((await repo.Git.CheckoutAsync(repo.Root, "--orphan")).Success);
        Assert.False((await repo.Git.CheckoutAsync(repo.Root, "bad name", create: true)).Success);
        Assert.False((await repo.Git.CheckoutAsync(repo.Root, "-b", create: true)).Success);
        Assert.False((await repo.Git.DeleteBranchAsync(repo.Root, "-D")).Success);

        // A detached checkout by sha (and the file named like a branch is never treated as a path).
        var sha = repo.Status().HeadSha!;
        repo.Write("main", "a file named main\n");
        Assert.True((await repo.Git.CheckoutAsync(repo.Root, sha[..10])).Success);
        Assert.True(repo.Status().IsDetached);
        Assert.True((await repo.Git.CheckoutAsync(repo.Root, "main")).Success);
        Assert.Equal("a file named main\n", repo.Read("main"));
    }

    [Fact]
    public async Task LogWithGraphAfterAMerge()
    {
        using var repo = TempRepo.Create();
        repo.Write("a.txt", "a\n");
        repo.CommitAll("base");
        repo.Run("checkout", "-q", "-b", "side");
        repo.Write("s.txt", "s\n");
        repo.CommitAll("side work");
        repo.Run("checkout", "-q", "main");
        repo.Write("m.txt", "m\n");
        repo.CommitAll("main work");
        repo.Run("merge", "-q", "--no-ff", "-m", "Merge side", "side");

        var graph = await repo.Git.GetLogAsync(repo.Root, max: 50, graph: true);
        Assert.Equal(4, graph.Count);
        Assert.Equal("Merge side", graph[0].Subject);
        Assert.StartsWith("*", graph[0].Graph, StringComparison.Ordinal);
        Assert.Contains(graph, c => c.Graph.Contains('|', StringComparison.Ordinal));
        Assert.Equal("base", graph[^1].Subject);
        Assert.All(graph, c => Assert.Equal(40, c.Sha.Length));

        var flat = await repo.Git.GetLogAsync(repo.Root, max: 2, graph: false);
        Assert.Equal(2, flat.Count);
        Assert.All(flat, c => Assert.Equal(string.Empty, c.Graph));

        using var empty = TempRepo.Create();
        Assert.Empty(await empty.Git.GetLogAsync(empty.Root));
    }

    [Fact]
    public async Task RunAsyncPassesArgumentsVerbatim()
    {
        using var repo = TempRepo.Create();
        var result = await repo.Git.RunAsync(repo.Root, ["rev-parse", "--is-inside-work-tree"]);
        Assert.True(result.Success);
        Assert.Equal("true", result.Output.Trim());

        var bad = await repo.Git.RunAsync(repo.Root, ["definitely-not-a-git-command"]);
        Assert.False(bad.Success);
        Assert.NotEqual(0, bad.ExitCode);
        Assert.NotEmpty(bad.Error);
    }

    [Fact]
    public async Task CancellationStopsGit()
    {
        using var repo = TempRepo.Create();
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => repo.Git.GetStatusAsync(repo.Root, cts.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => repo.Git.RunAsync(repo.Root, ["status"], cts.Token));
    }

    [Fact]
    public async Task MissingGitExecutableFailsGracefully()
    {
        using var repo = TempRepo.Create();
        repo.Git.GitPath = Path.Combine(repo.BaseDirectory, "no-such-git");
        Assert.Null(await repo.Git.GetStatusAsync(repo.Root));
        var result = await repo.Git.StageAsync(repo.Root, ["a.txt"]);
        Assert.False(result.Success);
        Assert.Contains("could not be started", result.Error, StringComparison.Ordinal);
        Assert.Empty(await repo.Git.GetBranchesAsync(repo.Root));
    }

    [Fact]
    public async Task StatusIsFastEnoughForThePrompt()
    {
        using var repo = TempRepo.Create();
        for (var i = 0; i < 300; i++)
        {
            repo.Write($"src/dir{i % 10}/file{i}.txt", $"line {i}\n");
        }

        repo.CommitAll("many files");
        repo.Write("src/dir1/file1.txt", "changed\n");
        await repo.Git.GetStatusAsync(repo.Root);

        var watch = System.Diagnostics.Stopwatch.StartNew();
        for (var i = 0; i < 5; i++)
        {
            Assert.NotNull(await repo.Git.GetStatusAsync(repo.Root));
        }

        watch.Stop();
        TestContext.Current.SendDiagnosticMessage($"status x5: {watch.ElapsedMilliseconds} ms");
        Assert.True(watch.ElapsedMilliseconds < 5 * 1000, $"status took {watch.ElapsedMilliseconds / 5} ms on average");
    }

    private static TempRepo CreateDivergedRepo()
    {
        var repo = TempRepo.Create();
        repo.Write("c.txt", "base\n");
        repo.CommitAll("base");
        repo.Run("checkout", "-q", "-b", "other");
        repo.Write("c.txt", "other\n");
        repo.CommitAll("other");
        repo.Run("checkout", "-q", "main");
        repo.Write("c.txt", "main\n");
        repo.CommitAll("main");
        return repo;
    }

    private static GitCommandResult RevertConflict(TempRepo repo)
    {
        repo.Write("c.txt", "main edited again\n");
        repo.CommitAll("again");
        return repo.TryRun("revert", "--no-edit", "HEAD~1");
    }
}
