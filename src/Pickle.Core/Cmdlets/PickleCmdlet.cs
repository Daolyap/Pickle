using System.Management.Automation;

namespace Pickle.Core.Cmdlets;

/// <summary>Base class for Pickle cmdlets. Cmdlets in Pickle.Core with [Cmdlet] are registered automatically.</summary>
public abstract class PickleCmdlet : PSCmdlet
{
    protected PickleRuntime Runtime => PickleRuntime.Resolve(Host);
}
