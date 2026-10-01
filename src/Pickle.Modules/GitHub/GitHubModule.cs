using Pickle.Abstractions;
using Pickle.Abstractions.Services;

namespace Pickle.Modules.GitHub;

public sealed class GitHubModule : IPicklePlugin
{
    public const string ModuleId = "github";

    public static ModuleDescriptor Descriptor { get; } = new()
    {
        Id = ModuleId,
        Name = "GitHub",
        Description = "Pull requests, issues and workflow runs: panel and pk gh (uses the gh CLI)",
        Create = () => new GitHubModule(),
        Tools = ["gh"],
        Provides = ["pk gh", "panel: github (Alt+Shift+G)"],
    };

    string IPicklePlugin.Id => "pickle." + ModuleId;

    public string DisplayName => Descriptor.Name;

    public string Description => Descriptor.Description;

    public void Initialize(IPickleContext context)
    {
        context.Panels.Register(GitHubPanel.Descriptor);
        context.Commands.Register(new GitHubCommand());
    }
}
