using Pickle.Abstractions;

namespace Pickle.Tui.Panels.Git;

/// <summary>FOUNDATION PLACEHOLDER — workstream W7 implements the Git panel (Alt+G) on IGitService.</summary>
public sealed class GitPanelPlugin : IPicklePlugin
{
    public string Id => "pickle.git";

    public string DisplayName => "Git";

    public string Description => "Interactive git status, staging, commits, branches, log and stash.";

    public void Initialize(IPickleContext context)
    {
    }
}
