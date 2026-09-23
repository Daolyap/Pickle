using Pickle.Abstractions;

namespace Pickle.Tui.Panels.Windows;

/// <summary>FOUNDATION PLACEHOLDER — workstream W8 implements the Winget (Alt+W), Updates (Alt+U) and Scheduler (Alt+S) panels.</summary>
public sealed class WindowsPanelsPlugin : IPicklePlugin
{
    public string Id => "pickle.windows.panels";

    public string DisplayName => "Windows panels";

    public string Description => "winget dashboard, Windows Update and Task Scheduler panels.";

    public void Initialize(IPickleContext context)
    {
    }
}
