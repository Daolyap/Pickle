using System.Buffers.Binary;
using System.Net;
using Pickle.Abstractions.Services;
using Pickle.Core.SystemMonitoring;

namespace Pickle.Core.Tests.SystemMonitoring;

public sealed class NetworkMonitorTests : IDisposable
{
    private readonly string _proc = Path.Combine(Path.GetTempPath(), "pickle-net-tests", Guid.NewGuid().ToString("N")[..10]);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_proc, recursive: true);
        }
        catch (DirectoryNotFoundException)
        {
        }
    }

    private static NetworkInterfaceSample Nic(string id, long sent, long received) =>
        new(id, id, id, "Ethernet", "Up", ["10.0.0.2/24"], [], "AA:BB:CC:DD:EE:FF", 1_000_000_000, sent, received, 0, 0);

    [Fact]
    public void RateTrackerDividesDeltasByElapsedTime()
    {
        var rates = new RateTracker();

        Assert.Equal(0, rates.Update("a", 1000, TimeSpan.FromSeconds(1)));
        Assert.Equal(500, rates.Update("a", 2000, TimeSpan.FromSeconds(3)));
        Assert.Equal(0, rates.Update("a", 100, TimeSpan.FromSeconds(4)));
        Assert.Equal(0, rates.Update("a", 100, TimeSpan.FromSeconds(4)));

        rates.Retain(new HashSet<string>());
        Assert.Equal(0, rates.Update("a", 5000, TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public void InterfaceThroughputComesFromCounterDeltas()
    {
        var monitor = new NetworkMonitor(_proc);

        var first = monitor.ApplyRates([Nic("eth0", 1_000, 5_000)], TimeSpan.FromSeconds(10));
        var second = monitor.ApplyRates([Nic("eth0", 3_000, 11_000), Nic("wlan0", 50, 50)], TimeSpan.FromSeconds(12));

        Assert.Equal((0.0, 0.0), (first[0].SendRate, first[0].ReceiveRate));
        Assert.Equal((1_000.0, 3_000.0), (second[0].SendRate, second[0].ReceiveRate));
        Assert.Equal((0.0, 0.0), (second[1].SendRate, second[1].ReceiveRate));
    }

    [Fact]
    public async Task RealInterfacesAndConnectionsCanBeRead()
    {
        var monitor = new NetworkMonitor();

        var interfaces = await monitor.SampleInterfacesAsync();
        var connections = await monitor.GetConnectionsAsync();

        Assert.All(interfaces, i => Assert.False(string.IsNullOrEmpty(i.Name)));
        Assert.All(connections, c => Assert.Contains(c.Protocol, new[] { "TCP", "UDP" }));
    }

    [Fact]
    public void ProcNetConnectionsAreMappedToTheirProcesses()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), "reads a /proc-style tree with symlinks");
        Directory.CreateDirectory(Path.Combine(_proc, "net"));
        File.WriteAllText(Path.Combine(_proc, "net", "tcp"), """
              sl  local_address rem_address   st tx_queue rx_queue tr tm->when retrnsmt   uid  timeout inode
               0: 00000000:1F90 00000000:0000 0A 00000000:00000000 00:00000000 00000000     0        0 3144 1 0000000000000000 100 0 0 10 0
               1: 0100007F:A1B2 0100007F:1F90 01 00000000:00000000 00:00000000 00000000  1000        0 5555 1 0000000000000000 20 4 30 10 -1
            """);
        File.WriteAllText(Path.Combine(_proc, "net", "udp"), """
              sl  local_address rem_address   st tx_queue rx_queue tr tm->when retrnsmt   uid  timeout inode ref pointer drops
             10: 00000000:14E9 00000000:0000 07 00000000:00000000 00:00000000 00000000     0        0 7777 2 0000000000000000 0
            """);
        Directory.CreateDirectory(Path.Combine(_proc, "321", "fd"));
        File.WriteAllText(Path.Combine(_proc, "321", "comm"), "webserver\n");
        File.CreateSymbolicLink(Path.Combine(_proc, "321", "fd", "3"), "socket:[3144]");
        File.CreateSymbolicLink(Path.Combine(_proc, "321", "fd", "4"), "/dev/null");
        Directory.CreateDirectory(Path.Combine(_proc, "654", "fd"));
        File.WriteAllText(Path.Combine(_proc, "654", "comm"), "resolver\n");
        File.CreateSymbolicLink(Path.Combine(_proc, "654", "fd", "9"), "socket:[7777]");

        var connections = new NetworkMonitor(_proc).ReadProcConnections();

        Assert.Equal(3, connections.Count);
        Assert.Equal(new NetworkConnection("TCP", "0.0.0.0", 8080, null, null, "Listen", 321, "webserver"), connections[0]);
        Assert.Equal(new NetworkConnection("TCP", "127.0.0.1", 41394, "127.0.0.1", 8080, "Established"), connections[1]);
        Assert.Equal(new NetworkConnection("UDP", "0.0.0.0", 5353, null, null, "Listen", 654, "resolver"), connections[2]);
    }

    [Fact]
    public void WindowsOwnerPidTablesAreParsed()
    {
        var tcp = new byte[4 + 24];
        BinaryPrimitives.WriteInt32LittleEndian(tcp, 1);
        BinaryPrimitives.WriteUInt32LittleEndian(tcp.AsSpan(4), 5);
        IPAddress.Parse("192.168.1.10").GetAddressBytes().CopyTo(tcp, 8);
        tcp[12] = 0xC3;
        tcp[13] = 0x50;
        IPAddress.Parse("140.82.112.3").GetAddressBytes().CopyTo(tcp, 16);
        tcp[20] = 0x01;
        tcp[21] = 0xBB;
        BinaryPrimitives.WriteInt32LittleEndian(tcp.AsSpan(24), 4321);

        var udp6 = new byte[4 + 28];
        BinaryPrimitives.WriteInt32LittleEndian(udp6, 1);
        IPAddress.IPv6Loopback.GetAddressBytes().CopyTo(udp6, 4);
        udp6[24] = 0x00;
        udp6[25] = 0x35;
        BinaryPrimitives.WriteInt32LittleEndian(udp6.AsSpan(28), 99);

        Assert.Equal(
            new NetworkConnection("TCP", "192.168.1.10", 50000, "140.82.112.3", 443, "Established", 4321),
            Assert.Single(ConnectionTables.ParseWindowsTable(tcp, udp: false, ipv6: false)));
        Assert.Equal(
            new NetworkConnection("UDP", "::1", 53, null, null, "Listen", 99),
            Assert.Single(ConnectionTables.ParseWindowsTable(udp6, udp: true, ipv6: true)));
        Assert.Empty(ConnectionTables.ParseWindowsTable([1, 0], udp: false, ipv6: false));
    }

    [Fact]
    public async Task FlushDnsIsWindowsOnly()
    {
        var monitor = new NetworkMonitor();
        Assert.Equal(OperatingSystem.IsWindows(), monitor.CanFlushDns);
        if (!OperatingSystem.IsWindows())
        {
            Assert.False((await monitor.FlushDnsAsync()).Success);
        }
    }

    [Fact]
    public async Task PingAndLookupWorkForLocalhost()
    {
        var monitor = new NetworkMonitor();

        var addresses = await monitor.LookupAsync("localhost");
        var reply = await monitor.PingAsync("127.0.0.1", TimeSpan.FromSeconds(2));

        Assert.NotEmpty(addresses);
        Assert.False(string.IsNullOrEmpty(reply.Status));
    }
}
