using Pickle.Abstractions;
using Pickle.Abstractions.Services;

namespace Pickle.Modules.Docker;

/// <summary>Docker and Podman: containers, images, volumes and compose projects as a panel and <c>pk docker</c>.</summary>
public sealed class DockerModule : IPicklePlugin
{
    public const string ModuleId = "docker";

    public static ModuleDescriptor Descriptor { get; } = new()
    {
        Id = ModuleId,
        Name = "Docker / Podman",
        Description = "Containers, images, volumes and compose projects: panel and pk docker",
        Create = () => new DockerModule(),
        Tools = ["docker", "podman"],
        Provides = ["pk docker", "panel: docker (Alt+Shift+D)"],
    };

    string IPicklePlugin.Id => "pickle." + ModuleId;

    public string DisplayName => Descriptor.Name;

    public string Description => Descriptor.Description;

    public void Initialize(IPickleContext context)
    {
        context.Panels.Register(DockerPanel.Descriptor);
        context.Commands.Register(new DockerCommand());
    }
}
