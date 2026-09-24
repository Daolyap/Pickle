using System.Text.RegularExpressions;
using Pickle.Abstractions;

namespace Pickle.Wizards;

/// <summary>Structural checks for a wizard definition (used at load time for user files and by the tests).</summary>
public static partial class WizardValidator
{
    private static readonly string[] ValueStyles = ["space", "equals", "none"];

    public static IReadOnlyList<string> Validate(WizardDefinition definition)
    {
        var problems = new List<string>();
        if (!IdRegex().IsMatch(definition.Id))
        {
            problems.Add($"id '{definition.Id}' must be lowercase letters, digits, '.', '_' or '-'.");
        }

        if (string.IsNullOrWhiteSpace(definition.Title))
        {
            problems.Add("title is required.");
        }

        if (string.IsNullOrWhiteSpace(definition.Command))
        {
            problems.Add("command is required.");
        }

        if (definition.WingetId is { } winget && !WingetIdRegex().IsMatch(winget))
        {
            problems.Add($"wingetId '{winget}' doesn't look like a winget package id (Publisher.Name).");
        }

        foreach (var duplicate in definition.Modes.GroupBy(m => m.Id, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1))
        {
            problems.Add($"mode id '{duplicate.Key}' is used more than once.");
        }

        foreach (var option in definition.Sections.SelectMany(s => s.Options).Where(o => definition.Modes.Count > 0 && o.IsPositional()))
        {
            problems.Add($"option '{option.Id}': global options (top-level sections of a wizard with modes) can't be positional.");
        }

        if (definition.Modes.Count == 0)
        {
            ValidateScope(definition, null, problems);
        }
        else
        {
            foreach (var mode in definition.Modes)
            {
                if (string.IsNullOrWhiteSpace(mode.Id) || string.IsNullOrWhiteSpace(mode.Title))
                {
                    problems.Add("every mode needs an id and a title.");
                }

                ValidateScope(definition, mode, problems);
            }
        }

        foreach (var duplicate in definition.Presets.GroupBy(p => p.Name, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1))
        {
            problems.Add($"preset name '{duplicate.Key}' is used more than once.");
        }

        foreach (var preset in definition.Presets)
        {
            ValidatePreset(definition, preset, problems);
        }

        return problems;
    }

    private static void ValidateScope(WizardDefinition definition, WizardMode? mode, List<string> problems)
    {
        var where = mode is null ? string.Empty : $" (mode '{mode.Id}')";
        var options = WizardSchema.GetOptions(definition, mode).Select(o => o.Option).ToList();

        foreach (var duplicate in options.GroupBy(o => o.Id, StringComparer.Ordinal).Where(g => g.Count() > 1))
        {
            problems.Add($"option id '{duplicate.Key}' is used more than once{where}.");
        }

        // Global options are parsed before the subcommand and mode options after it, so they may share a spelling.
        var scoped = WizardSchema.GetOptions(definition, mode);
        foreach (var group in scoped.Where(o => !o.Option.IsPositional()).GroupBy(o => o.IsGlobal))
        {
            var flags = group.Select(o => o.Option)
                .SelectMany(o => o.IsFlagChoice() ? o.Choices.Select(c => c.Value) : new[] { o.Flag }.Concat(o.FlagAliases))
                .OfType<string>();
            foreach (var duplicate in flags.GroupBy(f => f, StringComparer.Ordinal).Where(g => g.Count() > 1))
            {
                problems.Add($"flag '{duplicate.Key}' is used by more than one option{where}.");
            }
        }

        var positionals = options.Where(o => o.IsPositional()).ToList();
        foreach (var duplicate in positionals.GroupBy(o => o.Position).Where(g => g.Count() > 1))
        {
            problems.Add($"position {duplicate.Key} is used more than once{where}.");
        }

        var unflagged = positionals.Where(o => o.Flag is null).OrderBy(o => o.Position).ToList();
        if (unflagged.Count(o => o.IsMultiValued()) > 1)
        {
            problems.Add($"only one variadic (list) positional is allowed{where}.");
        }

        var variadicIndex = unflagged.FindIndex(o => o.IsMultiValued());
        foreach (var raw in unflagged.Where(o => o.IsRaw()))
        {
            if (raw != unflagged[^1] || (variadicIndex >= 0 && variadicIndex < unflagged.IndexOf(raw)))
            {
                problems.Add($"option '{raw.Id}': a raw positional takes the rest of the line, so it must be the last positional and follow no list positional.");
            }
        }

        foreach (var option in options)
        {
            ValidateOption(option, options, problems, where);
        }
    }

    private static void ValidateOption(WizardOption option, List<WizardOption> scope, List<string> problems, string where)
    {
        var name = $"option '{option.Id}'{where}";
        if (!OptionIdRegex().IsMatch(option.Id))
        {
            problems.Add($"{name}: id must start with a letter and contain only letters, digits and '_'.");
        }

        if (string.IsNullOrWhiteSpace(option.Label))
        {
            problems.Add($"{name}: label is required.");
        }

        if (!ValueStyles.Contains(option.ValueStyle, StringComparer.OrdinalIgnoreCase))
        {
            problems.Add($"{name}: valueStyle must be space, equals or none.");
        }

        foreach (var flag in new[] { option.Flag }.Concat(option.FlagAliases).OfType<string>())
        {
            if (flag.Length == 0 || PowerShellQuoting.NeedsQuoting(flag, allowLeadingDash: true) || flag.Contains('='))
            {
                problems.Add($"{name}: flag '{flag}' must be a plain word that PowerShell passes through unquoted.");
            }
        }

        if (option.Type == WizardOptionType.Flag && (option.Flag is null || option.Position is not null))
        {
            problems.Add($"{name}: a flag needs 'flag' and no position.");
        }

        if (option.Type == WizardOptionType.Positional && option.Position is null)
        {
            problems.Add($"{name}: a positional needs a position.");
        }

        if (!option.IsPositional() && option.Type != WizardOptionType.Flag && option.Flag is null && !option.IsFlagChoice())
        {
            problems.Add($"{name}: options without a flag must have a position.");
        }

        if (option.IsFlagChoice())
        {
            foreach (var choice in option.Choices.Where(c => PowerShellQuoting.NeedsQuoting(c.Value, allowLeadingDash: true)))
            {
                problems.Add($"{name}: flag-valued choice '{choice.Value}' must be a plain word.");
            }
        }

        if (option.Type == WizardOptionType.Choice)
        {
            if (option.Choices.Count == 0)
            {
                problems.Add($"{name}: a choice needs choices.");
            }

            foreach (var duplicate in option.Choices.GroupBy(c => c.Value, StringComparer.Ordinal).Where(g => g.Count() > 1))
            {
                problems.Add($"{name}: choice '{duplicate.Key}' is listed more than once.");
            }

            if (option.Default is { } def && option.Choices.All(c => c.Value != def))
            {
                problems.Add($"{name}: default '{def}' is not one of the choices.");
            }
        }

        if (option.Template is { } templateText)
        {
            try
            {
                var template = WizardTemplate.Parse(templateText);
                if (template.Placeholders.Count == 0)
                {
                    problems.Add($"{name}: template has no {{placeholders}}.");
                }

                foreach (var duplicate in template.Placeholders.GroupBy(p => p).Where(g => g.Count() > 1))
                {
                    problems.Add($"{name}: template placeholder '{duplicate.Key}' is used more than once.");
                }

                if (option.IsMultiValued() || option.Type is WizardOptionType.Flag or WizardOptionType.Choice)
                {
                    problems.Add($"{name}: templates are only supported on text, number, path and positional options.");
                }
            }
            catch (FormatException ex)
            {
                problems.Add($"{name}: {ex.Message}");
            }
        }

        if (option.IsRaw() && !option.IsPositional() && !option.ValueStyle.Equals("space", StringComparison.OrdinalIgnoreCase))
        {
            problems.Add($"{name}: raw options must use valueStyle 'space'.");
        }

        if (option.IsRaw() && option.IsPositional() && (option.IsMultiValued() || option.Template is not null))
        {
            problems.Add($"{name}: a raw positional is free PowerShell argument text (no list/template).");
        }

        if (option.Validation is { } pattern)
        {
            try
            {
                _ = new Regex(pattern);
            }
            catch (ArgumentException ex)
            {
                problems.Add($"{name}: invalid validation regex ({ex.Message}).");
            }
        }

        if (option.DependsOn is { } dependsOn)
        {
            var (id, _, _) = WizardEngine.ParseDependsOn(dependsOn);
            if (id == option.Id || scope.All(o => o.Id != id))
            {
                problems.Add($"{name}: dependsOn references unknown option '{id}'.");
            }
        }

        if (option.Type == WizardOptionType.KeyValueList && option.KeyValueSeparator.Length == 0)
        {
            problems.Add($"{name}: keyValueSeparator must not be empty.");
        }
    }

    private static void ValidatePreset(WizardDefinition definition, WizardPreset preset, List<string> problems)
    {
        var name = $"preset '{preset.Name}'";
        if (string.IsNullOrWhiteSpace(preset.Name))
        {
            problems.Add("every preset needs a name.");
        }

        WizardMode? mode = null;
        if (definition.Modes.Count > 0)
        {
            mode = definition.Modes.FirstOrDefault(m => string.Equals(m.Id, preset.Mode, StringComparison.OrdinalIgnoreCase));
            if (mode is null)
            {
                problems.Add($"{name}: mode '{preset.Mode}' doesn't exist.");
                return;
            }
        }
        else if (preset.Mode is not null)
        {
            problems.Add($"{name}: this wizard has no modes.");
        }

        var options = WizardSchema.GetOptions(definition, mode).Select(o => o.Option)
            .GroupBy(o => o.Id, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
        foreach (var (key, value) in preset.Values)
        {
            var dot = key.IndexOf('.', StringComparison.Ordinal);
            var id = dot < 0 ? key : key[..dot];
            if (!options.TryGetValue(id, out var option))
            {
                problems.Add($"{name}: unknown option '{key}'.");
                continue;
            }

            if (dot >= 0 && (option.GetTemplate() is not { } template || !template.Placeholders.Contains(key[(dot + 1)..])))
            {
                problems.Add($"{name}: '{key}' is not a template field of '{id}'.");
            }
            else if (dot < 0 && option.Template is not null)
            {
                problems.Add($"{name}: set template option '{id}' through its fields ('{id}.<field>').");
            }
            else if (option.Type == WizardOptionType.Flag && value is not ("true" or "false"))
            {
                problems.Add($"{name}: flag '{id}' must be \"true\" or \"false\".");
            }
        }
    }

    [GeneratedRegex(@"^[a-z0-9][a-z0-9._-]*$")]
    private static partial Regex IdRegex();

    [GeneratedRegex(@"^[A-Za-z][A-Za-z0-9_]*$")]
    private static partial Regex OptionIdRegex();

    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9_+-]*(\.[A-Za-z0-9_+-]+)+$")]
    private static partial Regex WingetIdRegex();
}
