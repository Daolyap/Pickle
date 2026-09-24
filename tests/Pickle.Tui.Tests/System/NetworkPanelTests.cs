using Pickle.Abstractions;
using Pickle.Abstractions.Services;
using Pickle.Testing;
using Pickle.Testing.Fakes;
using Pickle.Tui.Panels.SystemMonitoring;
using Terminal.Gui.Input;
using Terminal.Gui.Testing;
using static Pickle.Tui.Tests.Windows.PanelRunner;

namespace Pickle.Tui.Tests.SystemMonitoring;

public sealed class NetworkPanelTests : IDisposable
{
    private readonly TestPickle _t = TestPickle.Create();
    private readonly FakeNetworkMonitor _monitor = new();

    public NetworkPanelTests()
    {
        _t.Runtime.ServiceRegistry.Add<INetworkMonitor>(_monitor);
        _monitor.Interfaces.Add(Nic("eth0", "Up", 2048, 1_048_576));
        _monitor.Interfaces.Add(Nic("wlan0", "Down", 0, 0));
        _monitor.Connections.Add(new NetworkConnection("TCP", "0.0.0.0", 80, null, null, "Listen", 800, "nginx"));
        _monitor.Connections.Add(new NetworkConnection("TCP", "192.168.1.5", 50514, "140.82.112.3", 443, "Established", 900, "git"));
        _monitor.Connections.Add(new NetworkConnection("UDP", "::", 5353, null, null, "Listen"));
        _monitor.Hosts["example.test"] = ["203.0.113.7", "2001:db8::7"];
    }

    public void Dispose() => _t.Dispose();

    private static NetworkInterfaceSample Nic(string name, string status, double send, double receive) =>
        new(name, name, name + " adapter", "Ethernet", status, ["192.168.1.5/24"], ["fe80::1/64"], "AA:BB:CC:DD:EE:FF", 1_000_000_000, 10, 10, send, receive);

    private NetworkPanel Panel() => new(new PanelContext { Pickle = _t.Runtime });

    [Fact]
    public void InterfacesShowAddressesAndThroughputWithHistory()
    {
        using var panel = Panel();
        var screen = SystemPanelRunner.RunAndDraw(
            panel,
            Wait(() => panel.Interfaces.TotalCount == 2),
            Do(() => panel.ApplyInterfaces([Nic("eth0", "Up", 4096, 512), Nic("wlan0", "Down", 0, 0)])));

        Assert.Equal(["eth0", "wlan0"], panel.Interfaces.Rows.Select(n => n.Name));
        Assert.Equal("eth0", panel.Interfaces.Selected?.Name);
        var rows = panel.Traffic.Rows;
        Assert.Equal("↑ Send", rows[0].Label);
        Assert.Equal("4.00 KB/s", rows[0].Value);
        Assert.Equal([2048, 4096], rows[0].History);
        Assert.Equal([1_048_576, 512], rows[1].History);
        Assert.Contains("192.168.1.5/24", screen, StringComparison.Ordinal);
        Assert.Contains("Interfaces", screen, StringComparison.Ordinal);
        Assert.Contains("Connections", screen, StringComparison.Ordinal);
        Assert.Contains("←→ Tabs", screen, StringComparison.Ordinal);
    }

    [Fact]
    public void LeftAndRightSwitchTabsAndConnectionsLoadAndFilter()
    {
        using var panel = Panel();
        Run(
            panel,
            When(() => panel.Interfaces.TotalCount == 2, () => panel.App!.InjectKey(Key.CursorRight)),
            When(() => panel.CurrentTab == panel.ConnectionsTab && panel.Connections.TotalCount == 3, () =>
            {
                Assert.Equal([80, 5353, 50514], panel.Connections.Rows.Select(c => c.LocalPort));
                foreach (var ch in "ngin")
                {
                    panel.App!.InjectKey(new Key(ch));
                }
            }),
            When(() => panel.Connections.Rows.Count == 1, () =>
            {
                Assert.Equal(800, panel.Connections.Rows[0].ProcessId);
                panel.App!.InjectKey(Key.CursorRight);
            }),
            When(() => panel.CurrentTab == panel.ToolsTab, () => panel.MoveTab(1)),
            Wait(() => panel.CurrentTab == panel.InterfacesTab),
            Do(() => panel.App!.InjectKey(Key.CursorLeft)),
            Wait(() => panel.CurrentTab == panel.ToolsTab));
    }

    [Fact]
    public void EndpointsBracketIPv6()
    {
        Assert.Equal("[2001:db8::7]:443", NetworkPanel.Endpoint("2001:db8::7", 443));
        Assert.Equal("10.0.0.1:22", NetworkPanel.Endpoint("10.0.0.1", 22));
        Assert.Equal(string.Empty, NetworkPanel.Endpoint(null, null));
    }

    [Fact]
    public void PingShowsLiveRepliesUntilStopped()
    {
        using var panel = Panel();
        Run(
            panel,
            Do(() =>
            {
                panel.Host = "example.test";
                panel.StartPing();
            }),
            When(() => panel.OutputText.Contains("Reply from 203.0.113.7: seq=1 time=12 ms TTL=64", StringComparison.Ordinal), panel.StopPing),
            Wait(() => panel.OutputText.Contains("1 received, 0% loss", StringComparison.Ordinal) || panel.OutputText.Contains("2 received, 0% loss", StringComparison.Ordinal)));

        Assert.Equal(panel.ToolsTab, panel.CurrentTab);
        Assert.StartsWith("PING example.test", panel.OutputText, StringComparison.Ordinal);
        Assert.False(panel.Pinging);
    }

    [Fact]
    public void LookupListsAddressesOrTheError()
    {
        using var panel = Panel();
        Run(
            panel,
            Do(() =>
            {
                panel.Host = "example.test";
                panel.Lookup();
            }),
            When(() => panel.OutputText.Contains("2001:db8::7", StringComparison.Ordinal), () =>
            {
                panel.Host = "nowhere.test";
                panel.Lookup();
            }),
            Wait(() => panel.OutputText.Contains("✗ No such host is known: nowhere.test", StringComparison.Ordinal)));

        Assert.Contains("  203.0.113.7", panel.OutputText, StringComparison.Ordinal);
        Assert.Equal(["lookup example.test", "lookup nowhere.test"], _monitor.Calls);
    }

    [Fact]
    public void InvalidHostsAreNotSent()
    {
        using var panel = Panel();
        Run(
            panel,
            Do(() =>
            {
                panel.Host = "-f bad";
                panel.StartPing();
                panel.Lookup();
            }));

        Assert.Empty(_monitor.Calls);
        Assert.Contains("Type a host name", panel.OutputText, StringComparison.Ordinal);
    }

    [Fact]
    public void FlushDnsIsOfferedOnlyWhereSupported()
    {
        using (var panel = Panel())
        {
            var screen = SystemPanelRunner.RunAndDraw(panel, Do(panel.FlushDns));
            Assert.DoesNotContain("Flush DNS", screen, StringComparison.Ordinal);
        }

        _monitor.CanFlushDns = true;
        using var windows = Panel();
        Run(windows, Do(windows.FlushDns), Wait(() => windows.OutputText.Contains("Successfully flushed", StringComparison.Ordinal)));
        Assert.Equal(["flushdns"], _monitor.Calls);
    }
}
