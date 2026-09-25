using Pickle.Abstractions;
using Pickle.Network.Commands;

namespace Pickle.Network;

/// <summary>Registers the built-in network tools as <c>pk</c> commands (the Tools panel lives in Pickle.Tui).</summary>
public sealed class NetworkToolsPlugin : IPicklePlugin
{
    public string Id => "pickle.network";

    public string DisplayName => "Network tools";

    public string Description => "Port scan, host sweep, DNS, traceroute, whois, TLS certificates, subnets, HTTP timing, Wake-on-LAN.";

    public static IReadOnlyList<IPickleCommand> Commands { get; } =
    [
        new ScanCommand(), new SweepCommand(), new DnsCommand(), new TraceCommand(), new WhoisCommand(),
        new CertCommand(), new SubnetCommand(), new HttpCommand(), new WolCommand(), new IpCommand(),
    ];

    public void Initialize(IPickleContext context)
    {
        foreach (var command in Commands)
        {
            context.Commands.Register(command);
        }
    }
}
