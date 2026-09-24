using Pickle.Abstractions;

namespace Pickle.Tui.Panels.Settings;

/// <summary>Registers the settings panel (Alt+,).</summary>
public sealed class SettingsPanelPlugin : IPicklePlugin
{
    public const string PanelId = "settings";

    public string Id => "pickle.settings";

    public string DisplayName => "Settings";

    public string Description => "Edit config.json: theme, editor, history, prompt, sync, key bindings and more.";

    public void Initialize(IPickleContext context) =>
        context.Panels.Register(new PanelDescriptor
        {
            Id = PanelId,
            Title = "Settings",
            Description = "Theme, editor, history, prompt, sync and key bindings",
            DefaultKey = "Alt+,",
            CreateView = ctx => new SettingsPanel(ctx),
        });
}
