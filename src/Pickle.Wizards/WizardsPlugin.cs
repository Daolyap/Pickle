using Pickle.Abstractions;

namespace Pickle.Wizards;

/// <summary>
/// Loads the built-in Definitions/*.json plus the user's wizards folder (a user file replaces a built-in one with the
/// same id) into IWizardRegistry and registers <c>pk wizard</c>. The form UI lives in Pickle.Tui (panel "wizard").
/// </summary>
public sealed class WizardsPlugin : IPicklePlugin
{
    private const string LogCategory = "wizards";

    public string Id => "pickle.wizards";

    public string DisplayName => "Command wizards";

    public string Description => "Guided builders for curl, nmap, ffmpeg, git, docker, ssh, openssl and more.";

    public void Initialize(IPickleContext context)
    {
        foreach (var definition in WizardLoader.LoadEmbedded((name, ex) => context.Log.Error(LogCategory, $"Built-in wizard {name} failed to load", ex)))
        {
            context.Wizards.Register(definition);
        }

        var userFiles = WizardLoader.LoadDirectory(
            context.Paths.WizardsDir,
            (path, ex) => context.Log.Error(LogCategory, $"Wizard file {path} is invalid: {ex.Message}", ex));
        foreach (var (path, definition) in userFiles)
        {
            if (string.IsNullOrWhiteSpace(definition.Id) || string.IsNullOrWhiteSpace(definition.Command))
            {
                context.Log.Error(LogCategory, $"Wizard file {path} needs an id and a command; skipped.");
                continue;
            }

            foreach (var problem in WizardValidator.Validate(definition))
            {
                context.Log.Warn(LogCategory, $"{Path.GetFileName(path)}: {problem}");
            }

            context.Wizards.Register(definition);
        }

        context.Commands.Register(new WizardPickleCommand());
    }
}
