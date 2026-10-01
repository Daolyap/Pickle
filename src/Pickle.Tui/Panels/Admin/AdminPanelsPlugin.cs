using Pickle.Abstractions;
using Pickle.Tui.Panels.Admin.EnvEditor;

namespace Pickle.Tui.Panels.Admin;

/// <summary>Registers the administration panels and their <c>pk</c> commands: services, logs, packages, hosts/PATH/environment, timers.</summary>
public sealed class AdminPanelsPlugin : IPicklePlugin
{
    public string Id => "pickle.admin.panels";

    public string DisplayName => "Administration panels";

    public string Description => "Services (Alt+V), hosts/PATH/environment (Alt+O), logs (Alt+L), packages (Alt+K) and scheduled jobs (Alt+S on Linux/macOS).";

    public void Initialize(IPickleContext context)
    {
        context.Panels.Register(ServicesPanel.Descriptor);
        context.Commands.Register(new ServicesCommand());
        if (!OperatingSystem.IsWindows())
        {
            // The Task Scheduler's counterpart: same panel id and key (Alt+S), same pk schedule.
            context.Panels.Register(TimersPanel.Descriptor);
            context.Commands.Register(new TimersCommand("timers"));
            context.Commands.Register(new TimersCommand("schedule"));
        }

        context.Panels.Register(PackagesPanel.Descriptor);
        context.Commands.Register(new PackagesCommand());
        context.Panels.Register(LogsPanel.Descriptor);
        context.Commands.Register(new LogsCommand());
        context.Panels.Register(EnvironmentPanel.Descriptor);
        context.Commands.Register(new HostsCommand());
        context.Commands.Register(new EnvCommand(pathOnly: false));
        context.Commands.Register(new EnvCommand(pathOnly: true));
    }
}
