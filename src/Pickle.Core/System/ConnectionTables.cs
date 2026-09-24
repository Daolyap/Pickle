using System.Buffers.Binary;
using System.Net;
using System.Net.NetworkInformation;
using Pickle.Abstractions.Services;

namespace Pickle.Core.SystemMonitoring;

/// <summary>Parses the MIB_{TCP,UDP}{,6}TABLE_OWNER_PID buffers returned by GetExtendedTcpTable/GetExtendedUdpTable.</summary>
internal static class ConnectionTables
{
    public static IReadOnlyList<NetworkConnection> ParseWindowsTable(byte[] data, bool udp, bool ipv6)
    {
        if (data.Length < 4)
        {
            return [];
        }

        var count = BinaryPrimitives.ReadInt32LittleEndian(data);
        var rowSize = (udp, ipv6) switch
        {
            (false, false) => 24,
            (false, true) => 56,
            (true, false) => 12,
            _ => 28,
        };
        var result = new List<NetworkConnection>(Math.Max(0, Math.Min(count, (data.Length - 4) / rowSize)));
        for (var i = 0; i < count; i++)
        {
            var offset = 4 + (i * rowSize);
            if (offset + rowSize > data.Length)
            {
                break;
            }

            var row = new ReadOnlySpan<byte>(data, offset, rowSize);
            result.Add((udp, ipv6) switch
            {
                (false, false) => Tcp(new IPAddress(row[4..8]), Port(row[8..]), new IPAddress(row[12..16]), Port(row[16..]), UInt(row), Int(row[20..])),
                (false, true) => Tcp(V6(row, 0), Port(row[20..]), V6(row, 24), Port(row[44..]), UInt(row[48..]), Int(row[52..])),
                (true, false) => Udp(new IPAddress(row[..4]), Port(row[4..]), Int(row[8..])),
                _ => Udp(V6(row, 0), Port(row[20..]), Int(row[24..])),
            });
        }

        return result;
    }

    public static NetworkConnection Tcp(IPAddress local, int localPort, IPAddress remote, int remotePort, string state, int? processId)
    {
        var listening = state == nameof(TcpState.Listen);
        return new NetworkConnection("TCP", local.ToString(), localPort, listening ? null : remote.ToString(), listening ? null : remotePort, state, processId);
    }

    public static NetworkConnection Udp(IPAddress local, int localPort, int? processId) =>
        new("UDP", local.ToString(), localPort, null, null, nameof(TcpState.Listen), processId);

    private static NetworkConnection Tcp(IPAddress local, int localPort, IPAddress remote, int remotePort, uint state, int processId) =>
        Tcp(local, localPort, remote, remotePort, StateName(state), processId);

    // MIB_TCP_STATE numbers match TcpState (1 = Closed … 12 = DeleteTcb).
    private static string StateName(uint state) => state is >= 1 and <= 12 ? ((TcpState)state).ToString() : nameof(TcpState.Unknown);

    // Ports are stored in network byte order in the low 16 bits of a DWORD.
    private static int Port(ReadOnlySpan<byte> dword) => (dword[0] << 8) | dword[1];

    private static uint UInt(ReadOnlySpan<byte> span) => BinaryPrimitives.ReadUInt32LittleEndian(span);

    private static int Int(ReadOnlySpan<byte> span) => BinaryPrimitives.ReadInt32LittleEndian(span);

    private static IPAddress V6(ReadOnlySpan<byte> row, int offset) => new(row.Slice(offset, 16), UInt(row[(offset + 16)..]));
}
