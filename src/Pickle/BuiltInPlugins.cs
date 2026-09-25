using Pickle.Abstractions;

namespace Pickle;

/// <summary>
/// Every built-in feature plugin, in load order. Services (Windows, wizard definitions) load before the
/// panels that consume them. Adding a built-in plugin = one line here.
/// </summary>
internal static class BuiltInPlugins
{
    public static IReadOnlyList<IPicklePlugin> Create() =>
    [
        new Windows.WindowsPlugin(),
        new Wizards.WizardsPlugin(),
        new Core.SystemMonitoring.SystemMonitorsPlugin(),
        new Network.NetworkToolsPlugin(),
        new Tui.TuiPlugin(),
        new Tui.Panels.Git.GitPanelPlugin(),
        new Tui.Panels.Windows.WindowsPanelsPlugin(),
        new Tui.Panels.Wizard.WizardPanelPlugin(),
        new Tui.Panels.SystemMonitoring.SystemPanelsPlugin(),
        new Tui.Panels.NetTools.NetToolsPanelPlugin(),
    ];
}
