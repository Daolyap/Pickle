using System.Collections;
using System.Management.Automation;
using System.Management.Automation.Language;
using Pickle.Abstractions;

namespace Pickle.Core.Cmdlets;

/// <summary>One row of <c>Get-PickleTheme -ListAvailable</c>.</summary>
public sealed record PickleThemeInfo(string Name, string? Description, bool IsCurrent, string Source, string? Path);

/// <summary><c>Get-PickleTheme</c> (current), <c>-Name x</c> (a theme object), <c>-ListAvailable</c> (summaries).</summary>
[Cmdlet(VerbsCommon.Get, "PickleTheme", DefaultParameterSetName = "Current")]
[OutputType(typeof(Theme), typeof(PickleThemeInfo))]
public sealed class GetPickleThemeCmdlet : PickleCmdlet
{
    [Parameter(Position = 0, ParameterSetName = "Name", Mandatory = true)]
    [ArgumentCompleter(typeof(PickleThemeNameCompleter))]
    public string Name { get; set; } = string.Empty;

    [Parameter(ParameterSetName = "List", Mandatory = true)]
    public SwitchParameter ListAvailable { get; set; }

    protected override void EndProcessing()
    {
        var runtime = Runtime;
        var themes = runtime.ThemeProvider;
        if (ListAvailable)
        {
            foreach (var name in themes.Available)
            {
                var file = themes.UserThemeFile(name);
                WriteObject(new PickleThemeInfo(
                    name,
                    themes.Load(name)?.Description,
                    name.Equals(themes.Current.Name, StringComparison.OrdinalIgnoreCase),
                    file is null ? "BuiltIn" : "User",
                    file));
            }

            return;
        }

        if (ParameterSetName == "Name")
        {
            if (themes.Load(Name) is { } theme)
            {
                WriteObject(theme);
            }
            else
            {
                WriteError(new ErrorRecord(
                    new ItemNotFoundException($"Theme '{Name}' not found. Available: {string.Join(", ", themes.Available)}"),
                    "PickleThemeNotFound",
                    ErrorCategory.ObjectNotFound,
                    Name));
            }

            return;
        }

        WriteObject(themes.Current);
    }
}

/// <summary><c>Set-PickleTheme -Name x</c>: switch themes and persist the choice.</summary>
[Cmdlet(VerbsCommon.Set, "PickleTheme", SupportsShouldProcess = true)]
[OutputType(typeof(Theme))]
public sealed class SetPickleThemeCmdlet : PickleCmdlet
{
    [Parameter(Position = 0, Mandatory = true, ValueFromPipelineByPropertyName = true)]
    [ArgumentCompleter(typeof(PickleThemeNameCompleter))]
    public string Name { get; set; } = string.Empty;

    [Parameter]
    public SwitchParameter PassThru { get; set; }

    protected override void ProcessRecord()
    {
        if (!ShouldProcess(Name, "Set Pickle theme"))
        {
            return;
        }

        var themes = Runtime.ThemeProvider;
        try
        {
            themes.Apply(Name);
        }
        catch (ArgumentException ex)
        {
            WriteError(new ErrorRecord(ex, "PickleThemeNotFound", ErrorCategory.ObjectNotFound, Name));
            return;
        }

        if (PassThru)
        {
            WriteObject(themes.Current);
        }
    }
}

public sealed class PickleThemeNameCompleter : IArgumentCompleter
{
    public IEnumerable<CompletionResult> CompleteArgument(
        string commandName,
        string parameterName,
        string wordToComplete,
        CommandAst commandAst,
        IDictionary fakeBoundParameters)
    {
        if (PickleRuntime.Current is not { } runtime)
        {
            yield break;
        }

        var prefix = wordToComplete.Trim('\'', '"');
        foreach (var name in runtime.ThemeProvider.Available)
        {
            if (name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                yield return new CompletionResult(name, name, CompletionResultType.ParameterValue, runtime.ThemeProvider.Load(name)?.Description ?? name);
            }
        }
    }
}
