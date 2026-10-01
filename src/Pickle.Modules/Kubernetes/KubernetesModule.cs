using Pickle.Abstractions;
using Pickle.Abstractions.Services;

namespace Pickle.Modules.Kubernetes;

public sealed class KubernetesModule : IPicklePlugin
{
    public const string ModuleId = "kubernetes";

    public static ModuleDescriptor Descriptor { get; } = new()
    {
        Id = ModuleId,
        Name = "Kubernetes",
        Description = "Contexts, namespaces, pods, deployments and services: panel and pk k8s",
        Create = () => new KubernetesModule(),
        Tools = ["kubectl"],
        Provides = ["pk k8s", "panel: kubernetes (Alt+Shift+K)"],
    };

    string IPicklePlugin.Id => "pickle." + ModuleId;

    public string DisplayName => Descriptor.Name;

    public string Description => Descriptor.Description;

    public void Initialize(IPickleContext context)
    {
        context.Panels.Register(KubePanel.Descriptor);
        context.Commands.Register(new KubeCommand());
    }
}
