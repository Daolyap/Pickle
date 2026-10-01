using Pickle.Abstractions;
using Pickle.Modules.GitHub;
using Pickle.Testing.Fakes;
using Pickle.Tui.Tests;
using Terminal.Gui.Input;

namespace Pickle.Modules.Tests.GitHub;

public class GitHubTests
{
    private const string Prs = """[{"number":42,"title":"Add services panel","author":{"login":"ada"},"isDraft":false,"headRefName":"feat/services","updatedAt":"2025-10-01T10:00:00Z","url":"https://github.com/o/r/pull/42","reviewDecision":"APPROVED","state":"OPEN"},{"number":43,"title":"WIP","author":{"login":"bob"},"isDraft":true,"headRefName":"wip","updatedAt":"2025-10-01T11:00:00Z","url":"https://github.com/o/r/pull/43","reviewDecision":"","state":"OPEN"}]""";
    private const string Runs = """[{"databaseId":9001,"displayTitle":"Add services panel","status":"completed","conclusion":"failure","workflowName":"CI","headBranch":"feat/services","createdAt":"2025-10-01T10:05:00Z","url":"https://github.com/o/r/actions/runs/9001","event":"pull_request"},{"databaseId":9002,"displayTitle":"main","status":"in_progress","conclusion":"","workflowName":"CI","headBranch":"main","createdAt":"2025-10-01T10:06:00Z","url":"u","event":"push"}]""";

    [Fact]
    public void ParsesPullRequestsIssuesAndRuns()
    {
        var prs = GitHubService.ParsePullRequests(Prs);
        var issues = GitHubService.ParseIssues("""[{"number":7,"title":"Crash","author":{"login":"cy"},"labels":[{"name":"bug"},{"name":"p1"}],"updatedAt":"x","url":"u","state":"OPEN"}]""");
        var runs = GitHubService.ParseRuns(Runs);

        Assert.Equal([("42", "approved"), ("43", "draft")], prs.Select(p => (p.Number, p.State)));
        Assert.True(prs[0].IsGood);
        Assert.Equal("#7 bug, p1 · cy", issues[0].Summary);
        Assert.Equal([("9001", "failure"), ("9002", "in_progress")], runs.Select(r => (r.Number, r.State)));
        Assert.True(runs[0].IsBad);
    }

    [Fact]
    public void ShellCommandsAreQuotedAndNumbersChecked()
    {
        var github = new GitHubService(new FakeProgramRunner(), () => "/repo") { Repository = "cli/cli" };
        var pr = GitHubService.ParsePullRequests(Prs)[0];

        Assert.Equal("gh pr view 42 --web -R cli/cli", github.ShellCommand(pr, "web"));
        Assert.Equal("gh pr checkout 42 -R cli/cli", github.ShellCommand(pr, "checkout"));
        Assert.Equal("gh run view 9001 --log-failed -R cli/cli", github.ShellCommand(GitHubService.ParseRuns(Runs)[0], "logs"));
        Assert.Throws<ArgumentException>(() => github.ShellCommand(pr with { Number = "42; calc" }, "web"));
        github.Repository = "not a repo";
        Assert.DoesNotContain("-R", github.ShellCommand(pr, "web"), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ListsUseTheRepositoryOfTheCurrentFolderAndReportAuthProblems()
    {
        var runner = new FakeProgramRunner().On("gh", "pr list", Prs).On("gh", "auth status", string.Empty, exitCode: 1, stderr: "not logged in");
        var github = new GitHubService(runner, () => "/work/app");

        var prs = await github.ListAsync(GitHubKind.PullRequests, includeClosed: false, CancellationToken.None);

        Assert.Equal(2, prs.Count);
        Assert.Equal("/work/app", runner.Calls[0].Options!.WorkingDirectory);
        Assert.Equal("Not signed in to GitHub. Run: gh auth login", await github.AuthProblemAsync(CancellationToken.None));
        Assert.Contains("not installed", await new GitHubService(new FakeProgramRunner(), () => ".").AuthProblemAsync(CancellationToken.None), StringComparison.Ordinal);
    }

    [Fact]
    public void ThePanelListsPullRequestsAndHandsCheckoutToTheShell()
    {
        var runner = new FakeProgramRunner().On("gh", "auth status", "ok").On("gh", "pr list", Prs).On("gh", "pr view", "title: Add services panel\nbody\n");
        var script = new UiScript()
            .WaitFor("loaded", app => TuiHarness.Top<GitHubPanel>(app).List.TotalCount == 2 && TuiHarness.Top<GitHubPanel>(app).List.Selected?.Number == "42")
            .Press(Key.F3);
        var (t, host) = ModuleTestSupport.StartPanels("github", runner, script);
        using var _ = t;

        var result = host.Show("github");

        script.AssertOk();
        Assert.Equal(new PanelResult(PanelResultKind.RunCommand, "gh pr checkout 42"), result);
    }

    [Fact]
    public void ThePkCommandListsRuns()
    {
        var runner = new FakeProgramRunner().On("gh", "auth status", "ok").On("gh", "run list", Runs);
        using var t = ModuleTestSupport.Start("github", runner);

        Assert.Equal(["9001:failure", "9002:in_progress"], t.Run("pk gh list runs | ForEach-Object { \"$($_.Number):$($_.State)\" }"));
    }
}
