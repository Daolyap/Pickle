using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using Pickle.Abstractions.Services;
using Pickle.Core.Commands;

namespace Pickle.Core.SystemMonitoring;

/// <summary>
/// <see cref="INetworkMonitor"/>: interfaces from <see cref="NetworkInterface"/>, connections with owning processes from
/// GetExtendedTcpTable/GetExtendedUdpTable (Windows) or <c>/proc/net</c> + <c>/proc/&lt;pid&gt;/fd</c> (Linux), and
/// <see cref="IPGlobalProperties"/> without owners elsewhere.
/// </summary>
public sealed class NetworkMonitor : INetworkMonitor
{
    private readonly RateTracker _rates = new();
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly object _gate = new();
    private readonly string _proc;

    public NetworkMonitor()
        : this("/proc")
    {
    }

    internal NetworkMonitor(string proc) => _proc = proc;

    public bool CanFlushDns => OperatingSystem.IsWindows();

    public Task<IReadOnlyList<NetworkInterfaceSample>> SampleInterfacesAsync(CancellationToken cancellationToken = default) =>
        Task.Run(() => ApplyRates(ReadInterfaces(), _clock.Elapsed), cancellationToken);

    public Task<IReadOnlyList<NetworkConnection>> GetConnectionsAsync(CancellationToken cancellationToken = default) =>
        Task.Run(ReadConnections, cancellationToken);

    public async Task<PingResult> PingAsync(string host, TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        using var ping = new Ping();
        try
        {
            var reply = await ping.SendPingAsync(host, timeout, new byte[32], null, cancellationToken).ConfigureAwait(false);
            var address = reply.Address is { } a && !a.Equals(IPAddress.Any) ? a.ToString() : null;
            return new PingResult(reply.Status == IPStatus.Success, reply.Status.ToString(), address, reply.RoundtripTime, reply.Options?.Ttl);
        }
        catch (PingException ex)
        {
            return new PingResult(false, (ex.InnerException ?? ex).Message, null, 0, null);
        }
        catch (Exception ex) when (ex is ArgumentException or SocketException or InvalidOperationException)
        {
            return new PingResult(false, ex.Message, null, 0, null);
        }
    }

    public async Task<IReadOnlyList<string>> LookupAsync(string host, CancellationToken cancellationToken = default)
    {
        var addresses = await Dns.GetHostAddressesAsync(host, cancellationToken).ConfigureAwait(false);
        return [.. addresses.Select(a => a.ToString())];
    }

    public async Task<NetworkToolResult> FlushDnsAsync(CancellationToken cancellationToken = default)
    {
        if (!OperatingSystem.IsWindows())
        {
            return new NetworkToolResult(false, "Flushing the DNS cache is only available on Windows.");
        }

        var result = await ProcessRunner.RunAsync(ExecutableLocator.SystemProgram("ipconfig.exe"), ["/flushdns"], timeout: TimeSpan.FromSeconds(30), cancellationToken: cancellationToken).ConfigureAwait(false);
        var output = string.Join('\n', $"{result.StdOut}\n{result.StdErr}".Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries));
        return new NetworkToolResult(result.Success, output.Length > 0 ? output : result.Message);
    }

    /// <summary>Fills in send/receive rates from the counters' change since the previous call.</summary>
    internal IReadOnlyList<NetworkInterfaceSample> ApplyRates(IReadOnlyList<NetworkInterfaceSample> interfaces, TimeSpan now)
    {
        lock (_gate)
        {
            var keys = new HashSet<string>(StringComparer.Ordinal);
            var result = new List<NetworkInterfaceSample>(interfaces.Count);
            foreach (var nic in interfaces)
            {
                string tx = nic.Id + "/tx", rx = nic.Id + "/rx";
                keys.Add(tx);
                keys.Add(rx);
                result.Add(nic with { SendRate = _rates.Update(tx, nic.BytesSent, now), ReceiveRate = _rates.Update(rx, nic.BytesReceived, now) });
            }

            _rates.Retain(keys);
            return result;
        }
    }

    private static List<NetworkInterfaceSample> ReadInterfaces()
    {
        NetworkInterface[] interfaces;
        try
        {
            interfaces = NetworkInterface.GetAllNetworkInterfaces();
        }
        catch (NetworkInformationException)
        {
            return [];
        }

        var result = new List<NetworkInterfaceSample>(interfaces.Length);
        foreach (var nic in interfaces)
        {
            var (ipv4, ipv6) = Addresses(nic);
            var (sent, received) = Counters(nic);
            var mac = Try(() => nic.GetPhysicalAddress().GetAddressBytes()) is { Length: > 0 } bytes && bytes.Any(b => b != 0)
                ? string.Join(':', bytes.Select(b => b.ToString("X2", CultureInfo.InvariantCulture)))
                : null;
            result.Add(new NetworkInterfaceSample(
                nic.Id,
                nic.Name,
                nic.Description,
                Try(() => nic.NetworkInterfaceType.ToString()) ?? "Unknown",
                Try(() => nic.OperationalStatus.ToString()) ?? "Unknown",
                ipv4,
                ipv6,
                mac,
                TryValue(() => nic.Speed) is { } speed && speed > 0 ? speed : null,
                sent,
                received,
                0,
                0));
        }

        return result;
    }

    private static (List<string> IPv4, List<string> IPv6) Addresses(NetworkInterface nic)
    {
        List<string> ipv4 = [], ipv6 = [];
        var addresses = Try(() => nic.GetIPProperties().UnicastAddresses);
        foreach (var unicast in addresses ?? Enumerable.Empty<UnicastIPAddressInformation>())
        {
            var prefix = TryValue(() => unicast.PrefixLength);
            var text = unicast.Address.ToString() + (prefix is > 0 ? "/" + prefix.Value.ToString(CultureInfo.InvariantCulture) : string.Empty);
            (unicast.Address.AddressFamily == AddressFamily.InterNetworkV6 ? ipv6 : ipv4).Add(text);
        }

        return (ipv4, ipv6);
    }

    private static (long Sent, long Received) Counters(NetworkInterface nic)
    {
        if (Try(() => nic.GetIPStatistics()) is { } stats)
        {
            return (TryValue(() => stats.BytesSent) ?? 0, TryValue(() => stats.BytesReceived) ?? 0);
        }

        // macOS has no GetIPStatistics.
        return Try(() => nic.GetIPv4Statistics()) is { } v4 ? (TryValue(() => v4.BytesSent) ?? 0, TryValue(() => v4.BytesReceived) ?? 0) : (0, 0);
    }

    private IReadOnlyList<NetworkConnection> ReadConnections()
    {
        if (OperatingSystem.IsWindows() && ReadWindowsConnections() is { } windows)
        {
            return windows;
        }

        if (OperatingSystem.IsLinux() && Directory.Exists(Path.Join(_proc, "net")))
        {
            return ReadProcConnections();
        }

        return ReadGenericConnections();
    }

    private static List<NetworkConnection>? ReadWindowsConnections()
    {
        if (!OperatingSystem.IsWindows())
        {
            return null;
        }

        var result = new List<NetworkConnection>();
        var any = false;
        foreach (var (udp, ipv6) in new[] { (false, false), (false, true), (true, false), (true, true) })
        {
            if (WindowsSystemNative.ConnectionTable(udp, ipv6) is { } table)
            {
                any = true;
                result.AddRange(ConnectionTables.ParseWindowsTable(table, udp, ipv6));
            }
        }

        if (!any)
        {
            return null;
        }

        var names = new Dictionary<int, string?>();
        return [.. result.Select(c => c.ProcessId is { } pid ? c with { ProcessName = NameOf(pid, names) } : c)];
    }

    internal List<NetworkConnection> ReadProcConnections()
    {
        var entries = new List<(bool Udp, ProcNetEntry Entry)>();
        foreach (var (file, udp) in new[] { ("tcp", false), ("tcp6", false), ("udp", true), ("udp6", true) })
        {
            if (ReadFile(Path.Join(_proc, "net", file)) is { } text)
            {
                entries.AddRange(ProcFs.ParseNet(text, udp).Select(e => (udp, e)));
            }
        }

        var owners = SocketOwners([.. entries.Select(e => e.Entry.Inode).Where(i => i > 0)]);
        var names = new Dictionary<int, string?>();
        return [.. entries.Select(e =>
        {
            int? pid = owners.TryGetValue(e.Entry.Inode, out var owner) ? owner : null;
            var connection = e.Udp
                ? ConnectionTables.Udp(e.Entry.Local, e.Entry.LocalPort, pid)
                : ConnectionTables.Tcp(e.Entry.Local, e.Entry.LocalPort, e.Entry.Remote, e.Entry.RemotePort, e.Entry.State, pid);
            return pid is { } id ? connection with { ProcessName = ProcName(id, names) } : connection;
        })];
    }

    /// <summary>socket inode → pid, from the <c>socket:[inode]</c> links in <c>/proc/&lt;pid&gt;/fd</c> we are allowed to read.</summary>
    private Dictionary<long, int> SocketOwners(HashSet<long> inodes)
    {
        var owners = new Dictionary<long, int>();
        if (inodes.Count == 0)
        {
            return owners;
        }

        foreach (var directory in SafeDirectories(_proc))
        {
            if (!int.TryParse(Path.GetFileName(directory), NumberStyles.None, CultureInfo.InvariantCulture, out var pid))
            {
                continue;
            }

            foreach (var fd in SafeEntries(Path.Join(directory, "fd")))
            {
                string? target;
                try
                {
                    target = new FileInfo(fd).LinkTarget;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    continue;
                }

                if (target is not null && target.StartsWith("socket:[", StringComparison.Ordinal) && target.EndsWith(']') &&
                    long.TryParse(target.AsSpan(8, target.Length - 9), NumberStyles.None, CultureInfo.InvariantCulture, out var inode) &&
                    inodes.Contains(inode))
                {
                    owners.TryAdd(inode, pid);
                }
            }

            if (owners.Count == inodes.Count)
            {
                break;
            }
        }

        return owners;
    }

    private string? ProcName(int pid, Dictionary<int, string?> cache)
    {
        if (!cache.TryGetValue(pid, out var name))
        {
            name = ReadFile(Path.Join(_proc, pid.ToString(CultureInfo.InvariantCulture), "comm"))?.Trim();
            cache[pid] = name;
        }

        return name;
    }

    private static string? NameOf(int pid, Dictionary<int, string?> cache)
    {
        if (!cache.TryGetValue(pid, out var name))
        {
            try
            {
                using var process = Process.GetProcessById(pid);
                name = process.ProcessName;
            }
            catch (Exception ex) when (ProcessMonitor.IsInaccessible(ex))
            {
                name = null;
            }

            cache[pid] = name;
        }

        return name;
    }

    private static List<NetworkConnection> ReadGenericConnections()
    {
        var result = new List<NetworkConnection>();
        IPGlobalProperties properties;
        try
        {
            properties = IPGlobalProperties.GetIPGlobalProperties();
        }
        catch (Exception ex) when (ex is NetworkInformationException or PlatformNotSupportedException)
        {
            return result;
        }

        foreach (var c in Try(properties.GetActiveTcpConnections) ?? [])
        {
            result.Add(ConnectionTables.Tcp(c.LocalEndPoint.Address, c.LocalEndPoint.Port, c.RemoteEndPoint.Address, c.RemoteEndPoint.Port, c.State.ToString(), null));
        }

        foreach (var endpoint in Try(properties.GetActiveTcpListeners) ?? [])
        {
            result.Add(ConnectionTables.Tcp(endpoint.Address, endpoint.Port, IPAddress.Any, 0, nameof(TcpState.Listen), null));
        }

        foreach (var endpoint in Try(properties.GetActiveUdpListeners) ?? [])
        {
            result.Add(ConnectionTables.Udp(endpoint.Address, endpoint.Port, null));
        }

        return result;
    }

    private static IEnumerable<string> SafeDirectories(string path)
    {
        try
        {
            return Directory.EnumerateDirectories(path).ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    private static IEnumerable<string> SafeEntries(string path)
    {
        try
        {
            return Directory.EnumerateFileSystemEntries(path).ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    private static string? ReadFile(string path)
    {
        try
        {
            return File.ReadAllText(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static T? Try<T>(Func<T> read)
        where T : class
    {
        try
        {
            return read();
        }
        catch (Exception ex) when (ex is NetworkInformationException or PlatformNotSupportedException or NotSupportedException or InvalidOperationException)
        {
            return null;
        }
    }

    private static T? TryValue<T>(Func<T> read)
        where T : struct
    {
        try
        {
            return read();
        }
        catch (Exception ex) when (ex is NetworkInformationException or PlatformNotSupportedException or NotSupportedException or InvalidOperationException)
        {
            return null;
        }
    }
}
