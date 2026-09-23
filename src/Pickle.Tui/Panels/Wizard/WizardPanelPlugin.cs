using Pickle.Abstractions;

namespace Pickle.Tui.Panels.Wizard;

/// <summary>FOUNDATION PLACEHOLDER — workstream W9 implements the wizard panel (F2) on Pickle.Wizards.</summary>
public sealed class WizardPanelPlugin : IPicklePlugin
{
    public string Id => "pickle.wizards.panel";

    public string DisplayName => "Command wizards (UI)";

    public string Description => "Interactive form for building complex commands with live preview.";

    public void Initialize(IPickleContext context)
    {
    }
}
