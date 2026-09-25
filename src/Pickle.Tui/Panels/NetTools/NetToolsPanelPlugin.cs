using Pickle.Abstractions;
using Pickle.Tui.Panels.SystemMonitoring;

namespace Pickle.Tui.Panels.NetTools;

/// <summary>Registers the Network tools panel (Alt+T, <c>pk tools [tool]</c>).</summary>
public sealed class NetToolsPanelPlugin : IPicklePlugin
{
    public string Id => "pickle.nettools.panel";

    public string DisplayName => "Network tools panel";

    public string Description => "Port scan, host discovery, DNS, traceroute, whois, certificates, HTTP, subnets and Wake-on-LAN in one panel.";

    public void Initialize(IPickleContext context)
    {
        context.Panels.Register(new PanelDescriptor
        {
            Id = NetToolsPanel.PanelId,
            Title = "Network tools",
            Description = "Port scan, host discovery, DNS, traceroute, whois, TLS certificate, HTTP check, subnet calculator, Wake-on-LAN",
            DefaultKey = "Alt+T",
            CreateView = ctx => new NetToolsPanel(ctx),
        });
        context.Commands.Register(new OpenPanelCommand(
            "tools",
            NetToolsPanel.PanelId,
            "Open the network tools panel (scan, sweep, dns, trace, whois, cert, http, subnet, wol, ip)",
            "pk tools [scan|sweep|dns|trace|whois|cert|http|subnet|wol|ip] [target]"));
    }
}
