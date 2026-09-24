using Pickle.Abstractions;

namespace Pickle.Wizards;

public sealed record WizardInfo(string Id, string Title, string Command, string Modes, int Presets, bool WindowsOnly, string Description);

public sealed record WizardPresetInfo(string Name, string? Mode, string CommandLine, string Description);

/// <summary><c>pk wizard</c>: list wizards, open one in the panel, or print a command line.</summary>
internal sealed class WizardPickleCommand : IPickleCommand
{
    public string Name => "wizard";

    public string Description => "List command wizards, open one, or print a preset's command line.";

    public string Usage => "pk wizard [list] | pk wizard <id> [--preset <name>] [--mode <id>] [--print] | pk wizard <id> --presets";

    public ValueTask<int> ExecuteAsync(PickleCommandContext context, IReadOnlyList<string> args, CancellationToken cancellationToken)
    {
        var wizards = context.Pickle.Wizards;
        if (args.Count == 0 || args[0].Equals("list", StringComparison.OrdinalIgnoreCase))
        {
            foreach (var w in wizards.All)
            {
                context.WriteObject(new WizardInfo(
                    w.Id, w.Title, w.Command, string.Join(", ", w.Modes.Select(m => m.Id)), w.Presets.Count, w.WindowsOnly, w.Description));
            }

            return ValueTask.FromResult(0);
        }

        var definition = wizards.Get(args[0]) ?? wizards.FindForCommand(args[0]);
        if (definition is null)
        {
            context.WriteError($"No wizard '{args[0]}'. Run 'pk wizard list'.");
            return ValueTask.FromResult(1);
        }

        string? presetName = null;
        string? modeId = null;
        var print = false;
        var listPresets = false;
        for (var i = 1; i < args.Count; i++)
        {
            switch (args[i].ToLowerInvariant())
            {
                case "--print" or "-p":
                    print = true;
                    break;
                case "--presets":
                    listPresets = true;
                    break;
                case "--preset" when i + 1 < args.Count:
                    presetName = args[++i];
                    break;
                case "--mode" when i + 1 < args.Count:
                    modeId = args[++i];
                    break;
                default:
                    context.WriteError($"Unknown argument '{args[i]}'. Usage: {Usage}");
                    return ValueTask.FromResult(2);
            }
        }

        if (listPresets)
        {
            foreach (var p in definition.Presets)
            {
                context.WriteObject(new WizardPresetInfo(p.Name, p.Mode, WizardEngine.Build(definition, p.Mode, p.Values).CommandLine, p.Description));
            }

            return ValueTask.FromResult(0);
        }

        WizardPreset? preset = null;
        if (presetName is not null)
        {
            preset = definition.Presets.FirstOrDefault(p => p.Name.Equals(presetName, StringComparison.OrdinalIgnoreCase))
                ?? (int.TryParse(presetName, out var n) && n >= 1 && n <= definition.Presets.Count ? definition.Presets[n - 1] : null);
            if (preset is null)
            {
                context.WriteError($"Wizard '{definition.Id}' has no preset '{presetName}'. Run 'pk wizard {definition.Id} --presets'.");
                return ValueTask.FromResult(1);
            }
        }

        var command = WizardEngine.Build(definition, preset?.Mode ?? modeId, preset?.Values ?? []);
        if (print)
        {
            context.WriteObject(command.CommandLine);
            return ValueTask.FromResult(0);
        }

        var host = context.Pickle.Services.Get<IPanelHost>();
        if (host is null || !context.Interactive)
        {
            context.WriteError("The wizard form needs an interactive terminal; use --print to get the command line.");
            return ValueTask.FromResult(1);
        }

        var result = host.Show("wizard", definition.Id, preset is null && modeId is null ? null : command.CommandLine);
        switch (result?.Kind)
        {
            case PanelResultKind.RunCommand:
                context.Pickle.Shell.SubmitCommand(result.Text);
                break;
            case PanelResultKind.ReplaceInput:
                context.Pickle.Shell.ReplaceInput(result.Text);
                break;
            case PanelResultKind.InsertText:
                context.Pickle.Shell.InsertText(result.Text);
                break;
        }

        return ValueTask.FromResult(0);
    }
}
