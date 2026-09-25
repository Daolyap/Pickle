using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.CompilerServices;

namespace Pickle.Network;

/// <param name="Address">The router that answered, or null when the hop timed out.</param>
public sealed record TraceHop(int Hop, string? Address, TimeSpan? Latency, string? HostName, bool Reached);

/// <summary>ICMP traceroute: pings with a growing TTL and reports who sent back "time exceeded".</summary>
public static class Traceroute
{
    public static async IAsyncEnumerable<TraceHop> TraceAsync(
        string host,
        int maxHops,
        TimeSpan timeout,
        bool resolveNames = true,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var target = await PortScanner.ResolveAsync(host, cancellationToken).ConfigureAwait(false);
        using var ping = new Ping();
        for (var ttl = 1; ttl <= Math.Clamp(maxHops, 1, 64); ttl++)
        {
            PingReply? reply = null;
            try
            {
                reply = await ping.SendPingAsync(target, timeout, buffer: null, new PingOptions(ttl, dontFragment: true), cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is PingException or PlatformNotSupportedException or SocketException)
            {
            }

            var answered = reply is { Status: IPStatus.Success or IPStatus.TtlExpired or IPStatus.TimeExceeded } && !IPAddress.Any.Equals(reply.Address);
            var address = answered ? reply!.Address : null;
            var name = address is not null && resolveNames ? await HostSweep.ReverseLookupAsync(address, cancellationToken).ConfigureAwait(false) : null;
            var reached = reply?.Status == IPStatus.Success;
            yield return new TraceHop(ttl, address?.ToString(), answered ? TimeSpan.FromMilliseconds(reply!.RoundtripTime) : null, name, reached);
            if (reached)
            {
                yield break;
            }
        }
    }
}
