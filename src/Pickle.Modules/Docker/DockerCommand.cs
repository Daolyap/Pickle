using Pickle.Abstractions;
using Pickle.Abstractions.Services;
using Pickle.Tui;

namespace Pickle.Modules.Docker;

/// <summary><c>pk docker</c>: the panel interactively; <c>list</c> and one-line actions for scripts.</summary>
internal sealed class DockerCommand : PanelCommand
{
    public override string Name => "docker";

    public override string Description => "Docker or Podman containers, images, volumes and compose projects";

    public override string Usage => "pk docker [containers|images|volumes|compose] | list [kind] [filter] | start|stop|restart|rm <name|id> [--yes]";

    public override IReadOnlyList<string> Examples => ["pk docker", "pk docker list containers web", "pk docker restart web-1"];

    protected override string PanelId => DockerPanel.PanelId;

    protected override string? PanelArgument(IReadOnlyList<string> args) => args.Count > 0 ? args[0] : null;

    protected override async Task<int> RunAsync(CommandOutput output, IReadOnlyList<string> raw, CancellationToken cancellationToken)
    {
        var verb = raw.Count > 0 ? raw[0].ToLowerInvariant() : string.Empty;
        if (verb is not ("start" or "stop" or "restart" or "rm" or "remove"))
        {
            return await base.RunAsync(output, raw, cancellationToken).ConfigureAwait(false);
        }

        var args = CommandArgs.Parse(raw.Skip(1).ToList());
        if (args.Arg(0) is not { } target)
        {
            return UsageError(output, $"Name the container to {verb}.");
        }

        var docker = Docker(output);
        var item = (await docker.ListAsync(DockerKind.Containers, cancellationToken).ConfigureAwait(false))
            .FirstOrDefault(i => string.Equals(i.Name, target, StringComparison.OrdinalIgnoreCase) || i.Id.StartsWith(target, StringComparison.OrdinalIgnoreCase));
        if (item is null)
        {
            output.Failure($"No container matches '{target}'.");
            return 1;
        }

        if (verb is "stop" or "restart" or "rm" or "remove" && !output.Confirm(args, $"{verb} {item.Name}?", false))
        {
            output.Muted("Cancelled.");
            return 1;
        }

        var result = await docker.ActAsync(item, verb == "rm" ? "remove" : verb, cancellationToken).ConfigureAwait(false);
        return Report(output, result);
    }

    protected override async Task<int> ListAsync(CommandOutput output, IReadOnlyList<string> raw, CancellationToken cancellationToken)
    {
        var args = CommandArgs.Parse(raw);
        var kind = Enum.TryParse<DockerKind>(args.Arg(0), ignoreCase: true, out var parsed) ? parsed : DockerKind.Containers;
        var filter = Enum.TryParse<DockerKind>(args.Arg(0), ignoreCase: true, out _) ? args.Arg(1) : args.Arg(0);
        foreach (var item in (await Docker(output).ListAsync(kind, cancellationToken).ConfigureAwait(false))
            .Where(i => filter is null || i.Name.Contains(filter, StringComparison.OrdinalIgnoreCase)))
        {
            output.Object(Display.Columns(item, "Name", "State", "Summary", "Id"));
        }

        return 0;
    }

    private static DockerService Docker(CommandOutput output) =>
        new(output.Pickle.Services.Require<IProgramRunner>(), output.Pickle.Config);

    private static int Report(CommandOutput output, ServiceOperationResult result)
    {
        if (result.Success)
        {
            output.Success(result.Message);
            return 0;
        }

        output.Failure(result.Message);
        return 1;
    }
}
