using Pickle.Abstractions;

namespace Pickle.Tui.Panels.Wizard;

/// <summary>
/// Registers the "wizard" panel (argument = wizard id) and the F2 editor action, which opens the wizard for the first
/// command in the input line that has one (parsing the typed arguments into the form), or the wizard picker.
/// </summary>
public sealed class WizardPanelPlugin : IPicklePlugin
{
    public const string PanelId = "wizard";

    public string Id => "pickle.wizards.panel";

    public string DisplayName => "Command wizards (UI)";

    public string Description => "Interactive form for building complex commands with live preview.";

    public void Initialize(IPickleContext context)
    {
        context.Panels.Register(new PanelDescriptor
        {
            Id = PanelId,
            Title = "Command wizard",
            Description = "Build curl, git, docker, ffmpeg, ssh … commands with a form",
            CreateView = ctx => new WizardPanel(ctx),
        });

        context.KeyBindings.RegisterAction(
            EditorActionNames.OpenWizard,
            "Open the command wizard for the typed command (or pick one)",
            (buffer, _) =>
            {
                var wizard = string.IsNullOrWhiteSpace(buffer.Text) ? null : WizardPanel.FindInInput(context.Wizards, buffer.Text);
                buffer.ShowPanel(PanelId, wizard?.Id);
                return ValueTask.CompletedTask;
            });
    }
}
