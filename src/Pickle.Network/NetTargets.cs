using System.Net;
using System.Net.Sockets;

namespace Pickle.Network;

/// <summary>
/// Expands target specs like nmap does: <c>10.0.0.5</c>, <c>host.example</c>, <c>10.0.0.0/24</c>,
/// <c>10.0.0.1-50</c>, <c>10.0.0.1-10.0.0.20</c>, separated by commas or spaces.
/// </summary>
public static class NetTargets
{
    public const int MaxHosts = 65536;

    /// <param name="skipNetworkAndBroadcast">For a CIDR block of /30 or larger, leave out its first and last address.</param>
    public static IReadOnlyList<string> Expand(string spec, bool skipNetworkAndBroadcast = true, int max = MaxHosts)
    {
        var result = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var part in spec.Split([',', ' ', ';', '\t', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            foreach (var host in ExpandOne(part, skipNetworkAndBroadcast))
            {
                if (seen.Add(host))
                {
                    result.Add(host);
                    if (result.Count > max)
                    {
                        throw new ArgumentException($"'{spec}' covers more than {max:N0} hosts; narrow it down.");
                    }
                }
            }
        }

        return result;
    }

    private static IEnumerable<string> ExpandOne(string part, bool skipEnds)
    {
        var slash = part.IndexOf('/', StringComparison.Ordinal);
        if (slash > 0)
        {
            if (!TryParseLiteral(part[..slash], out var baseAddress) || !int.TryParse(part[(slash + 1)..], out var prefix))
            {
                throw new ArgumentException($"'{part}' is not a CIDR block like 192.168.1.0/24.");
            }

            if (baseAddress.AddressFamily != AddressFamily.InterNetwork)
            {
                if (prefix < 112 || prefix > 128)
                {
                    throw new ArgumentException($"IPv6 blocks must be /112 or smaller ('{part}').");
                }

                return Range6(baseAddress, prefix);
            }

            if (prefix is < 0 or > 32)
            {
                throw new ArgumentException($"'{part}': the prefix must be 0–32.");
            }

            var mask = prefix == 0 ? 0u : uint.MaxValue << (32 - prefix);
            var network = ToUInt(baseAddress) & mask;
            var last = network | ~mask;
            if (skipEnds && prefix <= 30)
            {
                network++;
                last--;
            }

            return Range(network, last);
        }

        var dash = part.IndexOf('-', StringComparison.Ordinal);
        if (dash > 0 && TryParseLiteral(part[..dash], out var start) && start.AddressFamily == AddressFamily.InterNetwork)
        {
            var tail = part[(dash + 1)..];
            uint end;
            if (TryParseLiteral(tail, out var endAddress) && endAddress.AddressFamily == AddressFamily.InterNetwork)
            {
                end = ToUInt(endAddress);
            }
            else if (byte.TryParse(tail, out var lastOctet))
            {
                end = (ToUInt(start) & 0xFFFFFF00u) | lastOctet;
            }
            else
            {
                throw new ArgumentException($"'{part}' is not a range like 10.0.0.1-50 or 10.0.0.1-10.0.0.20.");
            }

            var from = ToUInt(start);
            if (end < from)
            {
                throw new ArgumentException($"'{part}': the range ends before it starts.");
            }

            return Range(from, end);
        }

        if (!IsHostName(part) && !TryParseLiteral(part, out _))
        {
            throw new ArgumentException($"'{part}' is not an address, host name, CIDR block or range.");
        }

        return [part];
    }

    private static IEnumerable<string> Range(uint from, uint to)
    {
        if ((ulong)to - from + 1 > MaxHosts)
        {
            throw new ArgumentException($"That covers more than {MaxHosts:N0} hosts; narrow it down.");
        }

        for (var value = (ulong)from; value <= to; value++)
        {
            yield return FromUInt((uint)value).ToString();
        }
    }

    private static IEnumerable<string> Range6(IPAddress baseAddress, int prefix)
    {
        var bytes = baseAddress.GetAddressBytes();
        var hostBits = 128 - prefix;
        var count = 1 << hostBits;
        var fixedLow = (bytes[14] << 8) | bytes[15];
        var mask = (0xFFFF << hostBits) & 0xFFFF;
        for (var i = 0; i < count; i++)
        {
            var low = (fixedLow & mask) | i;
            bytes[14] = (byte)(low >> 8);
            bytes[15] = (byte)low;
            yield return new IPAddress(bytes).ToString();
        }
    }

    /// <summary>
    /// A written-out IPv4 (four parts) or IPv6 address. <see cref="IPAddress.TryParse(string, out IPAddress)"/> alone
    /// also takes "22" (0.0.0.22) and "10.1" (10.0.0.1), which are never what someone typing a target means.
    /// </summary>
    public static bool TryParseLiteral(string text, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out IPAddress? address)
    {
        address = null;
        return (text.Contains(':', StringComparison.Ordinal) || text.Count(c => c == '.') == 3) && IPAddress.TryParse(text, out address);
    }

    public static bool IsHostName(string text) =>
        text.Length is > 0 and <= 253 && text.Split('.').All(label =>
            label.Length is > 0 and <= 63 && label[0] != '-' && label[^1] != '-' && label.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_'))
        && !text.All(c => char.IsAsciiDigit(c) || c == '.');

    internal static uint ToUInt(IPAddress address)
    {
        var b = address.GetAddressBytes();
        return ((uint)b[0] << 24) | ((uint)b[1] << 16) | ((uint)b[2] << 8) | b[3];
    }

    internal static IPAddress FromUInt(uint value) =>
        new([(byte)(value >> 24), (byte)(value >> 16), (byte)(value >> 8), (byte)value]);
}
