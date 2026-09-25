using System.Net;
using System.Net.Sockets;
using System.Numerics;

namespace Pickle.Network;

public sealed record SubnetInfo(
    string Cidr,
    string Network,
    int Prefix,
    string Mask,
    string Wildcard,
    string? Broadcast,
    string FirstHost,
    string LastHost,
    BigInteger Hosts,
    string Kind);

/// <summary>Subnet maths for IPv4 and IPv6 (<c>pk subnet 10.1.2.3/22</c>, splitting into smaller blocks).</summary>
public static class Subnet
{
    public static SubnetInfo Calculate(string cidr)
    {
        var (address, prefix) = Parse(cidr);
        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            var mask = prefix == 0 ? 0u : uint.MaxValue << (32 - prefix);
            var network = NetTargets.ToUInt(address) & mask;
            var broadcast = network | ~mask;
            var (first, last, hosts) = prefix switch
            {
                32 => (network, network, 1L),
                31 => (network, broadcast, 2L),
                _ => (network + 1, broadcast - 1, (long)broadcast - network - 1),
            };
            return new SubnetInfo(
                $"{NetTargets.FromUInt(network)}/{prefix}",
                NetTargets.FromUInt(network).ToString(),
                prefix,
                NetTargets.FromUInt(mask).ToString(),
                NetTargets.FromUInt(~mask).ToString(),
                prefix >= 31 ? null : NetTargets.FromUInt(broadcast).ToString(),
                NetTargets.FromUInt(first).ToString(),
                NetTargets.FromUInt(last).ToString(),
                hosts,
                Kind4(network));
        }

        var value = new BigInteger(address.GetAddressBytes(), isUnsigned: true, isBigEndian: true);
        var hostBits = 128 - prefix;
        var hostMask = (BigInteger.One << hostBits) - 1;
        var all = (BigInteger.One << 128) - 1;
        var start = value & (all ^ hostMask);
        var end = start | hostMask;
        return new SubnetInfo(
            $"{ToAddress(start)}/{prefix}",
            ToAddress(start).ToString(),
            prefix,
            ToAddress(all ^ hostMask).ToString(),
            ToAddress(hostMask).ToString(),
            null,
            ToAddress(start).ToString(),
            ToAddress(end).ToString(),
            BigInteger.One << hostBits,
            Kind6(start));
    }

    /// <summary>The blocks of size <paramref name="newPrefix"/> inside <paramref name="cidr"/> (IPv4, at most 4096).</summary>
    public static IReadOnlyList<string> Split(string cidr, int newPrefix)
    {
        var (address, prefix) = Parse(cidr);
        if (address.AddressFamily != AddressFamily.InterNetwork)
        {
            throw new ArgumentException("Splitting works for IPv4 blocks.");
        }

        if (newPrefix <= prefix || newPrefix > 32)
        {
            throw new ArgumentException($"Split into a longer prefix than /{prefix} (up to /32).");
        }

        if (newPrefix - prefix > 12)
        {
            throw new ArgumentException("That would be more than 4096 blocks.");
        }

        var network = NetTargets.ToUInt(address) & (prefix == 0 ? 0u : uint.MaxValue << (32 - prefix));
        var size = 1u << (32 - newPrefix);
        return [.. Enumerable.Range(0, 1 << (newPrefix - prefix)).Select(i => $"{NetTargets.FromUInt(network + ((uint)i * size))}/{newPrefix}")];
    }

    public static bool Contains(string cidr, string ip)
    {
        var info = Calculate(cidr);
        var (network, prefix) = Parse(info.Cidr);
        return NetTargets.TryParseLiteral(ip, out var address) && address.AddressFamily == network.AddressFamily
            && Calculate($"{address}/{prefix}").Network == info.Network;
    }

    private static (IPAddress Address, int Prefix) Parse(string cidr)
    {
        var text = cidr.Trim();
        var slash = text.IndexOf('/', StringComparison.Ordinal);
        if (!NetTargets.TryParseLiteral(slash > 0 ? text[..slash] : text, out var address))
        {
            throw new ArgumentException($"'{cidr}' is not an address or CIDR block.");
        }

        var max = address.AddressFamily == AddressFamily.InterNetwork ? 32 : 128;
        var prefix = max;
        if (slash > 0)
        {
            var tail = text[(slash + 1)..];
            if (tail.Contains('.', StringComparison.Ordinal) && IPAddress.TryParse(tail, out var mask) && mask.AddressFamily == AddressFamily.InterNetwork && max == 32)
            {
                prefix = BitOperations.PopCount(NetTargets.ToUInt(mask));
            }
            else if (!int.TryParse(tail, out prefix) || prefix < 0 || prefix > max)
            {
                throw new ArgumentException($"'{cidr}': the prefix must be 0–{max} (or a dotted mask).");
            }
        }

        return (address, prefix);
    }

    private static IPAddress ToAddress(BigInteger value)
    {
        var bytes = value.ToByteArray(isUnsigned: true, isBigEndian: true);
        var full = new byte[16];
        bytes.CopyTo(full, 16 - Math.Min(16, bytes.Length));
        return new IPAddress(full);
    }

    private static string Kind4(uint n) =>
        (n >> 24) == 10 || (n >> 20) == 0xAC1 || (n >> 16) == 0xC0A8 ? "private (RFC 1918)"
        : (n >> 24) == 127 ? "loopback"
        : (n >> 16) == 0xA9FE ? "link-local (APIPA)"
        : (n >> 22) == 0x191 ? "shared (CGNAT)"
        : (n >> 28) == 0xE ? "multicast"
        : "public";

    private static string Kind6(BigInteger start)
    {
        var top = (int)(start >> 112);
        return (top & 0xFE00) == 0xFC00 ? "unique local (private)"
            : (top & 0xFFC0) == 0xFE80 ? "link-local"
            : (top & 0xFF00) == 0xFF00 ? "multicast"
            : start == BigInteger.One ? "loopback"
            : "global";
    }
}
