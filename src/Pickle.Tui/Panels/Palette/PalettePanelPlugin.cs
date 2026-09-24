using Pickle.Abstractions;

namespace Pickle.Tui.Panels.Palette;

/// <summary>Registers the command palette panel and the <see cref="EditorActionNames.CommandPalette"/> action (F1 / Ctrl+P).</summary>
public sealed class PalettePanelPlugin : IPicklePlugin
{
    public const string PanelId = "palette";

    public string Id => "pickle.palette";

    public string DisplayName => "Command palette";

    public string Description => "Search and run panels, wizards, commands, editor actions, themes and settings.";

    public void Initialize(IPickleContext context)
    {
        var state = new PaletteState();
        context.Panels.Register(new PanelDescriptor
        {
            Id = PanelId,
            Title = "Command palette",
            Description = "Search panels, wizards, commands, actions, themes and settings",
            CreateView = ctx => new PalettePanel(ctx, state),
        });

        context.KeyBindings.RegisterAction(EditorActionNames.CommandPalette, "Open the command palette", async (buffer, cancellationToken) =>
        {
            state.PendingAction = null;
            buffer.ShowPanel(PanelId);
            if (state.TakePendingAction() is { } action)
            {
                await action.Handler(buffer, cancellationToken);
            }
        });
    }
}
