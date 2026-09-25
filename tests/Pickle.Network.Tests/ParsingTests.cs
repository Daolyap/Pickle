using System.Net;

namespace Pickle.Network.Tests;

public sealed class ParsingTests
{
    [Theory]
    [InlineData("10.0.0.0/30", "10.0.0.1,10.0.0.2")]
    [InlineData("10.0.0.7/32", "10.0.0.7")]
    [InlineData("10.0.0.1-3", "10.0.0.1,10.0.0.2,10.0.0.3")]
    [InlineData("10.0.0.254-10.0.1.1", "10.0.0.254,10.0.0.255,10.0.1.0,10.0.1.1")]
    [InlineData("server1, 10.0.0.1 server1", "server1,10.0.0.1")]
    public void ExpandsTargets(string spec, string expected) =>
        Assert.Equal(expected.Split(','), NetTargets.Expand(spec));

    [Fact]
    public void CidrCountsAndLimits()
    {
        Assert.Equal(254, NetTargets.Expand("192.168.1.0/24").Count);
        Assert.Equal(256, NetTargets.Expand("192.168.1.0/24", skipNetworkAndBroadcast: false).Count);
        Assert.Equal(16, NetTargets.Expand("fd00::/124").Count);
        Assert.Throws<ArgumentException>(() => NetTargets.Expand("10.0.0.0/8"));
        Assert.Throws<ArgumentException>(() => NetTargets.Expand("10.0.0.9-3"));
        Assert.Throws<ArgumentException>(() => NetTargets.Expand("not a host!"));
    }

    [Theory]
    [InlineData("22", false)]
    [InlineData("10.1", false)]
    [InlineData("10.0.0.1", true)]
    [InlineData("::1", true)]
    public void OnlyWrittenOutAddressesAreLiterals(string text, bool literal) =>
        Assert.Equal(literal, NetTargets.TryParseLiteral(text, out _));

    [Fact]
    public void PortSpecs()
    {
        Assert.Equal(100, PortSpec.Top100.Distinct().Count());
        Assert.Equal([22, 80, 443], PortSpec.Parse("443,22 80,22"));
        Assert.Equal([1, 2, 3, 8080], PortSpec.Parse("1-3,8080"));
        Assert.Equal(PortSpec.Sets["web"].Order(), PortSpec.Parse("web"));
        Assert.Contains(3389, PortSpec.Parse("windows,db"));
        Assert.Equal(65535, PortSpec.Parse("all").Count);
        Assert.Equal(1024, PortSpec.Parse("-1024").Count);
        Assert.Throws<ArgumentException>(() => PortSpec.Parse("70000"));
        Assert.Throws<ArgumentException>(() => PortSpec.Parse("webz"));
        Assert.Equal("rdp", PortCatalog.Name(3389));
    }

    [Theory]
    [InlineData("10.1.2.3/22", "10.1.0.0/22", "255.255.252.0", "10.1.3.255", "10.1.0.1", "10.1.3.254", 1022, "private (RFC 1918)")]
    [InlineData("192.168.5.9/255.255.255.0", "192.168.5.0/24", "255.255.255.0", "192.168.5.255", "192.168.5.1", "192.168.5.254", 254, "private (RFC 1918)")]
    [InlineData("8.8.8.8", "8.8.8.8/32", "255.255.255.255", null, "8.8.8.8", "8.8.8.8", 1, "public")]
    [InlineData("100.64.0.1/31", "100.64.0.0/31", "255.255.255.254", null, "100.64.0.0", "100.64.0.1", 2, "shared (CGNAT)")]
    public void SubnetMaths(string input, string cidr, string mask, string? broadcast, string first, string last, long hosts, string kind)
    {
        var info = Subnet.Calculate(input);
        Assert.Equal((cidr, mask, broadcast, first, last, (System.Numerics.BigInteger)hosts, kind), (info.Cidr, info.Mask, info.Broadcast, info.FirstHost, info.LastHost, info.Hosts, info.Kind));
    }

    [Fact]
    public void SubnetSplitContainsAndIPv6()
    {
        Assert.Equal(["10.0.0.0/26", "10.0.0.64/26", "10.0.0.128/26", "10.0.0.192/26"], Subnet.Split("10.0.0.0/24", 26));
        Assert.True(Subnet.Contains("10.0.0.0/24", "10.0.0.200"));
        Assert.False(Subnet.Contains("10.0.0.0/25", "10.0.0.200"));
        var v6 = Subnet.Calculate("fd12:3456::7/48");
        Assert.Equal(("fd12:3456::/48", "unique local (private)"), (v6.Cidr, v6.Kind));
        Assert.Throws<ArgumentException>(() => Subnet.Split("10.0.0.0/8", 30));
    }

    [Fact]
    public void WakeOnLanPacket()
    {
        var mac = WakeOnLan.ParseMac("aa:bb:cc:dd:ee:ff");
        var packet = WakeOnLan.BuildPacket(mac);
        Assert.Equal(102, packet.Length);
        Assert.All(packet[..6], b => Assert.Equal(0xFF, b));
        Assert.Equal(mac, packet[96..102]);
        Assert.Equal(mac, WakeOnLan.ParseMac("AA-BB-CC-DD-EE-FF"));
        Assert.Throws<ArgumentException>(() => WakeOnLan.ParseMac("aa:bb:cc:dd:ee"));
        Assert.Throws<ArgumentException>(() => WakeOnLan.ParseMac("zz:bb:cc:dd:ee:ff"));
    }

    [Fact]
    public void ArpTableParsing()
    {
        const string proc = """
            IP address       HW type     Flags       HW address            Mask     Device
            192.168.1.1      0x1         0x2         aa:bb:cc:00:11:22     *        eth0
            192.168.1.9      0x1         0x0         00:00:00:00:00:00     *        eth0
            """;
        Assert.Equal("AA-BB-CC-00-11-22", ArpTable.ParseProcArp(proc, "192.168.1.1"));
        Assert.Null(ArpTable.ParseProcArp(proc, "192.168.1.9"));
    }

    [Fact]
    public void WhoisReferralsAndFields()
    {
        const string iana = """
            % IANA WHOIS server
            domain:       COM
            refer:        whois.verisign-grs.com
            """;
        const string registry = """
               Domain Name: EXAMPLE.COM
               Registrar WHOIS Server: whois.registrar.example
               Registrar: Example Registrar, Inc.
               Creation Date: 1995-08-14T04:00:00Z
               Registry Expiry Date: 2027-08-13T04:00:00Z
               Name Server: A.IANA-SERVERS.NET
               Name Server: B.IANA-SERVERS.NET
               Domain Status: clientDeleteProhibited https://icann.org/epp#clientDeleteProhibited
            """;
        Assert.Equal("whois.verisign-grs.com", Whois.Referral(iana));
        Assert.Equal("whois.registrar.example", Whois.Referral(registry));
        var fields = Whois.ParseFields(registry);
        Assert.Equal("Example Registrar, Inc.", fields["Registrar"]);
        Assert.Equal("2027-08-13T04:00:00Z", fields["Expires"]);
        Assert.Equal("a.iana-servers.net, b.iana-servers.net", fields["Name servers"]);
        Assert.Equal("clientdeleteprohibited", fields["Status"]);
    }

    [Fact]
    public void BannerSummaries()
    {
        Assert.Equal("SSH-2.0-OpenSSH_9.6", PortScanner.SummarizeBanner("SSH-2.0-OpenSSH_9.6\r\n"));
        Assert.Equal("HTTP/1.1 200 OK · nginx/1.25", PortScanner.SummarizeBanner("HTTP/1.1 200 OK\r\nDate: x\r\nServer: nginx/1.25\r\n\r\n"));
        Assert.Equal("220 mail ESMTP", PortScanner.SummarizeBanner("220 mail ESMTP\u0007\r\n"));
    }

    [Fact]
    public void ReverseNames()
    {
        Assert.Equal("4.3.2.1.in-addr.arpa", DnsClient.ReverseName(IPAddress.Parse("1.2.3.4")));
        Assert.EndsWith(".8.b.d.0.1.0.0.2.ip6.arpa", DnsClient.ReverseName(IPAddress.Parse("2001:db8::1")), StringComparison.Ordinal);
        Assert.True(DnsClient.TryParseType("mx", out var type));
        Assert.Equal(DnsRecordType.MX, type);
        Assert.False(DnsClient.TryParseType("opt", out _));
    }
}
