using Pickle.Abstractions;
using Pickle.Tui.Panels.Admin.EnvEditor;

namespace Pickle.Tui.Panels.Admin;

/// <summary>Registers the administration panels and their <c>pk</c> commands: services, logs, packages, hosts/PATH/environment, timers.</summary>
public sealed class AdminPanelsPlugin : IPicklePlugin
{
    public string Id => "pickle.admin.panels";

    public string DisplayName => "Administration panels";

    public string Description => "Services (Alt+V), hosts/PATH/environment (Alt+O), logs, packages and timers.";

    public void Initialize(IPickleContext context)
    {
        context.Panels.Register(ServicesPanel.Descriptor);
        context.Commands.Register(new ServicesCommand());
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
