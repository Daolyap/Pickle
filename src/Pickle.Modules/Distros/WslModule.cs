using Pickle.Abstractions;
using Pickle.Abstractions.Services;

namespace Pickle.Modules.Distros;

/// <summary>WSL distributions on Windows and Distrobox, Toolbx, LXC and Incus containers on Linux (and macOS, where those tools are installed): panel and <c>pk wsl</c>.</summary>
public sealed class WslModule : IPicklePlugin
{
    public const string ModuleId = "wsl";

    public static ModuleDescriptor Descriptor { get; } = new()
    {
        Id = ModuleId,
        Name = "WSL & Linux containers",
        Description = "WSL distributions (Windows) and Distrobox, Toolbx, LXC and Incus containers (Linux): list, start, stop, enter, export, remove",
        Create = () => new WslModule(),
        Tools = ["wsl.exe", "distrobox", "toolbox + podman", "lxc", "incus"],
        Provides = ["pk wsl", "pk distros", "panel: distros (Alt+Shift+W)"],
    };

    string IPicklePlugin.Id => "pickle." + ModuleId;

    public string DisplayName => Descriptor.Name;

    public string Description => Descriptor.Description;

    public void Initialize(IPickleContext context)
    {
        Func<IProgramRunner> runner = () => context.Services.Require<IProgramRunner>();
        context.Services.Add(new DistroService(
            [new WslBackend(runner), new DistroboxBackend(runner), new ToolboxBackend(runner), new LxcBackend(runner, "lxc"), new LxcBackend(runner, "incus")],
            runner));
        context.Panels.Register(DistroPanel.Descriptor);
        context.Commands.Register(new DistroCommand("wsl"));
        context.Commands.Register(new DistroCommand("distros"));
    }
}
