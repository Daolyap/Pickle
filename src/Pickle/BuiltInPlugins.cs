using Pickle.Abstractions;
using Pickle.Abstractions.Services;

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
        new Tui.Panels.Dashboard.DashboardPanelPlugin(),
        new Tui.Panels.Themes.ThemeGalleryPanelPlugin(),
        new Tui.Panels.Ssh.SshPanelPlugin(),
    ];

    /// <summary>The optional modules (Docker, Kubernetes, nmap, …); only those selected at install or with <c>pk module</c> are loaded.</summary>
    public static IReadOnlyList<ModuleDescriptor> OptionalModules() => Modules.OptionalModules.All;
}
