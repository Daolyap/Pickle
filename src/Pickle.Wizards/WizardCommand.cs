namespace Pickle.Wizards;

/// <summary>A built command. <see cref="Arguments"/> are the unquoted arguments; <see cref="CommandLine"/> is PowerShell source.</summary>
public sealed record WizardCommand(
    string Executable,
    IReadOnlyList<string> Arguments,
    string CommandLine,
    IReadOnlyList<string> Errors,
    IReadOnlyList<string> Warnings)
{
    public bool IsValid => Errors.Count == 0;
}

/// <summary>
/// A command line parsed back into wizard values. <see cref="UnknownTokens"/> hold the original PowerShell source of
/// arguments the wizard doesn't model; passing them back to <see cref="WizardEngine.Build"/> keeps them.
/// </summary>
public sealed record WizardParseResult(
    string? ModeId,
    IReadOnlyDictionary<string, string> Values,
    IReadOnlyList<string> UnknownTokens)
{
    /// <summary>Where the parsed command sits in the input (so a pipeline around it can be kept).</summary>
    public int Start { get; init; }

    public int Length { get; init; }

    public bool Matched { get; init; } = true;

    public void Deconstruct(out string? modeId, out IReadOnlyDictionary<string, string> values, out IReadOnlyList<string> unknownTokens)
    {
        modeId = ModeId;
        values = Values;
        unknownTokens = UnknownTokens;
    }
}
