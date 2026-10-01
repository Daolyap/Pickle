using Pickle.Abstractions.Services;
using Pickle.Modules.Docker;
using Pickle.Modules.Example;
using Pickle.Modules.GitHub;
using Pickle.Modules.Kubernetes;
using Pickle.Modules.Nmap;

namespace Pickle.Modules;

/// <summary>
/// Every optional module, whether or not it is turned on. A module costs nothing until selected: <c>Create</c> only runs
/// for enabled ones (see <c>docs/modules.md</c> to add one).
/// </summary>
public static class OptionalModules
{
    public static IReadOnlyList<ModuleDescriptor> All { get; } =
    [
        NmapModule.Descriptor,
        DockerModule.Descriptor,
        KubernetesModule.Descriptor,
        GitHubModule.Descriptor,
        ExampleModule.Descriptor,
    ];
}
