using System.Diagnostics;
using System.Globalization;
using System.Net;
using Pickle.Network;

namespace Pickle.Tui.Panels.NetTools;

internal enum NetFieldKind
{
    Text,
    Flag,
}

internal sealed record NetField(string Key, string Label, string Default = "", NetFieldKind Kind = NetFieldKind.Text, string? Flag = null);

/// <param name="Run">Runs the tool: (values, write a result line, show a status, token).</param>
/// <param name="Command">The equivalent <c>pk</c> command line for the values.</param>
internal sealed record NetTool(
    string Id,
    string Title,
    string Description,
    IReadOnlyList<NetField> Fields,
    Func<IReadOnlyDictionary<string, string>, Action<string>, Action<string>, CancellationToken, Task> Run,
    Func<IReadOnlyDictionary<string, string>, string> Command);

/// <summary>The Network tools panel's tools: the Pickle.Network engines with their results as text lines.</summary>
internal static class NetToolCatalog
{
    public static IReadOnlyList<NetTool> Create(string? localNetwork) =>
    [
        new(
            "scan",
            "Port scan",
            "TCP connect scan of hosts, CIDR blocks or ranges (no install or admin rights needed). Ports: 22,80,1-1024, top100, web, windows, db, mail, remote, all.",
            [new("target", "Targets", "127.0.0.1"), new("ports", "Ports", "top100"), new("timeout", "Timeout (ms)", "800"), new("banner", "Read service banners", "true", NetFieldKind.Flag, "--banner"), new("all", "Show closed/filtered", "", NetFieldKind.Flag, "--all")],
            ScanAsync,
            v => $"pk scan {v["target"]} -p {v["ports"]} --timeout {v["timeout"]}{Flags(v, "banner", "all")}"),
        new(
            "sweep",
            "Host discovery",
            "Which addresses answer (ping, then TCP probes for hosts that block ping), with names and MAC addresses on the local subnet.",
            [new("target", "Range", localNetwork ?? "192.168.1.0/24"), new("timeout", "Timeout (ms)", "1000"), new("tcp", "Also probe TCP ports", "true", NetFieldKind.Flag), new("names", "Look up names", "true", NetFieldKind.Flag)],
            SweepAsync,
            v => $"pk sweep {v["target"]} --timeout {v["timeout"]}{(IsOn(v, "tcp") ? string.Empty : " --no-tcp")}{(IsOn(v, "names") ? string.Empty : " --no-names")}"),
        new(
            "dns",
            "DNS lookup",
            "Any record type from any server (blank = this machine's). 'all' asks for every common type; an IP address gets its PTR name.",
            [new("name", "Name or IP", "example.com"), new("type", "Type", "A"), new("server", "Server (optional)")],
            DnsAsync,
            v => $"pk dns {v["name"]} {v["type"]}" + (v["server"].Length > 0 ? $" --server {v["server"]}" : string.Empty)),
        new(
            "trace",
            "Traceroute",
            "Every router on the way to a host, with latency and names.",
            [new("host", "Host", "1.1.1.1"), new("hops", "Max hops", "30")],
            TraceAsync,
            v => $"pk trace {v["host"]} --max-hops {v["hops"]}"),
        new(
            "whois",
            "Whois",
            "Registrar, dates and name servers of a domain, or the network and abuse contact of an IP address.",
            [new("query", "Domain or IP", "example.com"), new("raw", "Full record", "", NetFieldKind.Flag, "--raw")],
            WhoisAsync,
            v => $"pk whois {v["query"]}{Flags(v, "raw")}"),
        new(
            "cert",
            "TLS certificate",
            "A server's certificate: who it's for, who issued it, when it expires and whether this machine trusts it.",
            [new("host", "Host[:port]", "example.com"), new("sni", "Name to ask for (optional)")],
            CertAsync,
            v => $"pk cert {v["host"]} --chain" + (v["sni"].Length > 0 ? $" --sni {v["sni"]}" : string.Empty)),
        new(
            "http",
            "HTTP check",
            "Status, DNS/connect/first-byte timings, the redirect chain and response headers.",
            [new("url", "URL", "https://example.com"), new("method", "Method", "GET")],
            HttpAsync,
            v => $"pk http {v["url"]} -X {v["method"]} --headers"),
        new(
            "subnet",
            "Subnet calculator",
            "Network, mask, broadcast and host range of a block (10.1.2.3/22 or 10.1.2.3/255.255.252.0); optionally split it.",
            [new("cidr", "Block", localNetwork ?? "192.168.1.0/24"), new("split", "Split into /n (optional)")],
            SubnetAsync,
            v => $"pk subnet {v["cidr"]}" + (v["split"].Length > 0 ? $" --split /{v["split"].TrimStart('/')}" : string.Empty)),
        new(
            "wol",
            "Wake-on-LAN",
            "Wake a PC whose network card is set up for Wake-on-LAN (magic packet to the broadcast address).",
            [new("mac", "MAC address", "AA-BB-CC-DD-EE-FF"), new("broadcast", "Broadcast (optional)")],
            WolAsync,
            v => $"pk wol {v["mac"]}" + (v["broadcast"].Length > 0 ? $" --broadcast {v["broadcast"]}" : string.Empty)),
        new(
            "ip",
            "My addresses",
            "This machine's interfaces, addresses, gateways and DNS servers, and optionally the public address.",
            [new("public", "Also ask for the public address", "", NetFieldKind.Flag, "--public")],
            IpAsync,
            v => $"pk ip{Flags(v, "public")}"),
    ];

    /// <summary>The /24 (or smaller) block of the first interface with a gateway, for the sweep and subnet defaults.</summary>
    public static string? GuessLocalNetwork()
    {
        try
        {
            foreach (var nic in LocalNetwork.Interfaces())
            {
                if (nic.Gateways.Count > 0 && nic.IPv4.FirstOrDefault() is { } cidr)
                {
                    var slash = cidr.IndexOf('/', StringComparison.Ordinal);
                    var prefix = Math.Max(24, int.Parse(cidr[(slash + 1)..], CultureInfo.InvariantCulture));
                    return Subnet.Calculate(cidr[..slash] + "/" + prefix).Cidr;
                }
            }
        }
        catch (Exception ex) when (ex is ArgumentException or FormatException or System.Net.NetworkInformation.NetworkInformationException)
        {
        }

        return null;
    }

    private static bool IsOn(IReadOnlyDictionary<string, string> v, string key) => v.TryGetValue(key, out var value) && value == "true";

    private static string Flags(IReadOnlyDictionary<string, string> v, params string[] keys) =>
        string.Concat(keys.Where(k => IsOn(v, k)).Select(k => " --" + k));

    private static TimeSpan Ms(IReadOnlyDictionary<string, string> v, string key, int fallback) =>
        TimeSpan.FromMilliseconds(int.TryParse(v.GetValueOrDefault(key), out var ms) && ms > 0 ? Math.Min(ms, 60_000) : fallback);

    private static string Ms(TimeSpan? value) =>
        value is { } t ? t.TotalMilliseconds.ToString(t.TotalMilliseconds < 10 ? "0.0" : "0", CultureInfo.InvariantCulture) + " ms" : string.Empty;

    private static async Task ScanAsync(IReadOnlyDictionary<string, string> v, Action<string> write, Action<string> status, CancellationToken ct)
    {
        var hosts = NetTargets.Expand(v["target"]);
        var ports = PortSpec.Parse(v["ports"]);
        var watch = Stopwatch.StartNew();
        var open = 0;
        var progress = new InlineProgress(p => status($"scanned {p.Done:N0} of {p.Total:N0} ({100 * p.Done / Math.Max(1, p.Total)}%) · {open} open"));
        write($"{"HOST",-18} {"PORT",-7} {"STATE",-9} {"SERVICE",-15} {"TIME",-8} BANNER");
        var options = new PortScanOptions(Ms(v, "timeout", 800), GrabBanner: IsOn(v, "banner"), IncludeClosed: IsOn(v, "all"), Progress: progress);
        await foreach (var r in PortScanner.ScanAsync(hosts, ports, options, ct).ConfigureAwait(false))
        {
            open += r.State == PortState.Open ? 1 : 0;
            write($"{r.Host,-18} {r.Port + "/tcp",-7} {r.State.ToString().ToLowerInvariant(),-9} {r.Service,-15} {Ms(r.Latency),-8} {r.Banner}".TrimEnd());
        }

        write(string.Empty);
        write($"{open} open port(s) · {hosts.Count} host(s) × {ports.Count} port(s) in {watch.Elapsed.TotalSeconds:0.0} s");
    }

    private static async Task SweepAsync(IReadOnlyDictionary<string, string> v, Action<string> write, Action<string> status, CancellationToken ct)
    {
        var addresses = NetTargets.Expand(v["target"]);
        var watch = Stopwatch.StartNew();
        var up = 0;
        var progress = new InlineProgress(p => status($"probed {p.Done:N0} of {p.Total:N0} · {up} up"));
        write($"{"ADDRESS",-16} {"TIME",-9} {"VIA",-9} {"MAC",-18} NAME");
        await foreach (var r in HostSweep.SweepAsync(addresses, new SweepOptions(Ms(v, "timeout", 1000), TcpProbe: IsOn(v, "tcp"), ResolveNames: IsOn(v, "names"), Progress: progress), ct).ConfigureAwait(false))
        {
            up++;
            write($"{r.Address,-16} {Ms(r.Latency),-9} {r.Method,-9} {r.Mac,-18} {r.HostName}".TrimEnd());
        }

        write(string.Empty);
        write($"{up} of {addresses.Count} host(s) up in {watch.Elapsed.TotalSeconds:0.0} s");
    }

    private static async Task DnsAsync(IReadOnlyDictionary<string, string> v, Action<string> write, Action<string> status, CancellationToken ct)
    {
        var name = v["name"].Trim();
        var typeText = v["type"].Trim();
        var server = v["server"].Trim();
        IPEndPoint endpoint;
        if (server.Length == 0)
        {
            endpoint = new IPEndPoint(DnsClient.SystemServers().FirstOrDefault() ?? throw new InvalidOperationException("No DNS server is configured; enter one."), 53);
        }
        else
        {
            endpoint = IPEndPoint.TryParse(server, out var parsed) && parsed.Port != 0 ? parsed
                : new IPEndPoint(await PortScanner.ResolveAsync(server, ct).ConfigureAwait(false), 53);
        }

        DnsRecordType[] types;
        if (NetTargets.TryParseLiteral(name, out var ip) && (typeText.Length == 0 || typeText.Equals("PTR", StringComparison.OrdinalIgnoreCase) || typeText.Equals("A", StringComparison.OrdinalIgnoreCase)))
        {
            name = DnsClient.ReverseName(ip);
            types = [DnsRecordType.PTR];
        }
        else if (typeText.Equals("all", StringComparison.OrdinalIgnoreCase))
        {
            types = DnsClient.AllTypes;
        }
        else
        {
            types = [DnsClient.TryParseType(typeText.Length == 0 ? "A" : typeText, out var t) ? t : throw new ArgumentException($"Unknown record type '{typeText}'.")];
        }

        foreach (var type in types)
        {
            status($"asking {endpoint} for {type}…");
            var response = await DnsClient.QueryAsync(name, type, endpoint, TimeSpan.FromSeconds(4), ct).ConfigureAwait(false);
            var records = response.Answers.ToList();
            if (records.Count == 0 && types.Length > 1)
            {
                continue;
            }

            write($";; {type} {name}: {response.Status} from {response.Server} in {Ms(response.Elapsed)} ({response.Transport}){(response.Authoritative ? ", authoritative" : string.Empty)}");
            foreach (var r in records.Count > 0 ? records : [.. response.Records.Where(r => r.Section == "authority")])
            {
                write($"{r.Name,-32} {r.Ttl,7} {r.Type,-6} {r.Data}");
            }

            write(string.Empty);
        }
    }

    private static async Task TraceAsync(IReadOnlyDictionary<string, string> v, Action<string> write, Action<string> status, CancellationToken ct)
    {
        var hops = int.TryParse(v["hops"], out var h) ? Math.Clamp(h, 1, 64) : 30;
        var reached = false;
        await foreach (var hop in Traceroute.TraceAsync(v["host"].Trim(), hops, TimeSpan.FromSeconds(2), cancellationToken: ct).ConfigureAwait(false))
        {
            status($"hop {hop.Hop}…");
            reached |= hop.Reached;
            write($"{hop.Hop,3}  {hop.Address ?? "*",-40} {Ms(hop.Latency),-9} {hop.HostName}".TrimEnd());
        }

        write(string.Empty);
        write(reached ? "Reached the host." : $"Not reached within {hops} hops (routers or the host may drop ICMP).");
    }

    private static async Task WhoisAsync(IReadOnlyDictionary<string, string> v, Action<string> write, Action<string> status, CancellationToken ct)
    {
        status("asking whois servers…");
        var result = await Whois.LookupAsync(v["query"], TimeSpan.FromSeconds(10), ct).ConfigureAwait(false);
        write(";; asked " + string.Join(" → ", result.Servers));
        foreach (var (key, value) in result.Fields)
        {
            write($"{key + ":",-14} {value}");
        }

        if (IsOn(v, "raw") || result.Fields.Count == 0)
        {
            write(string.Empty);
            foreach (var line in result.Text.Replace("\r", string.Empty, StringComparison.Ordinal).Split('\n'))
            {
                write(line);
            }
        }
    }

    private static async Task CertAsync(IReadOnlyDictionary<string, string> v, Action<string> write, Action<string> status, CancellationToken ct)
    {
        var text = v["host"].Trim();
        var colon = text.LastIndexOf(':');
        var (host, port) = colon > 0 && text.IndexOf(':', StringComparison.Ordinal) == colon && int.TryParse(text[(colon + 1)..], out var p) ? (text[..colon], p) : (text, 443);
        status($"connecting to {host}:{port}…");
        var r = await CertInspector.InspectAsync(host, port, TimeSpan.FromSeconds(10), v["sni"].Length > 0 ? v["sni"] : null, ct).ConfigureAwait(false);
        write($"Subject:   {r.Subject}");
        write($"Issuer:    {r.Issuer}");
        write($"Valid:     {r.NotBefore:yyyy-MM-dd} → {r.NotAfter:yyyy-MM-dd} ({(r.DaysLeft < 0 ? "EXPIRED" : r.DaysLeft + " days left")})");
        write($"Names:     {string.Join(", ", r.Names)}");
        write($"Protocol:  {r.Protocol} · {r.Cipher}");
        write($"SHA-1:     {r.Thumbprint}");
        write(r.Trusted ? "Trust:     ✓ trusted by this machine" : $"Trust:     ✖ {r.Problems}");
        write(string.Empty);
        write("Chain:");
        foreach (var (link, i) in r.Chain.Select((c, i) => (c, i)))
        {
            write($"  {i}  {link.Subject}  ← {link.Issuer}  (until {link.NotAfter:yyyy-MM-dd}, {link.KeyAlgorithm})");
        }
    }

    private static async Task HttpAsync(IReadOnlyDictionary<string, string> v, Action<string> write, Action<string> status, CancellationToken ct)
    {
        status("requesting…");
        var r = await HttpProbe.RunAsync(v["url"].Trim(), v["method"].Length > 0 ? v["method"] : "GET", cancellationToken: ct).ConfigureAwait(false);
        foreach (var hop in r.Redirects)
        {
            write($"{hop.Status} {hop.Url} → {hop.Location}");
        }

        write($"{r.Version} {r.Status} {r.Reason}  {r.Url}");
        write($"dns {Ms(r.Dns)} · connect {Ms(r.Connect)} · first byte {Ms(r.FirstByte)} · total {Ms(r.Total)} · {r.RemoteAddress}");
        write(string.Empty);
        foreach (var (key, value) in r.Headers)
        {
            write($"{key}: {value}");
        }
    }

    private static Task SubnetAsync(IReadOnlyDictionary<string, string> v, Action<string> write, Action<string> status, CancellationToken ct)
    {
        var info = Subnet.Calculate(v["cidr"]);
        write($"Network:    {info.Cidr}  ({info.Kind})");
        write($"Mask:       {info.Mask}  (wildcard {info.Wildcard})");
        if (info.Broadcast is not null)
        {
            write($"Broadcast:  {info.Broadcast}");
        }

        write($"Hosts:      {info.FirstHost} – {info.LastHost}  ({info.Hosts:N0})");
        if (v["split"].TrimStart('/') is { Length: > 0 } split)
        {
            write(string.Empty);
            foreach (var block in Subnet.Split(info.Cidr, int.TryParse(split, out var prefix) ? prefix : throw new ArgumentException("Split: a prefix like 26.")))
            {
                var b = Subnet.Calculate(block);
                write($"{b.Cidr,-20} {b.FirstHost,-16} – {b.LastHost,-16} {b.Hosts,8:N0}");
            }
        }

        return Task.CompletedTask;
    }

    private static async Task WolAsync(IReadOnlyDictionary<string, string> v, Action<string> write, Action<string> status, CancellationToken ct)
    {
        IPAddress? broadcast = null;
        if (v["broadcast"].Length > 0 && !NetTargets.TryParseLiteral(v["broadcast"], out broadcast))
        {
            throw new ArgumentException($"'{v["broadcast"]}' is not an address.");
        }

        await WakeOnLan.SendAsync(v["mac"], broadcast, cancellationToken: ct).ConfigureAwait(false);
        write($"Sent a magic packet for {v["mac"]} to {broadcast ?? IPAddress.Broadcast}:9.");
    }

    private static async Task IpAsync(IReadOnlyDictionary<string, string> v, Action<string> write, Action<string> status, CancellationToken ct)
    {
        foreach (var nic in LocalNetwork.Interfaces())
        {
            write($"{nic.Name}  ({nic.Type}{(nic.SpeedMbps > 0 ? $", {nic.SpeedMbps} Mb/s" : string.Empty)})  {nic.Mac}");
            foreach (var address in nic.IPv4.Concat(nic.IPv6))
            {
                write($"    {address}");
            }

            if (nic.Gateways.Count > 0)
            {
                write($"    gateway {string.Join(", ", nic.Gateways)}");
            }

            if (nic.DnsServers.Count > 0)
            {
                write($"    dns     {string.Join(", ", nic.DnsServers)}");
            }
        }

        if (IsOn(v, "public"))
        {
            status("asking for the public address…");
            write(string.Empty);
            write("Public address: " + await LocalNetwork.PublicAddressAsync(TimeSpan.FromSeconds(10), ct).ConfigureAwait(false));
        }
    }

    /// <summary>Reports on the calling thread: <see cref="Progress{T}"/> posts to the thread pool, so a late report could land after the run finished.</summary>
    private sealed class InlineProgress(Action<(int Done, int Total)> report) : IProgress<(int Done, int Total)>
    {
        public void Report((int Done, int Total) value) => report(value);
    }
}
