using System.Runtime.CompilerServices;
using Pickle.Abstractions;

namespace Pickle.Wizards;

/// <summary>
/// Schema helpers plus local stand-ins for properties not (yet) on the Abstractions models: an option's
/// <c>raw</c> flag (value is PowerShell source, not a literal) and <c>warning</c> texts on options and choices.
/// <see cref="WizardLoader"/> reads them from the JSON; code-built definitions can set them with the Set* methods.
/// </summary>
public static class WizardSchema
{
    private static readonly ConditionalWeakTable<WizardOption, OptionExtras> OptionExtrasTable = new();
    private static readonly ConditionalWeakTable<WizardChoice, string> ChoiceWarnings = new();

    public static bool IsRaw(this WizardOption option) =>
        OptionExtrasTable.TryGetValue(option, out var extras) && extras.Raw;

    public static string? GetWarning(this WizardOption option) =>
        OptionExtrasTable.TryGetValue(option, out var extras) ? extras.Warning : null;

    public static string? GetWarning(this WizardChoice choice) =>
        ChoiceWarnings.TryGetValue(choice, out var warning) ? warning : null;

    public static WizardOption SetRaw(this WizardOption option, bool raw = true)
    {
        OptionExtrasTable.GetOrCreateValue(option).Raw = raw;
        return option;
    }

    public static WizardOption SetWarning(this WizardOption option, string? warning)
    {
        OptionExtrasTable.GetOrCreateValue(option).Warning = string.IsNullOrWhiteSpace(warning) ? null : warning;
        return option;
    }

    public static WizardChoice SetWarning(this WizardChoice choice, string? warning)
    {
        if (string.IsNullOrWhiteSpace(warning))
        {
            ChoiceWarnings.Remove(choice);
        }
        else
        {
            ChoiceWarnings.AddOrUpdate(choice, warning);
        }

        return choice;
    }

    /// <summary>Positionals are Type=Positional or any option with a Position (List = variadic, Path = file picker).</summary>
    public static bool IsPositional(this WizardOption option) =>
        option.Type == WizardOptionType.Positional || option.Position is not null;

    public static bool IsMultiValued(this WizardOption option) =>
        option.Type is WizardOptionType.List or WizardOptionType.KeyValueList;

    /// <summary>A choice without a flag whose values are the flags themselves (e.g. git reset --soft/--mixed/--hard).</summary>
    public static bool IsFlagChoice(this WizardOption option) =>
        option.Type == WizardOptionType.Choice && option.Flag is null && !option.IsPositional();

    /// <summary>Flags written without a leading dash or slash, netsh style (<c>listenport=8080</c>).</summary>
    public static bool IsBareFlag(string flag) => flag.Length > 0 && flag[0] is not '-' and not '/';

    public static WizardMode? ResolveMode(WizardDefinition definition, string? modeId)
    {
        if (definition.Modes.Count == 0)
        {
            return null;
        }

        return definition.Modes.FirstOrDefault(m => string.Equals(m.Id, modeId, StringComparison.OrdinalIgnoreCase))
            ?? definition.Modes[0];
    }

    /// <summary>
    /// Every option in scope for a mode. With modes, the definition-level sections are global options that are
    /// emitted before the subcommand (<c>adb -s serial shell</c>, <c>git -C dir log</c>).
    /// </summary>
    public static IReadOnlyList<ScopedOption> GetOptions(WizardDefinition definition, WizardMode? mode)
    {
        var list = new List<ScopedOption>();
        foreach (var section in definition.Sections)
        {
            foreach (var option in section.Options)
            {
                list.Add(new ScopedOption(option, section, IsGlobal: mode is not null));
            }
        }

        if (mode is not null)
        {
            foreach (var section in mode.Sections)
            {
                foreach (var option in section.Options)
                {
                    list.Add(new ScopedOption(option, section, IsGlobal: false));
                }
            }
        }

        return list;
    }

    public static IReadOnlyList<ScopedOption> GetOptions(WizardDefinition definition, string? modeId) =>
        GetOptions(definition, ResolveMode(definition, modeId));

    public static WizardTemplate? GetTemplate(this WizardOption option) =>
        string.IsNullOrEmpty(option.Template) ? null : WizardTemplate.Get(option.Template);

    private sealed class OptionExtras
    {
        public bool Raw { get; set; }

        public string? Warning { get; set; }
    }
}

public sealed record ScopedOption(WizardOption Option, WizardSection Section, bool IsGlobal);
