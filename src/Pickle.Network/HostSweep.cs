using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Threading.Channels;

namespace Pickle.Network;

public sealed record SweepOptions(TimeSpan Timeout, int Concurrency = 128, bool TcpProbe = true, bool ResolveNames = true, bool IncludeDown = false, IProgress<(int Done, int Total)>? Progress = null);

/// <param name="Method">How the host answered: "icmp" or "tcp/445" etc.</param>
public sealed record SweepResult(string Address, bool Up, string? Method, TimeSpan? Latency, string? HostName, string? Mac);

/// <summary>
/// Host discovery (nmap -sn): ping each address, and when ICMP is blocked try a few common TCP ports — a refused
/// connection also proves the host is there. Up hosts get a reverse-DNS name and, on the local subnet, a MAC address.
/// </summary>
public static class HostSweep
{
    public static readonly int[] ProbePorts = [445, 80, 443, 22, 3389, 135, 139];

    public static async IAsyncEnumerable<SweepResult> SweepAsync(
        IReadOnlyList<string> addresses,
        SweepOptions options,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var channel = Channel.CreateBounded<SweepResult>(512);
        var producer = Task.Run(
            async () =>
            {
                try
                {
                    using var gate = new SemaphoreSlim(Math.Clamp(options.Concurrency, 1, 1024));
                    var probes = new List<Task>();
                    var done = 0;
                    foreach (var text in addresses)
                    {
                        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
                        probes.Add(Task.Run(
                            async () =>
                            {
                                try
                                {
                                    var result = await ProbeHostAsync(text, options, cancellationToken).ConfigureAwait(false);
                                    if (result.Up || options.IncludeDown)
                                    {
                                        await channel.Writer.WriteAsync(result, cancellationToken).ConfigureAwait(false);
                                    }
                                }
                                finally
                                {
                                    gate.Release();
                                    var finished = Interlocked.Increment(ref done);
                                    if (options.Progress is { } progress && (finished % 8 == 0 || finished == addresses.Count))
                                    {
                                        progress.Report((finished, addresses.Count));
                                    }
                                }
                            },
                            cancellationToken));
                        probes.RemoveAll(p => p.IsCompleted);
                    }

                    await Task.WhenAll(probes).ConfigureAwait(false);
                    channel.Writer.Complete();
                }
                catch (Exception ex)
                {
                    channel.Writer.Complete(ex);
                }
            },
            cancellationToken);

        await foreach (var result in channel.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
        {
            yield return result;
        }

        await producer.ConfigureAwait(false);
    }

    private static async Task<SweepResult> ProbeHostAsync(string text, SweepOptions options, CancellationToken cancellationToken)
    {
        var address = await PortScanner.ResolveAsync(text, cancellationToken).ConfigureAwait(false);
        var (up, method, latency) = await PingAsync(address, options.Timeout, cancellationToken).ConfigureAwait(false);
        if (!up && options.TcpProbe)
        {
            (up, method, latency) = await TcpProbeAsync(address, options.Timeout, cancellationToken).ConfigureAwait(false);
        }

        if (!up)
        {
            return new SweepResult(address.ToString(), false, null, null, null, null);
        }

        string? name = null;
        if (options.ResolveNames)
        {
            name = await ReverseLookupAsync(address, cancellationToken).ConfigureAwait(false);
        }

        return new SweepResult(address.ToString(), true, method, latency, name, ArpTable.Lookup(address));
    }

    internal static async Task<(bool Up, string? Method, TimeSpan? Latency)> PingAsync(IPAddress address, TimeSpan timeout, CancellationToken cancellationToken)
    {
        try
        {
            using var ping = new Ping();

            // No custom payload: unprivileged ICMP sockets (Linux, macOS) only allow the default one.
            var reply = await ping.SendPingAsync(address, timeout, buffer: null, options: null, cancellationToken).ConfigureAwait(false);
            return reply.Status == IPStatus.Success ? (true, "icmp", TimeSpan.FromMilliseconds(reply.RoundtripTime)) : (false, null, null);
        }
        catch (Exception ex) when (ex is PingException or PlatformNotSupportedException or InvalidOperationException or SocketException)
        {
            return (false, null, null);
        }
    }

    private static async Task<(bool Up, string? Method, TimeSpan? Latency)> TcpProbeAsync(IPAddress address, TimeSpan timeout, CancellationToken cancellationToken)
    {
        using var first = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        first.CancelAfter(timeout);
        var watch = Stopwatch.StartNew();
        var attempts = ProbePorts.Select(async port =>
        {
            using var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
            try
            {
                await socket.ConnectAsync(new IPEndPoint(address, port), first.Token).ConfigureAwait(false);
                return (Up: true, Port: port);
            }
            catch (SocketException ex) when (ex.SocketErrorCode == SocketError.ConnectionRefused)
            {
                return (Up: true, Port: port);
            }
            catch (Exception ex) when (ex is SocketException or OperationCanceledException)
            {
                return (Up: false, Port: port);
            }
        }).ToList();

        while (attempts.Count > 0)
        {
            var done = await Task.WhenAny(attempts).ConfigureAwait(false);
            attempts.Remove(done);
            var (up, port) = await done.ConfigureAwait(false);
            if (up)
            {
                var latency = watch.Elapsed;
                await first.CancelAsync().ConfigureAwait(false);
                return (true, "tcp/" + port, latency);
            }
        }

        return (false, null, null);
    }

    internal static async Task<string?> ReverseLookupAsync(IPAddress address, CancellationToken cancellationToken)
    {
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(2));
            var entry = await Dns.GetHostEntryAsync(address.ToString(), timeout.Token).ConfigureAwait(false);
            return string.Equals(entry.HostName, address.ToString(), StringComparison.Ordinal) ? null : entry.HostName;
        }
        catch (Exception ex) when (ex is SocketException or OperationCanceledException or ArgumentException)
        {
            return null;
        }
    }
}

/// <summary>MAC addresses of hosts on the local subnet (Windows: SendARP; Linux: /proc/net/arp).</summary>
public static partial class ArpTable
{
    public static string? Lookup(IPAddress address)
    {
        if (address.AddressFamily != AddressFamily.InterNetwork)
        {
            return null;
        }

        try
        {
            if (OperatingSystem.IsWindows())
            {
                return SendArp(address);
            }

            return File.Exists("/proc/net/arp") ? ParseProcArp(File.ReadAllText("/proc/net/arp"), address.ToString()) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or DllNotFoundException or EntryPointNotFoundException)
        {
            return null;
        }
    }

    internal static string? ParseProcArp(string text, string ip)
    {
        foreach (var line in text.Split('\n').Skip(1))
        {
            var columns = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (columns.Length >= 4 && columns[0] == ip && columns[3] != "00:00:00:00:00:00")
            {
                return columns[3].ToUpperInvariant().Replace(':', '-');
            }
        }

        return null;
    }

    [SupportedOSPlatform("windows")]
    private static unsafe string? SendArp(IPAddress address)
    {
        var destination = BitConverter.ToUInt32(address.GetAddressBytes(), 0);
        var mac = stackalloc byte[8];
        var length = 8;
        if (SendARP(destination, 0, mac, ref length) != 0 || length < 6)
        {
            return null;
        }

        return string.Join('-', Enumerable.Range(0, 6).Select(i => mac[i].ToString("X2", System.Globalization.CultureInfo.InvariantCulture)));
    }

    [LibraryImport("iphlpapi.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static unsafe partial int SendARP(uint destIp, uint srcIp, byte* macAddress, ref int physicalAddressLength);
}
