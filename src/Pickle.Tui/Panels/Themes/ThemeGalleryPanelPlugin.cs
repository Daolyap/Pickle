using Pickle.Abstractions;

namespace Pickle.Tui.Panels.Themes;

/// <summary>Registers the theme gallery (Alt+E, <c>pk theme gallery</c>).</summary>
public sealed class ThemeGalleryPanelPlugin : IPicklePlugin
{
    public string Id => "pickle.themes";

    public string DisplayName => "Theme gallery";

    public string Description => "Browse every theme with a live preview in its own colors.";

    public void Initialize(IPickleContext context) => context.Panels.Register(new PanelDescriptor
    {
        Id = ThemeGalleryPanel.PanelId,
        Title = "Themes",
        Description = "Theme gallery: every theme previewed in its own colors, animated ones moving; Enter applies",
        DefaultKey = "Alt+E",
        CreateView = ctx => new ThemeGalleryPanel(ctx),
    });
}
