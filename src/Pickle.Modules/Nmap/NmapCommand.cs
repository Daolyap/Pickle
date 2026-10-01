using Pickle.Abstractions;
using Pickle.Abstractions.Services;
using Pickle.Tui;

namespace Pickle.Modules.Nmap;

/// <summary><c>pk nmap</c>: the panel with no arguments; with a target it scans and prints one object per open port (or per host with <c>--hosts</c>).</summary>
internal sealed class NmapCommand : PanelCommand
{
    public override string Name => "nmap";

    public override string Description => "Scan hosts and ports with nmap (profiles, results as objects)";

    public override string Usage => $"pk nmap [target…] [--profile {string.Join('|', NmapService.Profiles.Select(p => p.Id))}] [--ports 22,80] [--hosts] [--yes]";

    public override IReadOnlyList<string> Examples =>
    [
        "pk nmap                                   open the panel (Alt+Shift+N)",
        "pk nmap 192.168.1.0/24 --profile ping     which hosts are up",
        "pk nmap 10.0.0.5 --ports 1-1024 | Where-Object Service -like 'ssh*'",
    ];

    protected override string PanelId => NmapPanel.PanelId;

    protected override string? PanelArgument(IReadOnlyList<string> args) => args.Count == 0 ? null : string.Join(' ', args.Where(a => !a.StartsWith('-')));

    protected override Task<int> RunAsync(CommandOutput output, IReadOnlyList<string> raw, CancellationToken cancellationToken) =>
        raw.Count == 0 && output.Pickle.Shell.IsInteractive ? base.RunAsync(output, raw, cancellationToken) : ListAsync(output, raw, cancellationToken);

    protected override async Task<int> ListAsync(CommandOutput output, IReadOnlyList<string> raw, CancellationToken cancellationToken)
    {
        var args = CommandArgs.Parse(raw, "profile", "ports");
        if (args.Positional.Count == 0)
        {
            return UsageError(output, "Give a target to scan.");
        }

        var nmap = new NmapService(output.Pickle.Services.Require<IProgramRunner>());
        var targets = NmapService.Targets(string.Join(' ', args.Positional));
        var profile = NmapService.Profile(args.Value("profile") ?? "quick");
        _ = NmapService.Arguments(string.Join(' ', targets), profile, args.Value("ports"));
        if (!NmapService.IsLocalOnly(targets)
            && !output.Confirm(args, "Only scan systems you own or have written permission to test. This target is not on a private network. Scan it?", false))
        {
            output.Muted("Cancelled.");
            return 1;
        }

        output.Muted($"nmap {profile.Name} → {string.Join(' ', targets)} …");
        var scan = await nmap.ScanAsync(string.Join(' ', targets), profile, args.Value("ports"), cancellationToken).ConfigureAwait(false);
        foreach (var host in scan.Hosts.Where(h => h.Status == "up"))
        {
            if (args.Has("hosts") || !host.OpenPorts.Any())
            {
                output.Object(Display.Columns(new HostRow(host.Address, host.Hostname, host.Status, host.Os, host.Mac, host.OpenPorts.Count()), "Address", "Hostname", "OpenPorts", "Os"));
            }

            if (!args.Has("hosts"))
            {
                foreach (var port in host.OpenPorts)
                {
                    output.Object(Display.Columns(new PortRow(host.Address, host.Hostname, port.Port, port.Protocol, port.State, port.Service, port.Version), "Address", "Port", "Protocol", "Service", "Version"));
                }
            }
        }

        output.Muted(scan.Summary);
        return 0;
    }

    private sealed record HostRow(string Address, string? Hostname, string Status, string? Os, string? Mac, int OpenPorts);

    private sealed record PortRow(string Address, string? Hostname, int Port, string Protocol, string State, string Service, string Version);
}
