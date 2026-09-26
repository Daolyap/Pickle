using Pickle.Abstractions;

namespace Pickle.Tui.Panels.Dashboard;

/// <summary>Registers the dashboard panel (Alt+I) and <c>pk dashboard</c>.</summary>
public sealed class DashboardPanelPlugin : IPicklePlugin
{
    public string Id => "pickle.dashboard";

    public string DisplayName => "Dashboard";

    public string Description => "The machine at a glance: CPU, memory, disks, network, power and busiest processes.";

    public void Initialize(IPickleContext context)
    {
        context.Panels.Register(new PanelDescriptor
        {
            Id = DashboardPanel.PanelId,
            Title = "Dashboard",
            Description = "System dashboard: CPU, memory, disks, network, uptime, battery, busiest processes",
            DefaultKey = "Alt+I",
            CreateView = ctx => new DashboardPanel(ctx),
        });
        context.Commands.Register(new DashboardCommand());
    }
}
