using Pickle.Abstractions;

namespace Pickle.Windows;

/// <summary>
/// FOUNDATION PLACEHOLDER — workstream W8 registers IWingetService, IWindowsUpdateService,
/// ITaskSchedulerService and IElevationBroker, plus `pk winget|upgrade|update|schedule` commands.
/// Workstream W3 adds `pk terminal` (Windows Terminal fragment) in Pickle.Windows/Terminal.
/// </summary>
public sealed class WindowsPlugin : IPicklePlugin
{
    public string Id => "pickle.windows";

    public string DisplayName => "Windows integrations";

    public string Description => "winget, Windows Update, Task Scheduler, elevation broker, Windows Terminal profile.";

    public void Initialize(IPickleContext context)
    {
    }
}
