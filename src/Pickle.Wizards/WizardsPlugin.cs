using Pickle.Abstractions;

namespace Pickle.Wizards;

/// <summary>
/// FOUNDATION PLACEHOLDER — workstream W9 loads the embedded Definitions/*.json plus the user's wizards folder
/// into IWizardRegistry and registers `pk wizard`.
/// </summary>
public sealed class WizardsPlugin : IPicklePlugin
{
    public string Id => "pickle.wizards";

    public string DisplayName => "Command wizards";

    public string Description => "Guided builders for curl, nmap, ffmpeg, git, docker, ssh, openssl and more.";

    public void Initialize(IPickleContext context)
    {
    }
}
