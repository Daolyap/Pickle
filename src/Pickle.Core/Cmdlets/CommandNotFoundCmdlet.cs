using System.Management.Automation;
using Pickle.Core.Translation;

namespace Pickle.Core.Cmdlets;

/// <summary>Prints "did you mean …?" / winget install hints for a missing command. Called by Pickle's CommandNotFoundAction.</summary>
[Cmdlet(VerbsLifecycle.Invoke, "PickleCommandNotFound")]
public sealed class InvokePickleCommandNotFoundCmdlet : PickleCmdlet
{
    [Parameter(Mandatory = true, Position = 0)]
    public string Name { get; set; } = string.Empty;

    protected override void EndProcessing()
    {
        if (Runtime.Translation is not TranslationPipeline pipeline)
        {
            return;
        }

        var hints = pipeline.CommandNotFound.Handle(Name, SessionCommandNames);
        foreach (var hint in hints)
        {
            Host.UI.WriteLine(hint);
        }
    }

    private IEnumerable<string> SessionCommandNames() =>
        InvokeCommand.GetCommands("*", CommandTypes.Alias | CommandTypes.Function | CommandTypes.Cmdlet | CommandTypes.Filter, nameIsPattern: true)
            .Select(c => c.Name);
}
