using Pickle.Abstractions;

namespace Pickle.Core.Commands;

/// <summary>Small `pk` commands owned by Core itself. Workstreams add their own IPickleCommand classes next to their features.</summary>
public sealed class VersionCommand : IPickleCommand
{
    public string Name => "version";

    public string Description => "Show Pickle, PowerShell and .NET versions";

    public string Usage => "pk version";

    public ValueTask<int> ExecuteAsync(PickleCommandContext context, IReadOnlyList<string> args, CancellationToken cancellationToken)
    {
        context.WriteObject(new
        {
            Pickle = PickleRuntime.Version,
            PowerShell = PickleRuntime.PowerShellVersion,
            DotNet = Environment.Version.ToString(),
            OS = System.Runtime.InteropServices.RuntimeInformation.OSDescription,
            ConfigDir = context.Pickle.Paths.ConfigDir,
        });
        return ValueTask.FromResult(0);
    }
}
