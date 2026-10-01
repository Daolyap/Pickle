using System.Runtime.Versioning;
using Pickle.Abstractions;
using Pickle.Abstractions.Services;

namespace Pickle.Windows.Services;

/// <summary>User variables are written directly (HKCU); machine-wide ones through the broker's allowlisted <c>MachineEnvironmentSet</c>.</summary>
[SupportedOSPlatform("windows")]
internal sealed class WindowsEnvironmentStore(IConfigStore config, IElevationBroker broker) : EnvironmentStoreBase(config)
{
    public override IReadOnlyList<EnvironmentScope> Scopes => [EnvironmentScope.Pickle, EnvironmentScope.User, EnvironmentScope.Machine, EnvironmentScope.Process];

    public override bool NeedsPrivileges(EnvironmentScope scope) => scope == EnvironmentScope.Machine && !broker.IsElevated;

    protected override string PathVariable => "Path";

    protected override Task<IReadOnlyList<EnvironmentVariable>> ListPersistentAsync(EnvironmentScope scope, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<EnvironmentVariable>>(
            [.. MachineEnvironment.Read(machine: scope == EnvironmentScope.Machine).Select(v => new EnvironmentVariable(v.Name, v.Value, scope) { Expandable = v.Expandable })]);

    protected override async Task<ServiceOperationResult> SetPersistentAsync(EnvironmentScope scope, string name, string? value, CancellationToken cancellationToken)
    {
        if (scope == EnvironmentScope.User)
        {
            MachineEnvironment.Set(name, value, machine: false);
            return new ServiceOperationResult(true, value is null ? $"{name} removed." : $"{name} saved. New programs see it; running ones keep the old value.");
        }

        try
        {
            var responses = await broker.RunAsync(
                [new ElevatedRequest(ElevatedOperationKind.MachineEnvironmentSet, value is null ? [name] : [name, value])], null, cancellationToken).ConfigureAwait(false);
            var response = responses.FirstOrDefault();
            return new ServiceOperationResult(response?.Success ?? false, response?.Message ?? "No answer from the elevated helper.");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new ServiceOperationResult(false, "Administrator approval was declined.");
        }
    }
}
