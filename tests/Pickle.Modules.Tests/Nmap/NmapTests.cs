using Pickle.Abstractions;
using Pickle.Modules.Nmap;
using Pickle.Testing.Fakes;
using Pickle.Tui.Tests;
using Terminal.Gui.Input;

namespace Pickle.Modules.Tests.Nmap;

public class NmapTests
{
    private const string Xml = """
        <?xml version="1.0"?>
        <nmaprun scanner="nmap">
          <host><status state="up"/><address addr="10.0.0.5" addrtype="ipv4"/><address addr="AA:BB:CC:00:11:22" addrtype="mac" vendor="Ubiquiti"/>
            <hostnames><hostname name="router.lan" type="PTR"/></hostnames>
            <ports>
              <port protocol="tcp" portid="22"><state state="open"/><service name="ssh" product="OpenSSH" version="8.9" extrainfo="protocol 2.0"/><script id="ssh-hostkey" output="256 aa:bb"/></port>
              <port protocol="tcp" portid="80"><state state="open"/><service name="http" product="nginx"/></port>
              <port protocol="tcp" portid="443"><state state="closed"/><service name="https"/></port>
            </ports>
            <os><osmatch name="Linux 5.15" accuracy="96"/></os></host>
          <host><status state="up"/><address addr="10.0.0.2" addrtype="ipv4"/></host>
          <host><status state="down"/><address addr="10.0.0.9" addrtype="ipv4"/></host>
          <runstats><finished summary="Nmap done: 256 IP addresses (2 hosts up) scanned in 3.2 seconds"/></runstats>
        </nmaprun>
        """;

    [Fact]
    public void ParsesHostsPortsServicesScriptsAndTheSummary()
    {
        var scan = NmapService.Parse(Xml);

        Assert.Equal(["10.0.0.2", "10.0.0.5", "10.0.0.9"], scan.Hosts.Select(h => h.Address));
        var router = scan.Hosts[1];
        Assert.Equal(("router.lan", "Ubiquiti", "Linux 5.15"), (router.Hostname, router.Vendor, router.Os));
        Assert.Equal([22, 80], router.OpenPorts.Select(p => p.Port));
        Assert.Equal("OpenSSH 8.9 protocol 2.0", router.Ports[0].Version);
        Assert.Equal("ssh-hostkey", router.Ports[0].Scripts[0].Id);
        Assert.Equal("Nmap done: 256 IP addresses (2 hosts up) scanned in 3.2 seconds", scan.Summary);
        Assert.Empty(NmapService.Parse("not xml").Hosts);
    }

    [Theory]
    [InlineData("10.0.0.5")]
    [InlineData("scanme.nmap.org example.com")]
    [InlineData("192.168.1.0/24,10.0.0.1-50")]
    [InlineData("fe80::1")]
    public void ValidTargetsPass(string target) => Assert.NotEmpty(NmapService.Targets(target));

    [Theory]
    [InlineData("")]
    [InlineData("--script=evil")]
    [InlineData("-iL /etc/passwd")]
    [InlineData("10.0.0.1; rm -rf /")]
    [InlineData("a|b")]
    [InlineData("$(whoami)")]
    public void OptionsAndShellCharactersAreRejected(string target) => Assert.Throws<ArgumentException>(() => NmapService.Targets(target));

    [Theory]
    [InlineData("10.0.0.5", true)]
    [InlineData("192.168.1.0/24", true)]
    [InlineData("172.20.0.1-20", true)]
    [InlineData("localhost router.lan", true)]
    [InlineData("127.0.0.1", true)]
    [InlineData("8.8.8.8", false)]
    [InlineData("scanme.nmap.org", false)]
    [InlineData("10.0.0.5 8.8.8.8", false)]
    [InlineData("172.32.0.1", false)]
    public void PublicTargetsNeedAnAuthorizationConfirm(string target, bool local) => Assert.Equal(local, NmapService.IsLocalOnly(NmapService.Targets(target)));

    [Fact]
    public void ArgumentsEndOptionsBeforeTargetsAndPortsReplaceProfileDefaults()
    {
        var quick = NmapService.Profile("quick");

        Assert.Equal(["-T4", "-F", "-oX", "-", "--", "10.0.0.5"], NmapService.Arguments("10.0.0.5", quick));
        Assert.Equal(["-T4", "-p", "22,80", "-oX", "-", "--", "10.0.0.5", "10.0.0.6"], NmapService.Arguments("10.0.0.5,10.0.0.6", quick, "22,80"));
        Assert.Throws<ArgumentException>(() => NmapService.Arguments("10.0.0.5", quick, "22; calc"));
        Assert.Throws<ArgumentException>(() => NmapService.Profile("nope"));
    }

    [Fact]
    public void ThePkCommandScansAndPrintsOnePortPerObject()
    {
        var runner = new FakeProgramRunner().On("nmap", "-T4", Xml);
        using var t = ModuleTestSupport.Start("nmap", runner);

        var ports = t.Run("pk nmap 10.0.0.5 --profile quick | Where-Object Port | ForEach-Object { \"$($_.Address):$($_.Port) $($_.Service)\" }");

        Assert.Equal(["10.0.0.5:22 ssh", "10.0.0.5:80 http"], ports);
        Assert.Equal(["-T4 -F -oX - -- 10.0.0.5"], runner.CommandLines("nmap"));
    }

    [Fact]
    public void APublicTargetIsNotScannedWithoutConfirmation()
    {
        var runner = new FakeProgramRunner().On("nmap", "-T4", Xml);
        using var t = ModuleTestSupport.Start("nmap", runner);

        t.Terminal.Type("n").Press("Enter");
        t.Run("pk nmap 8.8.8.8");

        Assert.Empty(runner.Calls);
        t.Run("pk nmap 8.8.8.8 --yes");
        Assert.Single(runner.Calls);
    }

    [Fact]
    public void ThePanelScansItsArgumentAndShowsHostsWithTheirOpenPorts()
    {
        var runner = new FakeProgramRunner().On("nmap", "-T4", Xml);
        var script = new UiScript()
            .WaitFor("rows", app => TuiHarness.Top<NmapPanel>(app).List.TotalCount == 4)
            .Do("check", app => Assert.Equal(["10.0.0.2", "10.0.0.5  (router.lan)", "    22/tcp ssh", "    80/tcp http"], TuiHarness.Top<NmapPanel>(app).List.VisibleItems.Select(r => r.Text)))
            .Do("select the ssh port", app => TuiHarness.Top<NmapPanel>(app).List.Select(TuiHarness.Top<NmapPanel>(app).List.VisibleItems[2]))
            .WaitFor("details", app => TuiHarness.Top<NmapPanel>(app).Details.PlainText.Contains("ssh-hostkey", StringComparison.Ordinal))
            .Press(Key.F4);
        var (t, host) = ModuleTestSupport.StartPanels("nmap", runner, script);
        using var _ = t;

        var result = host.Show("nmap", "10.0.0.5");

        script.AssertOk();
        Assert.Equal(new PanelResult(PanelResultKind.RunCommand, "nmap -T4 -F -- 10.0.0.5"), result);
    }
}
