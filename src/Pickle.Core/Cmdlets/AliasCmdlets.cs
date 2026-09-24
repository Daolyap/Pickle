using System.Management.Automation;
using Pickle.Abstractions;
using Pickle.Core.Aliases;

namespace Pickle.Core.Cmdlets;

[Cmdlet(VerbsCommon.Get, "PickleAlias")]
[OutputType(typeof(AliasDefinition))]
public sealed class GetPickleAliasCmdlet : PickleCmdlet
{
    [Parameter(Position = 0)]
    [SupportsWildcards]
    public string[]? Name { get; set; }

    protected override void EndProcessing()
    {
        var patterns = (Name ?? ["*"]).Select(n => WildcardPattern.Get(n, WildcardOptions.IgnoreCase)).ToList();
        foreach (var alias in Runtime.Aliases.All.Where(a => patterns.Any(p => p.IsMatch(a.Name))))
        {
            WriteObject(alias);
        }
    }
}

[Cmdlet(VerbsCommon.Set, "PickleAlias")]
[OutputType(typeof(AliasDefinition))]
public sealed class SetPickleAliasCmdlet : PickleCmdlet
{
    [Parameter(Mandatory = true, Position = 0, ValueFromPipelineByPropertyName = true)]
    public string Name { get; set; } = string.Empty;

    [Parameter(Mandatory = true, Position = 1, ValueFromPipelineByPropertyName = true)]
    public string Body { get; set; } = string.Empty;

    [Parameter(ValueFromPipelineByPropertyName = true)]
    public AliasKind? Kind { get; set; }

    [Parameter(ValueFromPipelineByPropertyName = true)]
    public string? DirectoryScope { get; set; }

    [Parameter(ValueFromPipelineByPropertyName = true)]
    public string? MachineScope { get; set; }

    [Parameter(ValueFromPipelineByPropertyName = true)]
    public string? Description { get; set; }

    [Parameter]
    public SwitchParameter Force { get; set; }

    [Parameter]
    public SwitchParameter PassThru { get; set; }

    protected override void ProcessRecord()
    {
        var alias = new AliasDefinition
        {
            Name = Name,
            Body = Body,
            Kind = Kind ?? AliasCompiler.DetectKind(Body),
            DirectoryScope = string.IsNullOrWhiteSpace(DirectoryScope) ? null : DirectoryScope,
            MachineScope = string.IsNullOrWhiteSpace(MachineScope) ? null : MachineScope,
            Description = string.IsNullOrWhiteSpace(Description) ? null : Description,
        };

        try
        {
            if (Runtime.Aliases is AliasManager manager)
            {
                manager.SetChecked(alias, Force);
            }
            else
            {
                Runtime.Aliases.Set(alias);
            }
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            WriteError(new ErrorRecord(ex, "PickleAliasInvalid", ErrorCategory.InvalidArgument, Name));
            return;
        }

        if (PassThru)
        {
            WriteObject(alias);
        }
    }
}

[Cmdlet(VerbsCommon.Remove, "PickleAlias")]
public sealed class RemovePickleAliasCmdlet : PickleCmdlet
{
    [Parameter(Mandatory = true, Position = 0, ValueFromPipeline = true, ValueFromPipelineByPropertyName = true)]
    public string[] Name { get; set; } = [];

    protected override void ProcessRecord()
    {
        foreach (var name in Name)
        {
            if (!Runtime.Aliases.Remove(name))
            {
                WriteError(new ErrorRecord(
                    new ItemNotFoundException($"No Pickle alias named '{name}'."),
                    "PickleAliasNotFound",
                    ErrorCategory.ObjectNotFound,
                    name));
            }
        }
    }
}
