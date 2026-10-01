using Pickle.Abstractions;
using Pickle.Abstractions.Services;
using Pickle.Tui;

namespace Pickle.Modules.GitHub;

/// <summary><c>pk gh</c>: the panel interactively, objects for scripts.</summary>
internal sealed class GitHubCommand : PanelCommand
{
    public override string Name => "gh";

    public override string Description => "GitHub pull requests, issues and workflow runs (through the gh CLI)";

    public override string Usage => "pk gh [prs|issues|runs] [owner/repo] | list [prs|issues|runs] [--repo owner/repo] [--all]";

    public override IReadOnlyList<string> Examples => ["pk gh", "pk gh runs cli/cli", "pk gh list prs --all"];

    protected override string PanelId => GitHubPanel.PanelId;

    protected override async Task<int> ListAsync(CommandOutput output, IReadOnlyList<string> raw, CancellationToken cancellationToken)
    {
        var args = CommandArgs.Parse(raw, "repo");
        var github = new GitHubService(output.Pickle.Services.Require<IProgramRunner>(), () => output.Context.Cwd) { Repository = args.Value("repo") };
        if (await github.AuthProblemAsync(cancellationToken).ConfigureAwait(false) is { } problem)
        {
            output.Failure(problem);
            return 1;
        }

        var kind = args.Arg(0)?.ToLowerInvariant() switch { "issues" or "issue" => GitHubKind.Issues, "runs" or "run" => GitHubKind.Runs, _ => GitHubKind.PullRequests };
        foreach (var item in await github.ListAsync(kind, args.Has("all"), cancellationToken).ConfigureAwait(false))
        {
            output.Object(Display.Columns(item, "Number", "Title", "State", "Author", "Url"));
        }

        return 0;
    }
}
