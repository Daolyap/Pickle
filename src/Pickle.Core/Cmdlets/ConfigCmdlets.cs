using System.Management.Automation;
using Pickle.Abstractions;
using Pickle.Core.Commands;
using Pickle.Core.Config;

namespace Pickle.Core.Cmdlets;

/// <summary><c>Get-PickleConfig [-Path editor.bellStyle]</c>: the effective settings (config.json + config.local.json).</summary>
[Cmdlet(VerbsCommon.Get, "PickleConfig")]
[OutputType(typeof(PickleConfig))]
public sealed class GetPickleConfigCmdlet : PickleCmdlet
{
    [Parameter(Position = 0)]
    public string? Path { get; set; }

    protected override void EndProcessing()
    {
        var store = Runtime.ConfigStore;
        if (string.IsNullOrWhiteSpace(Path))
        {
            WriteObject(store.Current);
            return;
        }

        try
        {
            WriteObject(ConfigValues.Get(store.Current, ConfigSchema.Resolve(Path)), enumerateCollection: false);
        }
        catch (ConfigValidationException ex)
        {
            WriteError(new ErrorRecord(new ArgumentException(ex.Message, ex), "PickleConfigPath", ErrorCategory.InvalidArgument, Path));
        }
    }
}

/// <summary><c>Set-PickleConfig -Path editor.bellStyle -Value visual [-Local]</c>.</summary>
[Cmdlet(VerbsCommon.Set, "PickleConfig", SupportsShouldProcess = true)]
public sealed class SetPickleConfigCmdlet : PickleCmdlet
{
    [Parameter(Mandatory = true, Position = 0)]
    public string Path { get; set; } = string.Empty;

    [Parameter(Mandatory = true, Position = 1)]
    [AllowNull]
    [AllowEmptyString]
    public object? Value { get; set; }

    /// <summary>Write to config.local.json (this machine only, never synced).</summary>
    [Parameter]
    public SwitchParameter Local { get; set; }

    protected override void EndProcessing()
    {
        var store = Runtime.ConfigStore;
        var text = Value switch
        {
            null => "null",
            string s => s,
            PSObject { BaseObject: string s } => s,
            _ => PsJson.ToNode(Value)?.ToJsonString() ?? "null",
        };

        if (!ShouldProcess($"{Path} = {text}", Local ? "Set in config.local.json" : "Set in config.json"))
        {
            return;
        }

        try
        {
            if (Local)
            {
                store.SetLocalValue(Path, text);
            }
            else
            {
                store.SetValue(Path, text);
            }
        }
        catch (ConfigValidationException ex)
        {
            WriteError(new ErrorRecord(new ArgumentException(ex.Message, ex), "PickleConfigInvalid", ErrorCategory.InvalidArgument, Value));
        }
    }
}
