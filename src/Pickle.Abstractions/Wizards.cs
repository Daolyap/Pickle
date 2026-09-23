namespace Pickle.Abstractions;

/// <summary>
/// Declarative definition of an interactive command builder (wizards/*.json). The engine in Pickle.Wizards
/// turns (mode, values) into a command line and parses a command line back into (mode, values).
/// </summary>
public sealed class WizardDefinition
{
    /// <summary>Unique id, usually the tool name ("curl", "ffmpeg", "git").</summary>
    public string Id { get; set; } = string.Empty;

    public string Title { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;

    /// <summary>Executable to invoke, e.g. "curl" (on Windows "curl.exe" is tried to bypass the PowerShell alias).</summary>
    public string Command { get; set; } = string.Empty;

    /// <summary>Other command names that open this wizard with F2 (e.g. "curl.exe", "scp").</summary>
    public List<string> Aliases { get; set; } = [];

    /// <summary>winget package id used to offer installation when the tool is missing.</summary>
    public string? WingetId { get; set; }

    public string? Homepage { get; set; }

    /// <summary>Subcommand modes (git commit / git rebase, docker run / build). Empty = single mode using <see cref="Sections"/>.</summary>
    public List<WizardMode> Modes { get; set; } = [];

    public List<WizardSection> Sections { get; set; } = [];

    public List<WizardPreset> Presets { get; set; } = [];

    public bool WindowsOnly { get; set; }
}

public sealed class WizardMode
{
    public string Id { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;

    /// <summary>Tokens inserted after the command, e.g. ["commit"] or ["compose", "up"].</summary>
    public List<string> Subcommand { get; set; } = [];

    public List<WizardSection> Sections { get; set; } = [];
}

public sealed class WizardSection
{
    public string Title { get; set; } = string.Empty;
    public string? Description { get; set; }
    public List<WizardOption> Options { get; set; } = [];
}

public enum WizardOptionType
{
    /// <summary>Boolean switch: emits <see cref="WizardOption.Flag"/> when true.</summary>
    Flag,

    /// <summary>Free text value.</summary>
    Text,

    Number,

    /// <summary>File/directory path (UI offers the file picker).</summary>
    Path,

    /// <summary>One of <see cref="WizardOption.Choices"/>.</summary>
    Choice,

    /// <summary>Repeatable "key: value" / "key=value" pairs (e.g. curl -H).</summary>
    KeyValueList,

    /// <summary>Repeatable plain values (each emitted with the flag).</summary>
    List,

    /// <summary>Positional argument (no flag), ordered by <see cref="WizardOption.Position"/>.</summary>
    Positional,
}

public sealed class WizardOption
{
    public string Id { get; set; } = string.Empty;
    public string Label { get; set; } = string.Empty;
    public string? Description { get; set; }
    public WizardOptionType Type { get; set; } = WizardOptionType.Text;

    /// <summary>Primary flag as emitted, e.g. "-X" or "--request". Null for positionals.</summary>
    public string? Flag { get; set; }

    /// <summary>Alternative spellings accepted when parsing, e.g. ["--request"] for "-X".</summary>
    public List<string> FlagAliases { get; set; } = [];

    /// <summary>How a value attaches to its flag: "space" (-o file), "equals" (--out=file), "none" (-ofile).</summary>
    public string ValueStyle { get; set; } = "space";

    /// <summary>For composite values, e.g. "{local}:{host}:{remote}" for ssh -L. Sub-fields are separate UI inputs.</summary>
    public string? Template { get; set; }

    public List<WizardChoice> Choices { get; set; } = [];
    public string? Default { get; set; }
    public bool Required { get; set; }
    public string? Placeholder { get; set; }

    /// <summary>Regex the value must match.</summary>
    public string? Validation { get; set; }

    public int? Position { get; set; }

    /// <summary>Only shown/emitted when the referenced option id has a non-empty/true value.</summary>
    public string? DependsOn { get; set; }

    /// <summary>For KeyValueList: separator between key and value ("=" or ": ").</summary>
    public string KeyValueSeparator { get; set; } = "=";
}

public sealed class WizardChoice
{
    public string Value { get; set; } = string.Empty;
    public string? Label { get; set; }
    public string? Description { get; set; }
}

public sealed class WizardPreset
{
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string? Mode { get; set; }

    /// <summary>Option id → value. Lists use newline-separated values.</summary>
    public Dictionary<string, string> Values { get; set; } = [];
}

public interface IWizardRegistry
{
    void Register(WizardDefinition wizard);

    WizardDefinition? Get(string id);

    /// <summary>Find a wizard for a typed command name (matches Command and Aliases, case-insensitive, ignoring ".exe").</summary>
    WizardDefinition? FindForCommand(string commandName);

    IReadOnlyList<WizardDefinition> All { get; }
}
