using System.Management.Automation.Runspaces;

[assembly: AssemblyFixture(typeof(Pickle.Core.Tests.Syntax.PowerShellWarmup))]

namespace Pickle.Core.Tests.Syntax;

/// <summary>
/// PowerShell caches a snap-in's cmdlets and providers in two separate steps, so runspaces created concurrently for
/// the first time can miss the FileSystem provider. Test classes open runspaces in parallel; fill the cache once first.
/// </summary>
public sealed class PowerShellWarmup
{
    public PowerShellWarmup() => InitialSessionState.CreateDefault2();
}
