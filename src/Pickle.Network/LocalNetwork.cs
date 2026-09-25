using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace Pickle.Network;

public sealed record InterfaceInfo(
    string Name,
    string Description,
    string Status,
    string Type,
    string? Mac,
    IReadOnlyList<string> IPv4,
    IReadOnlyList<string> IPv6,
    IReadOnlyList<string> Gateways,
    IReadOnlyList<string> DnsServers,
    long SpeedMbps);

/// <summary>This machine's interfaces, addresses, gateways and DNS servers, and its public address.</summary>
public static class LocalNetwork
{
    public const string PublicIpUrl = "https://api.ipify.org";

    public static IReadOnlyList<InterfaceInfo> Interfaces(bool includeDown = false)
    {
        var result = new List<InterfaceInfo>();
        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (!includeDown && nic.OperationalStatus != OperationalStatus.Up)
            {
                continue;
            }

            IPInterfaceProperties properties;
            try
            {
                properties = nic.GetIPProperties();
            }
            catch (NetworkInformationException)
            {
                continue;
            }

            var unicast = properties.UnicastAddresses;
            var mac = nic.GetPhysicalAddress().GetAddressBytes();
            result.Add(new InterfaceInfo(
                nic.Name,
                nic.Description,
                nic.OperationalStatus.ToString(),
                nic.NetworkInterfaceType.ToString(),
                mac.Length == 6 ? string.Join('-', mac.Select(b => b.ToString("X2", System.Globalization.CultureInfo.InvariantCulture))) : null,
                [.. unicast.Where(u => u.Address.AddressFamily == AddressFamily.InterNetwork).Select(u => $"{u.Address}/{u.PrefixLength}")],
                [.. unicast.Where(u => u.Address.AddressFamily == AddressFamily.InterNetworkV6).Select(u => $"{u.Address}/{u.PrefixLength}")],
                [.. SafeGateways(properties)],
                [.. properties.DnsAddresses.Where(a => !a.IsIPv6SiteLocal).Select(a => a.ToString())],
                nic.Speed > 0 ? nic.Speed / 1_000_000 : 0));
        }

        return [.. result.OrderByDescending(i => i.Gateways.Count > 0).ThenBy(i => i.Type == nameof(NetworkInterfaceType.Loopback)).ThenBy(i => i.Name, StringComparer.OrdinalIgnoreCase)];
    }

    /// <summary>The address the internet sees (asks <see cref="PublicIpUrl"/>).</summary>
    public static async Task<string> PublicAddressAsync(TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        using var client = new HttpClient { Timeout = timeout };
        var text = (await client.GetStringAsync(PublicIpUrl, cancellationToken).ConfigureAwait(false)).Trim();
        return System.Net.IPAddress.TryParse(text, out var address) ? address.ToString() : throw new InvalidDataException("Unexpected answer: " + text);
    }

    private static IEnumerable<string> SafeGateways(IPInterfaceProperties properties)
    {
        try
        {
            return properties.GatewayAddresses.Select(g => g.Address.ToString()).ToList();
        }
        catch (PlatformNotSupportedException)
        {
            return [];
        }
    }
}
