using Pickle.Abstractions;

namespace Pickle.Tui.Panels.Admin;

/// <summary>Registers the administration panels and their <c>pk</c> commands: services, logs, packages, hosts/PATH/environment, timers.</summary>
public sealed class AdminPanelsPlugin : IPicklePlugin
{
    public string Id => "pickle.admin.panels";

    public string DisplayName => "Administration panels";

    public string Description => "Services (Alt+V), logs, packages, hosts/PATH/environment and timers.";

    public void Initialize(IPickleContext context)
    {
        context.Panels.Register(ServicesPanel.Descriptor);
        context.Commands.Register(new ServicesCommand());
    }
}
