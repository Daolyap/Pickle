using Pickle.Abstractions.Services;

namespace Pickle.Modules.Distros;

public sealed record DistroListing(IReadOnlyList<Distro> Items, IReadOnlyList<string> Errors);

/// <summary>Every available backend behind one list: what <c>pk wsl</c> and the panel use.</summary>
public sealed class DistroService(IReadOnlyList<IDistroBackend> backends, Func<IProgramRunner> runnerFactory)
{
    public IReadOnlyList<IDistroBackend> Backends => backends;

    public IReadOnlyList<IDistroBackend> Available => [.. backends.Where(b => b.IsAvailable)];

    public IProgramRunner Runner => runnerFactory();

    /// <summary>A backend that fails (WSL not installed yet, a stopped LXD daemon) does not hide the others; its message goes in <see cref="DistroListing.Errors"/>.</summary>
    public async Task<DistroListing> ListAsync(CancellationToken cancellationToken)
    {
        var items = new List<Distro>();
        var errors = new List<string>();
        foreach (var backend in Available)
        {
            try
            {
                items.AddRange(await backend.ListAsync(cancellationToken).ConfigureAwait(false));
            }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or TimeoutException)
            {
                errors.Add($"{backend.DisplayName}: {DistroNames.Clean(ex.Message).Trim()}");
            }
        }

        return new DistroListing(items, errors);
    }

    public IDistroBackend BackendOf(Distro distro) =>
        backends.FirstOrDefault(b => b.Id == distro.Backend) ?? throw new InvalidOperationException($"Unknown backend '{distro.Backend}'.");

    public Task<IReadOnlyList<string>> DescribeAsync(Distro distro, CancellationToken cancellationToken) => BackendOf(distro).DescribeAsync(distro, cancellationToken);

    public bool Supports(Distro distro, string verb) => BackendOf(distro).Command(distro, verb) is not null;

    public async Task<ServiceOperationResult> ActAsync(Distro distro, string verb, CancellationToken cancellationToken)
    {
        var backend = BackendOf(distro);
        if (backend.Command(distro, verb) is not { } command)
        {
            return new ServiceOperationResult(false, $"{backend.DisplayName} has no '{verb}' for {distro.Name}.");
        }

        var result = await Runner.RunAsync(
            command.Program,
            command.Arguments,
            new ProgramRunOptions { Environment = command.Environment, Timeout = command.Timeout ?? TimeSpan.FromMinutes(2) },
            cancellationToken).ConfigureAwait(false);
        return new ServiceOperationResult(result.Success, result.Success ? $"{verb} {distro.Name}: done." : DistroNames.Clean(result.Message));
    }

    /// <summary>Runs a long operation (export, import, update), passing its output on as it arrives.</summary>
    public Task<int> StreamAsync(BackendCommand command, Action<string> onLine, CancellationToken cancellationToken) =>
        Runner.StreamAsync(
            command.Program,
            command.Arguments,
            line => onLine(DistroNames.Clean(line)),
            new ProgramRunOptions { Environment = command.Environment, Timeout = command.Timeout },
            cancellationToken);

    /// <summary>The one running with this name; <paramref name="backendId"/> narrows it down when two backends use the same name.</summary>
    public static Distro? Find(IEnumerable<Distro> distros, string name, string? backendId = null)
    {
        var matches = distros.Where(d => string.Equals(d.Name, name, StringComparison.OrdinalIgnoreCase) && (backendId is null || d.Backend == backendId)).ToList();
        return matches.Count switch
        {
            1 => matches[0],
            0 => null,
            _ => throw new ArgumentException($"'{name}' exists in {string.Join(" and ", matches.Select(m => m.Backend))}: add --backend <name>."),
        };
    }
}
