using Pickle.Abstractions;
using Pickle.Abstractions.Services;
using Pickle.Tui;
using Terminal.Gui.Input;

namespace Pickle.Modules.GitHub;

/// <summary>GitHub (Alt+Shift+G): pull requests, issues and workflow runs of the current repository (or <c>owner/repo</c> as the argument).</summary>
internal sealed class GitHubPanel : ResourcePanel<GitHubItem>
{
    public const string PanelId = "github";

    private readonly GitHubService _github;
    private GitHubKind _kind = GitHubKind.PullRequests;
    private bool _all;

    public static PanelDescriptor Descriptor { get; } = new()
    {
        Id = PanelId,
        Title = "GitHub",
        Description = "Pull requests, issues and workflow runs through the gh CLI",
        DefaultKey = "Alt+Shift+G",
        CreateView = context => new GitHubPanel(context),
    };

    public GitHubPanel(PanelContext context)
        : base(context, "GitHub", i => $"#{i.Number} {i.Title}", "Details")
    {
        _github = new GitHubService(context.Pickle.Services.Require<IProgramRunner>(), () => context.Pickle.Shell.CurrentDirectory);
        if (!string.IsNullOrWhiteSpace(context.Argument))
        {
            var parts = context.Argument.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            foreach (var part in parts)
            {
                if (Enum.TryParse<GitHubKind>(part, ignoreCase: true, out var kind))
                {
                    _kind = kind;
                }
                else if (part.Contains('/', StringComparison.Ordinal))
                {
                    _github.Repository = part;
                }
            }
        }

        Retitle();
        AddCommand(Key.F2, "Open in browser", i => _github.ShellCommand(i, "web"));
        AddCommand(Key.F3, "Checkout PR", i => _github.ShellCommand(i, "checkout"));
        AddCommand(Key.F4, "Failed logs", i => _github.ShellCommand(i, "logs"));
        AddCommand(Key.F8, "Diff", i => _github.ShellCommand(i, "diff"));
        AddAction(Key.F7, "Re-run failed", (i, ct) => Rerun(i, ct), confirm: i => $"Re-run the failed jobs of '{i.Title}'?", enabled: i => i.Kind == GitHubKind.Runs);
        AddHint(Key.F6, "PRs/Issues/Runs", CycleKind);
        AddHint(Key.F9, "Open/All", () =>
        {
            _all = !_all;
            Retitle();
            Reload();
        });
    }

    protected override TimeSpan? AutoRefresh => TimeSpan.FromSeconds(60);

    protected override string EmptyMessage => "Nothing to show. gh needs to be installed and signed in (gh auth login), and the folder must be a GitHub repository (or open the panel with owner/repo).";

    protected override async Task<IReadOnlyList<GitHubItem>> LoadAsync(CancellationToken cancellationToken)
    {
        if (await _github.AuthProblemAsync(cancellationToken).ConfigureAwait(false) is { } problem)
        {
            throw new InvalidOperationException(problem);
        }

        return await _github.ListAsync(_kind, _all, cancellationToken).ConfigureAwait(false);
    }

    protected override Task<IReadOnlyList<string>> DescribeAsync(GitHubItem item, CancellationToken cancellationToken) => _github.DescribeAsync(item, cancellationToken);

    protected override string KeyOf(GitHubItem item) => item.Kind + "|" + item.Number;

    protected override string? Hint(GitHubItem item) => item.State;

    protected override string? Detail(GitHubItem item) => item.Summary;

    protected override Terminal.Gui.Drawing.Color? ItemColor(GitHubItem item) =>
        item.IsBad ? Schemes.ErrorText.Foreground : item.IsGood ? Schemes.Success.Foreground : item.State is "closed" or "draft" or "completed" ? Schemes.Muted.Foreground : null;

    private async Task<ActionOutcome> Rerun(GitHubItem item, CancellationToken cancellationToken)
    {
        var result = await _github.RerunFailedAsync(item, cancellationToken).ConfigureAwait(false);
        return result.Success ? ActionOutcome.Done(result.Message) : ActionOutcome.Fail(result.Message);
    }

    private void CycleKind()
    {
        _kind = (GitHubKind)(((int)_kind + 1) % Enum.GetValues<GitHubKind>().Length);
        Retitle();
        Reload();
    }

    private void Retitle() => PanelTitle = $"GitHub · {_github.Repository ?? "this repository"} · {_kind.ToString().ToLowerInvariant()}{(_all ? " (all)" : string.Empty)}";
}
