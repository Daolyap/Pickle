using Pickle.Abstractions;

namespace Pickle.Tui.Panels.Jobs;

/// <summary>Registers the jobs panel (Alt+J).</summary>
public sealed class JobsPanelPlugin : IPicklePlugin
{
    public const string PanelId = "jobs";

    public string Id => "pickle.jobs";

    public string DisplayName => "Jobs";

    public string Description => "View, receive, stop and start PowerShell background jobs.";

    public void Initialize(IPickleContext context) =>
        context.Panels.Register(new PanelDescriptor
        {
            Id = PanelId,
            Title = "Jobs",
            Description = "PowerShell background jobs",
            DefaultKey = "Alt+J",
            CreateView = ctx => new JobsPanel(ctx),
        });
}
