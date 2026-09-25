using System.Buffers.Binary;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;

namespace Pickle.Network;

public enum DnsRecordType : ushort
{
    A = 1,
    NS = 2,
    CNAME = 5,
    SOA = 6,
    PTR = 12,
    MX = 15,
    TXT = 16,
    AAAA = 28,
    SRV = 33,
    OPT = 41,
    DS = 43,
    DNSKEY = 48,
    CAA = 257,
    ANY = 255,
}

public sealed record DnsRecord(string Section, string Name, DnsRecordType Type, uint Ttl, string Data);

public sealed record DnsResponse(
    string Server,
    string Question,
    DnsRecordType Type,
    string Status,
    bool Authoritative,
    bool Truncated,
    IReadOnlyList<DnsRecord> Records,
    TimeSpan Elapsed,
    string Transport)
{
    public IEnumerable<DnsRecord> Answers => Records.Where(r => r.Section == "answer");
}

/// <summary>A small stub resolver (dig-style): any record type, any server, UDP with EDNS and TCP when truncated.</summary>
public static class DnsClient
{
    /// <summary>What <c>pk dns name all</c> asks for.</summary>
    public static readonly DnsRecordType[] AllTypes =
        [DnsRecordType.A, DnsRecordType.AAAA, DnsRecordType.CNAME, DnsRecordType.MX, DnsRecordType.NS, DnsRecordType.TXT, DnsRecordType.SOA, DnsRecordType.CAA, DnsRecordType.SRV];

    /// <summary>The DNS servers this machine uses (active interfaces, then /etc/resolv.conf).</summary>
    public static IReadOnlyList<IPAddress> SystemServers()
    {
        var servers = new List<IPAddress>();
        try
        {
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.OperationalStatus != OperationalStatus.Up || nic.NetworkInterfaceType == NetworkInterfaceType.Loopback)
                {
                    continue;
                }

                // fec0:0:0:ffff::1-3 are Windows' placeholder site-local servers that never answer.
                servers.AddRange(nic.GetIPProperties().DnsAddresses.Where(a => !a.IsIPv6SiteLocal));
            }
        }
        catch (NetworkInformationException)
        {
        }

        if (servers.Count == 0 && File.Exists("/etc/resolv.conf"))
        {
            foreach (var line in File.ReadLines("/etc/resolv.conf"))
            {
                var parts = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length >= 2 && parts[0] == "nameserver" && IPAddress.TryParse(parts[1], out var address))
                {
                    servers.Add(address);
                }
            }
        }

        return [.. servers.Distinct()];
    }

    public static bool TryParseType(string text, out DnsRecordType type) =>
        Enum.TryParse(text, ignoreCase: true, out type) && Enum.IsDefined(type) && type != DnsRecordType.OPT;

    /// <summary>The PTR name for an address (1.2.3.4 → 4.3.2.1.in-addr.arpa).</summary>
    public static string ReverseName(IPAddress address)
    {
        var bytes = address.GetAddressBytes();
        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            return string.Join('.', bytes.Reverse()) + ".in-addr.arpa";
        }

        var nibbles = bytes.Reverse().SelectMany(b => new[] { b & 0xF, b >> 4 }).Select(n => n.ToString("x", CultureInfo.InvariantCulture));
        return string.Join('.', nibbles) + ".ip6.arpa";
    }

    public static async Task<DnsResponse> QueryAsync(string name, DnsRecordType type, IPEndPoint server, TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        var id = (ushort)Random.Shared.Next(ushort.MaxValue);
        var query = DnsWire.BuildQuery(id, name, type);
        var watch = Stopwatch.StartNew();
        var reply = await UdpAsync(query, server, timeout, cancellationToken).ConfigureAwait(false);
        var parsed = DnsWire.Parse(reply);
        var transport = "udp";
        if (parsed.Id != id)
        {
            throw new InvalidDataException("The DNS reply doesn't match the question.");
        }

        if (parsed.Truncated)
        {
            reply = await TcpAsync(query, server, timeout, cancellationToken).ConfigureAwait(false);
            parsed = DnsWire.Parse(reply);
            transport = "tcp";
        }

        return new DnsResponse(server.ToString(), name, type, parsed.Status, parsed.Authoritative, parsed.Truncated, parsed.Records, watch.Elapsed, transport);
    }

    private static async Task<byte[]> UdpAsync(byte[] query, IPEndPoint server, TimeSpan timeout, CancellationToken cancellationToken)
    {
        using var udp = new UdpClient(server.AddressFamily);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(timeout);
        try
        {
            await udp.SendAsync(query, server, cts.Token).ConfigureAwait(false);
            while (true)
            {
                var result = await udp.ReceiveAsync(cts.Token).ConfigureAwait(false);
                if (result.RemoteEndPoint.Address.Equals(server.Address) && result.Buffer.Length >= 12)
                {
                    return result.Buffer;
                }
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException($"{server} didn't answer within {timeout.TotalSeconds:0.#} s.");
        }
    }

    private static async Task<byte[]> TcpAsync(byte[] query, IPEndPoint server, TimeSpan timeout, CancellationToken cancellationToken)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(timeout);
        try
        {
            using var tcp = new TcpClient(server.AddressFamily);
            await tcp.ConnectAsync(server, cts.Token).ConfigureAwait(false);
            var stream = tcp.GetStream();
            var framed = new byte[query.Length + 2];
            BinaryPrimitives.WriteUInt16BigEndian(framed, (ushort)query.Length);
            query.CopyTo(framed, 2);
            await stream.WriteAsync(framed, cts.Token).ConfigureAwait(false);
            var length = new byte[2];
            await stream.ReadExactlyAsync(length, cts.Token).ConfigureAwait(false);
            var reply = new byte[BinaryPrimitives.ReadUInt16BigEndian(length)];
            await stream.ReadExactlyAsync(reply, cts.Token).ConfigureAwait(false);
            return reply;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException($"{server} didn't answer over TCP within {timeout.TotalSeconds:0.#} s.");
        }
    }
}

/// <summary>DNS message encoding and decoding (RFC 1035, EDNS0 OPT, name compression).</summary>
internal static class DnsWire
{
    private static readonly string[] Rcodes = ["NOERROR", "FORMERR", "SERVFAIL", "NXDOMAIN", "NOTIMP", "REFUSED", "YXDOMAIN", "YXRRSET", "NXRRSET", "NOTAUTH", "NOTZONE"];

    internal sealed record Parsed(ushort Id, string Status, bool Authoritative, bool Truncated, IReadOnlyList<DnsRecord> Records);

    public static byte[] BuildQuery(ushort id, string name, DnsRecordType type)
    {
        var bytes = new List<byte>(64);
        void U16(int value)
        {
            bytes.Add((byte)(value >> 8));
            bytes.Add((byte)value);
        }

        U16(id);
        U16(0x0100); // recursion desired
        U16(1);
        U16(0);
        U16(0);
        U16(1); // the EDNS OPT record
        WriteName(bytes, name);
        U16((int)type);
        U16(1); // IN
        bytes.Add(0); // OPT: root name
        U16((int)DnsRecordType.OPT);
        U16(4096); // UDP payload size
        U16(0);
        U16(0);
        U16(0);
        return [.. bytes];
    }

    private static void WriteName(List<byte> bytes, string name)
    {
        foreach (var label in name.TrimEnd('.').Split('.', StringSplitOptions.RemoveEmptyEntries))
        {
            var encoded = Encoding.ASCII.GetBytes(label);
            if (encoded.Length > 63)
            {
                throw new ArgumentException($"'{label}' is longer than 63 characters.");
            }

            bytes.Add((byte)encoded.Length);
            bytes.AddRange(encoded);
        }

        bytes.Add(0);
    }

    public static Parsed Parse(byte[] message)
    {
        if (message.Length < 12)
        {
            throw new InvalidDataException("The DNS reply is too short.");
        }

        var id = BinaryPrimitives.ReadUInt16BigEndian(message.AsSpan(0));
        var flags = BinaryPrimitives.ReadUInt16BigEndian(message.AsSpan(2));
        var counts = Enumerable.Range(0, 4).Select(i => BinaryPrimitives.ReadUInt16BigEndian(message.AsSpan(4 + (i * 2)))).ToArray();
        var offset = 12;
        for (var q = 0; q < counts[0]; q++)
        {
            ReadName(message, ref offset);
            offset += 4;
        }

        var records = new List<DnsRecord>();
        string[] sections = ["answer", "authority", "additional"];
        for (var s = 0; s < 3; s++)
        {
            for (var i = 0; i < counts[s + 1]; i++)
            {
                var name = ReadName(message, ref offset);
                Need(message, offset, 10);
                var type = (DnsRecordType)BinaryPrimitives.ReadUInt16BigEndian(message.AsSpan(offset));
                var ttl = BinaryPrimitives.ReadUInt32BigEndian(message.AsSpan(offset + 4));
                var length = BinaryPrimitives.ReadUInt16BigEndian(message.AsSpan(offset + 8));
                offset += 10;
                Need(message, offset, length);
                if (type != DnsRecordType.OPT)
                {
                    records.Add(new DnsRecord(sections[s], name, type, ttl, FormatData(message, offset, length, type)));
                }

                offset += length;
            }
        }

        var rcode = flags & 0xF;
        return new Parsed(id, rcode < Rcodes.Length ? Rcodes[rcode] : "RCODE" + rcode, (flags & 0x0400) != 0, (flags & 0x0200) != 0, records);
    }

    private static string FormatData(byte[] m, int offset, int length, DnsRecordType type)
    {
        var at = offset;
        switch (type)
        {
            case DnsRecordType.A when length == 4:
            case DnsRecordType.AAAA when length == 16:
                return new IPAddress(m.AsSpan(offset, length)).ToString();
            case DnsRecordType.NS or DnsRecordType.CNAME or DnsRecordType.PTR:
                return ReadName(m, ref at);
            case DnsRecordType.MX when length >= 3:
                var preference = BinaryPrimitives.ReadUInt16BigEndian(m.AsSpan(offset));
                at += 2;
                return $"{preference} {ReadName(m, ref at)}";
            case DnsRecordType.TXT:
                var parts = new List<string>();
                while (at < offset + length)
                {
                    var size = m[at++];
                    Need(m, at, size);
                    parts.Add("\"" + Encoding.UTF8.GetString(m, at, size) + "\"");
                    at += size;
                }

                return string.Join(' ', parts);
            case DnsRecordType.SOA:
                var primary = ReadName(m, ref at);
                var mailbox = ReadName(m, ref at);
                Need(m, at, 20);
                var numbers = Enumerable.Range(0, 5).Select(i => BinaryPrimitives.ReadUInt32BigEndian(m.AsSpan(at + (i * 4)))).ToArray();
                return $"{primary} {mailbox} serial={numbers[0]} refresh={numbers[1]} retry={numbers[2]} expire={numbers[3]} minimum={numbers[4]}";
            case DnsRecordType.SRV when length >= 7:
                var priority = BinaryPrimitives.ReadUInt16BigEndian(m.AsSpan(offset));
                var weight = BinaryPrimitives.ReadUInt16BigEndian(m.AsSpan(offset + 2));
                var port = BinaryPrimitives.ReadUInt16BigEndian(m.AsSpan(offset + 4));
                at += 6;
                return $"{priority} {weight} {port} {ReadName(m, ref at)}";
            case DnsRecordType.CAA when length >= 2:
                var tagLength = m[offset + 1];
                Need(m, offset + 2, tagLength);
                var tag = Encoding.ASCII.GetString(m, offset + 2, tagLength);
                var value = Encoding.UTF8.GetString(m, offset + 2 + tagLength, length - 2 - tagLength);
                return $"{m[offset]} {tag} \"{value}\"";
            default:
                return Convert.ToHexString(m, offset, length);
        }
    }

    internal static string ReadName(byte[] m, ref int offset)
    {
        var labels = new List<string>();
        var at = offset;
        var jumped = false;
        for (var hops = 0; hops < 128; hops++)
        {
            Need(m, at, 1);
            var length = m[at];
            if (length == 0)
            {
                if (!jumped)
                {
                    offset = at + 1;
                }

                return labels.Count == 0 ? "." : string.Join('.', labels) + ".";
            }

            if ((length & 0xC0) == 0xC0)
            {
                Need(m, at, 2);
                if (!jumped)
                {
                    offset = at + 2;
                }

                at = ((length & 0x3F) << 8) | m[at + 1];
                jumped = true;
                continue;
            }

            Need(m, at + 1, length);
            labels.Add(Encoding.ASCII.GetString(m, at + 1, length));
            at += length + 1;
        }

        throw new InvalidDataException("The DNS reply has a name that loops.");
    }

    private static void Need(byte[] m, int offset, int count)
    {
        if (offset < 0 || offset + count > m.Length)
        {
            throw new InvalidDataException("The DNS reply is cut short.");
        }
    }
}
