using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading.Channels;

namespace Pickle.Network;

public enum PortState
{
    Open,
    Closed,
    Filtered,
}

/// <param name="Progress">Probes finished so far and in total (reported every few probes).</param>
public sealed record PortScanOptions(TimeSpan Timeout, int Concurrency = 256, bool GrabBanner = false, bool IncludeClosed = false, IProgress<(int Done, int Total)>? Progress = null);

public sealed record PortScanResult(string Host, string Address, int Port, PortState State, string Service, TimeSpan? Latency, string? Banner = null);

/// <summary>
/// TCP connect scan (no raw packets, so it needs no administrator rights and no driver): open = the handshake
/// completed, closed = refused, filtered = no answer before the timeout.
/// </summary>
public static class PortScanner
{
    /// <summary>Results as they complete (open ports, and closed/filtered ones when asked for); unresolvable hosts throw.</summary>
    public static async IAsyncEnumerable<PortScanResult> ScanAsync(
        IReadOnlyList<string> hosts,
        IReadOnlyList<int> ports,
        PortScanOptions options,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var channel = Channel.CreateBounded<PortScanResult>(1024);
        var producer = Task.Run(
            async () =>
            {
                try
                {
                    using var gate = new SemaphoreSlim(Math.Clamp(options.Concurrency, 1, 2048));
                    var probes = new List<Task>();
                    var total = hosts.Count * ports.Count;
                    var done = 0;
                    foreach (var host in hosts)
                    {
                        var address = await ResolveAsync(host, cancellationToken).ConfigureAwait(false);
                        foreach (var port in ports)
                        {
                            await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
                            probes.Add(ProbeAsync(host, address, port));
                        }

                        probes.RemoveAll(p => p.IsCompleted);
                    }

                    await Task.WhenAll(probes).ConfigureAwait(false);

                    async Task ProbeAsync(string host, IPAddress address, int port)
                    {
                        try
                        {
                            var result = await ConnectOneAsync(host, address, port, options, cancellationToken).ConfigureAwait(false);
                            if (result.State == PortState.Open || options.IncludeClosed)
                            {
                                await channel.Writer.WriteAsync(result, cancellationToken).ConfigureAwait(false);
                            }
                        }
                        finally
                        {
                            gate.Release();
                            var finished = Interlocked.Increment(ref done);
                            if (options.Progress is { } progress && (finished % 50 == 0 || finished == total))
                            {
                                progress.Report((finished, total));
                            }
                        }
                    }

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

    public static async Task<IPAddress> ResolveAsync(string host, CancellationToken cancellationToken)
    {
        if (NetTargets.TryParseLiteral(host, out var literal))
        {
            return literal;
        }

        IPAddress[] addresses;
        try
        {
            addresses = await Dns.GetHostAddressesAsync(host, cancellationToken).ConfigureAwait(false);
        }
        catch (SocketException ex)
        {
            throw new ArgumentException($"Can't resolve '{host}': {ex.Message}", ex);
        }

        return addresses.FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork)
            ?? addresses.FirstOrDefault()
            ?? throw new ArgumentException($"'{host}' has no addresses.");
    }

    private static async Task<PortScanResult> ConnectOneAsync(string host, IPAddress address, int port, PortScanOptions options, CancellationToken cancellationToken)
    {
        using var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(options.Timeout);
        var watch = Stopwatch.StartNew();
        PortState state;
        try
        {
            await socket.ConnectAsync(new IPEndPoint(address, port), timeout.Token).ConfigureAwait(false);
            state = PortState.Open;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            state = PortState.Filtered;
        }
        catch (SocketException ex) when (ex.SocketErrorCode == SocketError.ConnectionRefused)
        {
            state = PortState.Closed;
        }
        catch (SocketException)
        {
            state = PortState.Filtered;
        }

        var latency = state == PortState.Filtered ? (TimeSpan?)null : watch.Elapsed;
        string? banner = null;
        if (state == PortState.Open && options.GrabBanner)
        {
            banner = await ReadBannerAsync(socket, host, port, cancellationToken).ConfigureAwait(false);
        }

        return new PortScanResult(host, address.ToString(), port, state, PortCatalog.Name(port), latency, banner);
    }

    /// <summary>What the service says first (SSH, FTP, SMTP…), or the Server header for HTTP; null when silent.</summary>
    internal static async Task<string?> ReadBannerAsync(Socket socket, string host, int port, CancellationToken cancellationToken)
    {
        if (PortCatalog.IsTls(port))
        {
            return null;
        }

        var head = Encoding.ASCII.GetBytes($"HEAD / HTTP/1.0\r\nHost: {host}\r\nUser-Agent: pickle\r\n\r\n");
        var buffer = new byte[1024];
        try
        {
            // Like nmap: wait for a greeting (SSH, SMTP, FTP…); a silent service gets an HTTP request instead.
            if (PortCatalog.IsHttp(port))
            {
                await socket.SendAsync(head, SocketFlags.None, cancellationToken).ConfigureAwait(false);
            }
            else if (await ReceiveAsync(socket, buffer, TimeSpan.FromMilliseconds(1500), cancellationToken).ConfigureAwait(false) is { } greeting)
            {
                return greeting;
            }
            else
            {
                await socket.SendAsync(head, SocketFlags.None, cancellationToken).ConfigureAwait(false);
            }

            return await ReceiveAsync(socket, buffer, TimeSpan.FromMilliseconds(1500), cancellationToken).ConfigureAwait(false);
        }
        catch (SocketException)
        {
            return null;
        }
    }

    private static async Task<string?> ReceiveAsync(Socket socket, byte[] buffer, TimeSpan wait, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(wait);
        try
        {
            var read = await socket.ReceiveAsync(buffer, SocketFlags.None, timeout.Token).ConfigureAwait(false);
            return read <= 0 ? null : SummarizeBanner(Encoding.ASCII.GetString(buffer, 0, read));
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return null;
        }
    }

    /// <summary>First line of a banner; for HTTP the status plus the Server header.</summary>
    internal static string SummarizeBanner(string raw)
    {
        var lines = raw.Replace("\r", string.Empty, StringComparison.Ordinal).Split('\n');
        var first = Printable(lines[0]);
        if (first.StartsWith("HTTP/", StringComparison.Ordinal))
        {
            var server = lines.FirstOrDefault(l => l.StartsWith("Server:", StringComparison.OrdinalIgnoreCase));
            return server is null ? first : first + " · " + Printable(server[7..].Trim());
        }

        return first;
    }

    private static string Printable(string text)
    {
        var clean = new string([.. text.Where(c => c >= ' ' && c < 127)]).Trim();
        return clean.Length > 100 ? clean[..99] + "…" : clean;
    }
}
