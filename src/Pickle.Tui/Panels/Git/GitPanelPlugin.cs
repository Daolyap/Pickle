using Pickle.Abstractions;
using Pickle.Abstractions.Services;

namespace Pickle.Tui.Panels.Git;

/// <summary>Registers the Git panel (Alt+G) and <c>pk git</c>.</summary>
public sealed class GitPanelPlugin : IPicklePlugin
{
    public string Id => "pickle.git";

    public string DisplayName => "Git";

    public string Description => "Interactive git status, staging, commits, branches, log and stash.";

    public void Initialize(IPickleContext context)
    {
        context.Panels.Register(new PanelDescriptor
        {
            Id = "git",
            Title = "Git",
            Description = "Status, diffs, hunk/line staging, commit, branches, log, stash, fetch/pull/push",
            DefaultKey = "Alt+G",
            CreateView = ctx => new GitPanel(ctx),
        });
        context.Commands.Register(new GitCommand());
    }
}

/// <summary><c>pk git</c> opens the panel; <c>pk git status</c> writes the <see cref="GitStatus"/> object.</summary>
public sealed class GitCommand : IPickleCommand
{
    public string Name => "git";

    public string Description => "Open the git panel, or output the repository status";

    public string Usage => "pk git [status]";

    public async ValueTask<int> ExecuteAsync(PickleCommandContext context, IReadOnlyList<string> args, CancellationToken cancellationToken)
    {
        if (args.Count == 0)
        {
            if (context.Pickle.Services.Get<IPanelHost>() is not { } host)
            {
                context.WriteError("Panels are not available in this session.");
                return 1;
            }

            host.Show("git", context.Cwd);
            return 0;
        }

        if (!string.Equals(args[0], "status", StringComparison.OrdinalIgnoreCase))
        {
            context.WriteError($"Unknown subcommand '{args[0]}'. Usage: {Usage}");
            return 2;
        }

        var git = context.Pickle.Services.Get<IGitService>();
        if (git is null || !git.IsGitAvailable)
        {
            context.WriteError("git was not found. Install Git and make sure it is on PATH.");
            return 1;
        }

        var status = await git.GetStatusAsync(context.Cwd, cancellationToken).ConfigureAwait(false);
        if (status is null)
        {
            context.WriteError($"Not a git repository: {context.Cwd}");
            return 1;
        }

        context.WriteObject(status);
        return 0;
    }
}
