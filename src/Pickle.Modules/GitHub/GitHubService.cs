using System.Text.RegularExpressions;
using Pickle.Abstractions;
using Pickle.Abstractions.Services;

namespace Pickle.Modules.GitHub;

public enum GitHubKind
{
    PullRequests,
    Issues,
    Runs,
}

public sealed record GitHubItem(GitHubKind Kind, string Number, string Title, string Author, string Summary, string State, string Url, IReadOnlyList<string> Lines)
{
    public bool IsGood => State is "success" or "approved" or "merged";

    public bool IsBad => State is "failure" or "cancelled" or "timed_out" or "changes requested";
}

/// <summary>GitHub through the <c>gh</c> CLI (it already holds your login), for the repository of the current folder or <c>-R owner/repo</c>.</summary>
public sealed partial class GitHubService(IProgramRunner runner, Func<string> workingDirectory)
{
    public bool IsAvailable => runner.Find("gh") is not null;

    public string? Repository { get; set; }

    public async Task<IReadOnlyList<GitHubItem>> ListAsync(GitHubKind kind, bool includeClosed, CancellationToken cancellationToken)
    {
        string[] arguments = kind switch
        {
            GitHubKind.PullRequests => ["pr", "list", "--state", includeClosed ? "all" : "open", "--limit", "50", "--json", "number,title,author,isDraft,headRefName,updatedAt,url,reviewDecision,state"],
            GitHubKind.Issues => ["issue", "list", "--state", includeClosed ? "all" : "open", "--limit", "50", "--json", "number,title,author,labels,updatedAt,url,state"],
            _ => ["run", "list", "--limit", "30", "--json", "databaseId,displayTitle,status,conclusion,workflowName,headBranch,createdAt,url,event"],
        };
        var json = await OutputAsync(arguments, cancellationToken).ConfigureAwait(false);
        return kind switch
        {
            GitHubKind.PullRequests => ParsePullRequests(json),
            GitHubKind.Issues => ParseIssues(json),
            _ => ParseRuns(json),
        };
    }

    public async Task<IReadOnlyList<string>> DescribeAsync(GitHubItem item, CancellationToken cancellationToken)
    {
        string[] arguments = item.Kind switch
        {
            GitHubKind.PullRequests => ["pr", "view", Number(item), "--comments"],
            GitHubKind.Issues => ["issue", "view", Number(item), "--comments"],
            _ => ["run", "view", Number(item)],
        };
        var result = await runner.RunAsync("gh", WithRepo(arguments), Options(), cancellationToken).ConfigureAwait(false);
        var lines = result.StdOut.Replace("\r", string.Empty, StringComparison.Ordinal).Split('\n').Select(l => l.TrimEnd()).Take(120).ToList();
        return lines.Count > 1 ? lines : item.Lines;
    }

    public async Task<ServiceOperationResult> RerunFailedAsync(GitHubItem item, CancellationToken cancellationToken)
    {
        var result = await runner.RunAsync("gh", WithRepo(["run", "rerun", Number(item), "--failed"]), Options(), cancellationToken).ConfigureAwait(false);
        return new ServiceOperationResult(result.Success, result.Success ? $"Re-running the failed jobs of run {item.Number}." : result.Message);
    }

    /// <summary>The line for something interactive: the browser, checkout, run logs.</summary>
    public string? ShellCommand(GitHubItem item, string what)
    {
        var number = Number(item);
        var repo = Repository is { Length: > 0 } r && RepoName().IsMatch(r) ? new[] { "-R", r } : [];
        return (item.Kind, what) switch
        {
            (GitHubKind.PullRequests, "web") => PowerShellQuote.Command("gh", ["pr", "view", number, "--web", .. repo]),
            (GitHubKind.Issues, "web") => PowerShellQuote.Command("gh", ["issue", "view", number, "--web", .. repo]),
            (GitHubKind.Runs, "web") => PowerShellQuote.Command("gh", ["run", "view", number, "--web", .. repo]),
            (GitHubKind.PullRequests, "checkout") => PowerShellQuote.Command("gh", ["pr", "checkout", number, .. repo]),
            (GitHubKind.Runs, "logs") => PowerShellQuote.Command("gh", ["run", "view", number, "--log-failed", .. repo]),
            (GitHubKind.PullRequests, "diff") => PowerShellQuote.Command("gh", ["pr", "diff", number, .. repo]),
            _ => null,
        };
    }

    public async Task<string?> AuthProblemAsync(CancellationToken cancellationToken)
    {
        if (!IsAvailable)
        {
            return "The GitHub CLI (gh) is not installed. Windows: pk tool install gh; Linux/macOS: your package manager (https://cli.github.com).";
        }

        var result = await runner.RunAsync("gh", ["auth", "status"], null, cancellationToken).ConfigureAwait(false);
        return result.Success ? null : "Not signed in to GitHub. Run: gh auth login";
    }

    public static IReadOnlyList<GitHubItem> ParsePullRequests(string json)
    {
        var list = new List<GitHubItem>();
        if (ModuleJson.Document(json) is not { ValueKind: System.Text.Json.JsonValueKind.Array } array)
        {
            return list;
        }

        foreach (var pr in array.EnumerateArray())
        {
            var number = pr.Text("number");
            var author = pr.Child("author")?.Text("login") ?? "?";
            var state = pr.Text("state", "OPEN");
            var review = pr.Text("reviewDecision").ToLowerInvariant().Replace('_', ' ');
            var shown = state == "MERGED" ? "merged" : state == "CLOSED" ? "closed" : review.Length > 0 ? review : pr.Child("isDraft") is { ValueKind: System.Text.Json.JsonValueKind.True } ? "draft" : "open";
            list.Add(new GitHubItem(GitHubKind.PullRequests, number, pr.Text("title"), author, $"#{number} {pr.Text("headRefName")} · {author}", shown, pr.Text("url"),
            [
                $"#{number}  {pr.Text("title")}",
                $"Author:  {author}",
                $"Branch:  {pr.Text("headRefName")}",
                $"State:   {shown}",
                $"Updated: {pr.Text("updatedAt")}",
                $"URL:     {pr.Text("url")}",
            ]));
        }

        return [.. list.Where(i => i.Number.Length > 0)];
    }

    public static IReadOnlyList<GitHubItem> ParseIssues(string json)
    {
        var list = new List<GitHubItem>();
        if (ModuleJson.Document(json) is not { ValueKind: System.Text.Json.JsonValueKind.Array } array)
        {
            return list;
        }

        foreach (var issue in array.EnumerateArray())
        {
            var number = issue.Text("number");
            var author = issue.Child("author")?.Text("login") ?? "?";
            var labels = string.Join(", ", issue.Items("labels").Select(l => l.Text("name")));
            list.Add(new GitHubItem(GitHubKind.Issues, number, issue.Text("title"), author, $"#{number} {labels} · {author}", issue.Text("state", "OPEN").ToLowerInvariant(), issue.Text("url"),
            [
                $"#{number}  {issue.Text("title")}",
                $"Author:  {author}",
                $"Labels:  {labels}",
                $"State:   {issue.Text("state").ToLowerInvariant()}",
                $"Updated: {issue.Text("updatedAt")}",
                $"URL:     {issue.Text("url")}",
            ]));
        }

        return [.. list.Where(i => i.Number.Length > 0)];
    }

    public static IReadOnlyList<GitHubItem> ParseRuns(string json)
    {
        var list = new List<GitHubItem>();
        if (ModuleJson.Document(json) is not { ValueKind: System.Text.Json.JsonValueKind.Array } array)
        {
            return list;
        }

        foreach (var run in array.EnumerateArray())
        {
            var id = run.Text("databaseId");
            var status = run.Text("status");
            var conclusion = run.Text("conclusion");
            var state = status == "completed" ? (conclusion.Length > 0 ? conclusion : "completed") : status;
            list.Add(new GitHubItem(GitHubKind.Runs, id, run.Text("displayTitle"), run.Text("event"), $"{run.Text("workflowName")} · {run.Text("headBranch")}", state, run.Text("url"),
            [
                run.Text("displayTitle"),
                $"Workflow: {run.Text("workflowName")}",
                $"Branch:   {run.Text("headBranch")}",
                $"Result:   {state}",
                $"Started:  {run.Text("createdAt")}",
                $"URL:      {run.Text("url")}",
            ]));
        }

        return [.. list.Where(i => i.Number.Length > 0)];
    }

    internal static string Number(GitHubItem item) => Digits().IsMatch(item.Number) ? item.Number : throw new ArgumentException($"'{item.Number}' is not a number.");

    private IReadOnlyList<string> WithRepo(string[] arguments) =>
        Repository is { Length: > 0 } repo ? [.. arguments, "-R", RepoName().IsMatch(repo) ? repo : throw new ArgumentException($"'{repo}' is not owner/repo.")] : arguments;

    private ProgramRunOptions Options() => new() { WorkingDirectory = workingDirectory(), Timeout = TimeSpan.FromSeconds(30) };

    private async Task<string> OutputAsync(string[] arguments, CancellationToken cancellationToken)
    {
        var result = await runner.RunAsync("gh", WithRepo(arguments), Options(), cancellationToken).ConfigureAwait(false);
        if (!result.WasFound)
        {
            throw new InvalidOperationException("gh was not found on PATH.");
        }

        return result.Success ? result.StdOut : throw new InvalidOperationException(result.Message);
    }

    [GeneratedRegex(@"^\d{1,12}$")]
    private static partial Regex Digits();

    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9._-]{0,99}/[A-Za-z0-9._-]{1,100}$")]
    private static partial Regex RepoName();
}
