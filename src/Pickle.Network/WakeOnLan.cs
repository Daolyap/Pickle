using System.Globalization;
using System.Net;
using System.Net.Sockets;

namespace Pickle.Network;

/// <summary>Wake-on-LAN magic packets (6 × 0xFF, then the MAC 16 times) sent as a UDP broadcast.</summary>
public static class WakeOnLan
{
    public static byte[] ParseMac(string mac)
    {
        var hex = new string([.. mac.Where(char.IsAsciiHexDigit)]);
        if (hex.Length != 12 || mac.Any(c => !char.IsAsciiHexDigit(c) && c is not ':' and not '-' and not '.' and not ' '))
        {
            throw new ArgumentException($"'{mac}' is not a MAC address like AA-BB-CC-DD-EE-FF.");
        }

        return [.. Enumerable.Range(0, 6).Select(i => byte.Parse(hex.AsSpan(i * 2, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture))];
    }

    public static byte[] BuildPacket(byte[] mac)
    {
        var packet = new byte[102];
        Array.Fill(packet, (byte)0xFF, 0, 6);
        for (var i = 0; i < 16; i++)
        {
            mac.CopyTo(packet, 6 + (i * 6));
        }

        return packet;
    }

    public static async Task SendAsync(string mac, IPAddress? broadcast = null, int port = 9, CancellationToken cancellationToken = default)
    {
        var packet = BuildPacket(ParseMac(mac));
        using var udp = new UdpClient { EnableBroadcast = true };
        var target = new IPEndPoint(broadcast ?? IPAddress.Broadcast, port);
        for (var i = 0; i < 3; i++)
        {
            await udp.SendAsync(packet, target, cancellationToken).ConfigureAwait(false);
        }
    }
}
