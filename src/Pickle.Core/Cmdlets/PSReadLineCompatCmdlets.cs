using System.Collections;
using System.Management.Automation;

namespace Pickle.Core.Cmdlets;

/// <summary>Internal entry point for the PSReadLine shim module (Set-/Get-/Remove-PSReadLine* functions).</summary>
[Cmdlet(VerbsLifecycle.Invoke, "PickleReadLineShim")]
public sealed class InvokePickleReadLineShimCmdlet : PickleCmdlet
{
    [Parameter(Mandatory = true, Position = 0)]
    [ValidateSet("SetOption", "GetOption", "SetKeyHandler", "GetKeyHandler", "RemoveKeyHandler")]
    public string Action { get; set; } = string.Empty;

    [Parameter(Position = 1)]
    public IDictionary? Parameters { get; set; }

    protected override void EndProcessing()
    {
        var compat = Runtime.ProfileLoader.ReadLine;
        var parameters = Parameters ?? new Hashtable();
        switch (Action)
        {
            case "SetOption":
                compat.SetOption(parameters, WriteWarning);
                break;
            case "GetOption":
                WriteObject(compat.GetOption());
                break;
            case "SetKeyHandler":
                var function = Unwrap(parameters["Function"]);
                var scriptBlock = function is ScriptBlock || parameters.Contains("ScriptBlock");
                compat.SetKeyHandler(Chords(parameters), function as string, scriptBlock, WriteWarning);
                break;
            case "GetKeyHandler":
                var chords = parameters.Contains("Chord") ? Chords(parameters) : null;
                foreach (var handler in compat.GetKeyHandlers(chords))
                {
                    WriteObject(handler);
                }

                break;
            default:
                compat.RemoveKeyHandler(Chords(parameters), WriteWarning);
                break;
        }
    }

    private static object? Unwrap(object? value) => value is PSObject pso ? pso.BaseObject : value;

    private static List<string> Chords(IDictionary parameters) =>
        Unwrap(parameters["Chord"]) switch
        {
            string single => [single],
            IEnumerable many => [.. many.Cast<object?>().Select(c => Unwrap(c)?.ToString() ?? string.Empty)],
            _ => [],
        };
}
