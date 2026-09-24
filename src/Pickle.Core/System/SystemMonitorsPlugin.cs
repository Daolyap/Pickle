using Pickle.Abstractions;
using Pickle.Abstractions.Services;

// The namespace avoids a "System" segment: Pickle.Core.System would shadow the BCL System namespace in every
// Pickle.Core.* file that writes System.Something.
namespace Pickle.Core.SystemMonitoring;

/// <summary>Registers the process, network and disk monitors used by the Processes, Network and Disks panels.</summary>
public sealed class SystemMonitorsPlugin : IPicklePlugin
{
    public string Id => "pickle.system.monitors";

    public string DisplayName => "System monitors";

    public string Description => "Process, network and disk data for the system panels.";

    public void Initialize(IPickleContext context)
    {
        context.Services.Add<IProcessMonitor>(new ProcessMonitor());
        context.Services.Add<INetworkMonitor>(new NetworkMonitor());
        context.Services.Add<IDiskMonitor>(new DiskMonitor());
    }
}
