using Pickle.Abstractions;
using Pickle.Abstractions.Services;

namespace Pickle.Tui.Panels.Admin;

/// <summary><c>pk services [filter]</c> opens the panel; <c>pk services list</c> prints objects; <c>pk services restart nginx.service</c> does one thing.</summary>
internal sealed class ServicesCommand : PanelCommand
{
    private static readonly Dictionary<string, ServiceAction> Verbs = new(StringComparer.OrdinalIgnoreCase)
    {
        ["start"] = ServiceAction.Start,
        ["stop"] = ServiceAction.Stop,
        ["restart"] = ServiceAction.Restart,
        ["enable"] = ServiceAction.Enable,
        ["disable"] = ServiceAction.Disable,
    };

    public override string Name => "services";

    public override string Description => "Windows services, systemd units and launchd jobs";

    public override string Usage => "pk services [filter] | list [--user] [--state running|stopped|failed] | start|stop|restart|enable|disable <name> [--user] [--yes]";

    public override IReadOnlyList<string> Examples =>
    [
        "pk services                      open the panel (Alt+V)",
        "pk services list --state failed  what is broken",
        "pk services restart ssh.service  one privileged command (UAC on Windows, sudo or polkit elsewhere)",
    ];

    protected override string PanelId => ServicesPanel.PanelId;

    protected override async Task<int> RunAsync(CommandOutput output, IReadOnlyList<string> args, CancellationToken cancellationToken)
    {
        if (args.Count > 0 && Verbs.TryGetValue(args[0], out var action))
        {
            return await ControlAsync(output, action, args, cancellationToken).ConfigureAwait(false);
        }

        return await base.RunAsync(output, args, cancellationToken).ConfigureAwait(false);
    }

    protected override async Task<int> ListAsync(CommandOutput output, IReadOnlyList<string> raw, CancellationToken cancellationToken)
    {
        var manager = Manager(output);
        var args = CommandArgs.Parse(raw, "state");
        var filter = args.Arg(0);
        var services = await manager.ListAsync(args.Has("user"), cancellationToken).ConfigureAwait(false);
        foreach (var service in services
            .Where(s => filter is null || s.Id.Contains(filter, StringComparison.OrdinalIgnoreCase) || s.DisplayName.Contains(filter, StringComparison.OrdinalIgnoreCase))
            .Where(s => args.Value("state") is not { } state || string.Equals(s.State.ToString(), state, StringComparison.OrdinalIgnoreCase)))
        {
            output.Object(Display.Columns(service, "Id", "State", "StartMode", "DisplayName"));
        }

        return 0;
    }

    private async Task<int> ControlAsync(CommandOutput output, ServiceAction action, IReadOnlyList<string> raw, CancellationToken cancellationToken)
    {
        var args = CommandArgs.Parse(raw.Skip(1).ToList());
        if (args.Arg(0) is not { } id)
        {
            return UsageError(output, $"Name the service to {action.ToString().ToLowerInvariant()}.");
        }

        if (action is ServiceAction.Stop or ServiceAction.Disable or ServiceAction.Restart
            && !output.Confirm(args, $"{action} {id}?", false))
        {
            output.Muted("Cancelled.");
            return 1;
        }

        var result = await Manager(output).ControlAsync(id, action, args.Has("user"), cancellationToken).ConfigureAwait(false);
        if (result.Success)
        {
            output.Success(result.Message);
            return 0;
        }

        output.Failure(result.Message);
        if (result.ShellCommand is { } command)
        {
            output.Muted("It needs a password. Run: " + command);
        }

        return 1;
    }

    private static ISystemServiceManager Manager(CommandOutput output) =>
        output.Pickle.Services.Get<ISystemServiceManager>() is { IsSupported: true } manager
            ? manager
            : throw new InvalidOperationException("Service management is not available on this machine.");
}
