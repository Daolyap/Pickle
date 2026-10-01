using Pickle.Abstractions;
using Pickle.Abstractions.Services;

namespace Pickle.Modules.Nmap;

public sealed class NmapModule : IPicklePlugin
{
    public const string ModuleId = "nmap";

    public static ModuleDescriptor Descriptor { get; } = new()
    {
        Id = ModuleId,
        Name = "nmap",
        Description = "Scan builder and results browser: panel and pk nmap (needs nmap installed)",
        Create = () => new NmapModule(),
        Tools = ["nmap"],
        Provides = ["pk nmap", "panel: nmap (Alt+Shift+N)"],
    };

    string IPicklePlugin.Id => "pickle." + ModuleId;

    public string DisplayName => Descriptor.Name;

    public string Description => Descriptor.Description;

    public void Initialize(IPickleContext context)
    {
        context.Panels.Register(NmapPanel.Descriptor);
        context.Commands.Register(new NmapCommand());
    }
}
