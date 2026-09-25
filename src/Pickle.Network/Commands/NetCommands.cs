using System.Diagnostics;
using System.Globalization;
using System.Net;
using Pickle.Abstractions;

namespace Pickle.Network.Commands;

internal static class NetArgs
{
    /// <summary>"800", "800ms", "2s", "1.5s", "1m" → a timeout (plain numbers are milliseconds).</summary>
    public static TimeSpan Timeout(CommandArgs args, TimeSpan fallback)
    {
        if (args.Value("timeout") is not { Length: > 0 } text)
        {
            return fallback;
        }

        var (number, unit) = text.EndsWith("ms", StringComparison.OrdinalIgnoreCase) ? (text[..^2], 1.0)
            : text.EndsWith('s') ? (text[..^1], 1000.0)
            : text.EndsWith('m') ? (text[..^1], 60_000.0)
            : (text, 1.0);
        return double.TryParse(number, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) && value > 0
            ? TimeSpan.FromMilliseconds(Math.Min(value * unit, 600_000))
            : throw new ArgumentException($"--timeout: '{text}' is not a duration like 800ms or 2s.");
    }

    public static int Int(CommandArgs args, string name, int fallback, int min, int max) =>
        args.Value(name) is not { } text ? fallback
        : int.TryParse(text, out var value) && value >= min && value <= max ? value
        : throw new ArgumentException($"--{name} must be a number from {min} to {max}.");

    public static string Ms(TimeSpan? value) => value is { } v ? v.TotalMilliseconds.ToString(v.TotalMilliseconds < 10 ? "0.0" : "0", CultureInfo.InvariantCulture) : string.Empty;
}

internal sealed class ScanCommand : PickleCommandBase
{
    public override string Name => "scan";

    public override string Description => "Port scan (TCP connect, like nmap -sT): hosts, CIDR blocks or ranges; no install or admin needed";

    public override string Usage =>
        "pk scan <host|10.0.0.0/24|10.0.0.1-50> [-p 22,80,1-1024|top100|web|windows|db|mail|remote|all]\n" +
        "        [--timeout 800ms] [--concurrency 256] [--banner] [--all]";

    protected override async Task<int> RunAsync(CommandOutput output, IReadOnlyList<string> raw, CancellationToken cancellationToken)
    {
        var args = CommandArgs.Parse(raw, "p", "ports", "timeout", "concurrency");
        if (args.Error is not null || args.Arg(0) is null)
        {
            return UsageError(output, args.Error);
        }

        await NetFormats.EnsureLoadedAsync(output.Pickle).ConfigureAwait(false);
        var hosts = NetTargets.Expand(args.Rest(0), skipNetworkAndBroadcast: true);
        var ports = PortSpec.Parse(args.Value("p") ?? args.Value("ports") ?? "top100");
        var options = new PortScanOptions(
            NetArgs.Timeout(args, TimeSpan.FromMilliseconds(800)),
            NetArgs.Int(args, "concurrency", 256, 1, 2048),
            GrabBanner: args.Has("banner", "b"),
            IncludeClosed: args.Has("all", "a"));
        output.Muted($"Scanning {hosts.Count} host(s) × {ports.Count} port(s), timeout {NetArgs.Ms(options.Timeout)} ms… (Ctrl+C stops)");
        var watch = Stopwatch.StartNew();
        var open = 0;
        await foreach (var result in PortScanner.ScanAsync(hosts, ports, options, cancellationToken).ConfigureAwait(false))
        {
            open += result.State == PortState.Open ? 1 : 0;

            output.Object(NetFormats.Row("ScanRow", result, "Host", "Port", "State", "Service", DisplayColumn.Note("Ms", NetArgs.Ms(result.Latency)), "Banner"));
        }

        if (open == 0)
        {
            output.Muted($"No open ports found in {watch.Elapsed.TotalSeconds:0.0} s.");
        }
        return 0;
    }
}

internal sealed class SweepCommand : PickleCommandBase
{
    public override string Name => "sweep";

    public override string Description => "Find live hosts on a network (ping + TCP probes, like nmap -sn) with names and MAC addresses";

    public override string Usage => "pk sweep <10.0.0.0/24|10.0.0.1-50|hosts> [--timeout 1s] [--no-tcp] [--no-names] [--all]";

    protected override async Task<int> RunAsync(CommandOutput output, IReadOnlyList<string> raw, CancellationToken cancellationToken)
    {
        var args = CommandArgs.Parse(raw, "timeout", "concurrency");
        if (args.Error is not null || args.Arg(0) is null)
        {
            return UsageError(output, args.Error);
        }

        await NetFormats.EnsureLoadedAsync(output.Pickle).ConfigureAwait(false);
        var addresses = NetTargets.Expand(args.Rest(0), skipNetworkAndBroadcast: true);
        var options = new SweepOptions(
            NetArgs.Timeout(args, TimeSpan.FromSeconds(1)),
            NetArgs.Int(args, "concurrency", 128, 1, 1024),
            TcpProbe: !args.Has("no-tcp"),
            ResolveNames: !args.Has("no-names"),
            IncludeDown: args.Has("all", "a"));
        output.Muted($"Sweeping {addresses.Count} address(es)… (Ctrl+C stops)");
        var watch = Stopwatch.StartNew();
        var up = 0;
        await foreach (var result in HostSweep.SweepAsync(addresses, options, cancellationToken).ConfigureAwait(false))
        {
            up += result.Up ? 1 : 0;
            output.Object(NetFormats.Row("SweepRow", result, "Address", DisplayColumn.Note("Status", result.Up ? "up" : "down"), DisplayColumn.Note("Ms", NetArgs.Ms(result.Latency)), "Method", "HostName", "Mac"));
        }

        if (up == 0)
        {
            output.Muted($"No hosts answered in {watch.Elapsed.TotalSeconds:0.0} s.");
        }
        return 0;
    }
}

internal sealed class DnsCommand : PickleCommandBase
{
    public override string Name => "dns";

    public override string Description => "DNS lookup like dig: any record type (A, AAAA, MX, TXT, NS, SOA, CAA, SRV, PTR), any server";

    public override string Usage => "pk dns <name|ip> [A|AAAA|MX|TXT|NS|SOA|CNAME|CAA|SRV|PTR|all] [--server 1.1.1.1] [--timeout 3s]";

    protected override async Task<int> RunAsync(CommandOutput output, IReadOnlyList<string> raw, CancellationToken cancellationToken)
    {
        var args = CommandArgs.Parse(raw, "timeout", "server");
        if (args.Error is not null || args.Arg(0) is not { } name)
        {
            return UsageError(output, args.Error);
        }

        var server = args.Value("server") ?? args.Positional.FirstOrDefault(p => p.StartsWith('@'))?[1..];
        var typeText = args.Positional.Skip(1).FirstOrDefault(p => !p.StartsWith('@'));
        var endpoint = await ServerAsync(server, cancellationToken).ConfigureAwait(false);
        DnsRecordType[] types;
        if (NetTargets.TryParseLiteral(name, out var ip) && typeText is null)
        {
            name = DnsClient.ReverseName(ip);
            types = [DnsRecordType.PTR];
        }
        else if (typeText is null)
        {
            types = [DnsRecordType.A, DnsRecordType.AAAA];
        }
        else if (typeText.Equals("all", StringComparison.OrdinalIgnoreCase))
        {
            types = DnsClient.AllTypes;
        }
        else
        {
            types = DnsClient.TryParseType(typeText, out var type) ? [type] : throw new ArgumentException($"Unknown record type '{typeText}'.");
        }

        var timeout = NetArgs.Timeout(args, TimeSpan.FromSeconds(3));
        var failures = 0;
        foreach (var type in types)
        {
            var response = await DnsClient.QueryAsync(name, type, endpoint, timeout, cancellationToken).ConfigureAwait(false);
            var shown = response.Answers.ToList();
            if (types.Length == 1 || shown.Count > 0)
            {
                output.Muted($"{type} {name} · {response.Status} from {response.Server} in {NetArgs.Ms(response.Elapsed)} ms ({response.Transport}){(response.Authoritative ? " · authoritative" : string.Empty)}");
            }

            if (shown.Count == 0 && types.Length == 1)
            {
                shown = [.. response.Records.Where(r => r.Section == "authority")];
            }

            foreach (var record in shown)
            {
                output.Object(Display.Columns(record, "Name", "Type", DisplayColumn.Alias("TTL", "Ttl"), "Data"));
            }

            failures += response.Status is "NOERROR" or "NXDOMAIN" ? 0 : 1;
        }

        return failures == 0 ? 0 : 1;
    }

    private static async Task<IPEndPoint> ServerAsync(string? server, CancellationToken cancellationToken)
    {
        if (server is null)
        {
            return new IPEndPoint(DnsClient.SystemServers().FirstOrDefault() ?? throw new InvalidOperationException("No DNS server is configured; name one with @1.1.1.1."), 53);
        }

        if (IPEndPoint.TryParse(server, out var endpoint))
        {
            return endpoint.Port == 0 ? new IPEndPoint(endpoint.Address, 53) : endpoint;
        }

        return new IPEndPoint(await PortScanner.ResolveAsync(server, cancellationToken).ConfigureAwait(false), 53);
    }
}

internal sealed class TraceCommand : PickleCommandBase
{
    public override string Name => "trace";

    public override string Description => "Traceroute: every router between here and a host, with latency and names";

    public override string Usage => "pk trace <host> [--max-hops 30] [--timeout 2s] [--no-names]";

    protected override async Task<int> RunAsync(CommandOutput output, IReadOnlyList<string> raw, CancellationToken cancellationToken)
    {
        var args = CommandArgs.Parse(raw, "max-hops", "timeout");
        if (args.Error is not null || args.Arg(0) is not { } host)
        {
            return UsageError(output, args.Error);
        }

        await NetFormats.EnsureLoadedAsync(output.Pickle).ConfigureAwait(false);
        var hops = NetArgs.Int(args, "max-hops", 30, 1, 64);
        output.Muted($"Tracing the route to {host} (at most {hops} hops)…");
        var reached = false;
        await foreach (var hop in Traceroute.TraceAsync(host, hops, NetArgs.Timeout(args, TimeSpan.FromSeconds(2)), !args.Has("no-names"), cancellationToken).ConfigureAwait(false))
        {
            reached |= hop.Reached;
            output.Object(NetFormats.Row("TraceRow", hop, "Hop", DisplayColumn.Note("Address", hop.Address ?? "*"), DisplayColumn.Note("Ms", NetArgs.Ms(hop.Latency)), "HostName"));
        }

        if (!reached)
        {
            output.Warning($"{host} wasn't reached within {hops} hops (routers or the host may drop ICMP).");
        }

        return reached ? 0 : 1;
    }
}

internal sealed class WhoisCommand : PickleCommandBase
{
    public override string Name => "whois";

    public override string Description => "Who owns a domain or IP: registrar, dates, name servers, network and abuse contact";

    public override string Usage => "pk whois <domain|ip> [--raw]";

    protected override async Task<int> RunAsync(CommandOutput output, IReadOnlyList<string> raw, CancellationToken cancellationToken)
    {
        var args = CommandArgs.Parse(raw, "timeout");
        if (args.Error is not null || args.Arg(0) is not { } query)
        {
            return UsageError(output, args.Error);
        }

        var result = await Whois.LookupAsync(query, NetArgs.Timeout(args, TimeSpan.FromSeconds(10)), cancellationToken).ConfigureAwait(false);
        output.Muted("Asked " + string.Join(" → ", result.Servers));
        if (args.Has("raw") || result.Fields.Count == 0)
        {
            foreach (var line in result.Text.Replace("\r", string.Empty, StringComparison.Ordinal).Split('\n'))
            {
                output.Line(line);
            }
        }
        else
        {
            var width = result.Fields.Keys.Max(k => k.Length) + 2;
            foreach (var (key, value) in result.Fields)
            {
                output.Line(output.Accent((key + ":").PadRight(width)) + value);
            }

            output.Muted("(pk whois " + query + " --raw for the full record)");
        }

        return 0;
    }
}

internal sealed class CertCommand : PickleCommandBase
{
    public override string Name => "cert";

    public override string Description => "Check a server's TLS certificate: expiry, names, issuer, protocol and trust problems";

    public override string Usage => "pk cert <host[:port]> [--sni name] [--chain] [--timeout 10s]";

    protected override async Task<int> RunAsync(CommandOutput output, IReadOnlyList<string> raw, CancellationToken cancellationToken)
    {
        var args = CommandArgs.Parse(raw, "sni", "timeout");
        if (args.Error is not null || args.Arg(0) is not { } target)
        {
            return UsageError(output, args.Error);
        }

        var (host, port) = HostPort(target.Contains("://", StringComparison.Ordinal) ? new Uri(target).Authority : target, 443);
        var report = await CertInspector.InspectAsync(host, port, NetArgs.Timeout(args, TimeSpan.FromSeconds(10)), args.Value("sni"), cancellationToken).ConfigureAwait(false);
        output.Line(output.Accent("Subject:   ") + report.Subject);
        output.Line(output.Accent("Issuer:    ") + report.Issuer);
        var expiry = $"{report.NotAfter:yyyy-MM-dd} ({report.DaysLeft} days left)";
        output.Line(output.Accent("Expires:   ") + (report.DaysLeft < 0 ? output.Warn("EXPIRED " + expiry) : report.DaysLeft < 21 ? output.Warn(expiry) : expiry));
        output.Line(output.Accent("Names:     ") + string.Join(", ", report.Names.Take(12)) + (report.Names.Count > 12 ? $" (+{report.Names.Count - 12})" : string.Empty));
        output.Line(output.Accent("Protocol:  ") + $"{report.Protocol} · {report.Cipher}");
        output.Line(output.Accent("SHA-1:     ") + report.Thumbprint);
        if (report.Trusted)
        {
            output.Success("Trusted by this machine.");
        }
        else
        {
            output.Failure("Not trusted: " + report.Problems);
        }

        if (args.Has("chain"))
        {
            foreach (var (link, i) in report.Chain.Select((c, i) => (c, i)))
            {
                output.Muted($"  {i}  {link.Subject}  ← {link.Issuer}  (until {link.NotAfter:yyyy-MM-dd}, {link.KeyAlgorithm})");
            }
        }

        output.Object(report);
        return report.Trusted && report.DaysLeft >= 0 ? 0 : 1;
    }

    internal static (string Host, int Port) HostPort(string text, int defaultPort)
    {
        if (text.StartsWith('[') && text.IndexOf(']', StringComparison.Ordinal) is var close and > 0)
        {
            var rest = text[(close + 1)..];
            return (text[1..close], rest.StartsWith(':') && int.TryParse(rest[1..], out var p6) ? p6 : defaultPort);
        }

        var colon = text.LastIndexOf(':');
        return colon > 0 && text.IndexOf(':', StringComparison.Ordinal) == colon && int.TryParse(text[(colon + 1)..], out var port) && port is > 0 and < 65536
            ? (text[..colon], port)
            : (text, defaultPort);
    }
}

internal sealed class SubnetCommand : PickleCommandBase
{
    public override string Name => "subnet";

    public override string Description => "Subnet calculator: network, mask, broadcast, host range and count; split blocks; test membership";

    public override string Usage => "pk subnet <ip/prefix|ip/mask> [--split /26] [--contains 10.0.0.9]";

    protected override async Task<int> RunAsync(CommandOutput output, IReadOnlyList<string> raw, CancellationToken cancellationToken)
    {
        var args = CommandArgs.Parse(raw, "split", "contains");
        if (args.Error is not null || args.Arg(0) is not { } cidr)
        {
            return UsageError(output, args.Error);
        }

        var info = Subnet.Calculate(cidr);
        if (args.Value("split") is { } split)
        {
            await NetFormats.EnsureLoadedAsync(output.Pickle).ConfigureAwait(false);
            var prefix = int.TryParse(split.TrimStart('/'), out var p) ? p : throw new ArgumentException("--split takes a prefix like /26.");
            foreach (var block in Subnet.Split(info.Cidr, prefix))
            {
                output.Object(NetFormats.Row("SubnetRow", Subnet.Calculate(block), "Cidr", "FirstHost", "LastHost", "Hosts"));
            }

            return 0;
        }

        if (args.Value("contains") is { } ip)
        {
            var inside = Subnet.Contains(info.Cidr, ip);
            (inside ? (Action<string>)output.Success : output.Failure)($"{ip} is {(inside ? string.Empty : "not ")}in {info.Cidr}");
            return inside ? 0 : 1;
        }

        output.Object(info);
        return 0;
    }
}

internal sealed class HttpCommand : PickleCommandBase
{
    public override string Name => "http";

    public override string Description => "Time an HTTP request: status, DNS/connect/first-byte timings, redirects and headers";

    public override string Usage => "pk http <url> [-X METHOD] [-H 'Name: value'] [--no-follow] [--headers] [--timeout 30s]";

    protected override async Task<int> RunAsync(CommandOutput output, IReadOnlyList<string> raw, CancellationToken cancellationToken)
    {
        var args = CommandArgs.Parse(raw, "X", "method", "H", "header", "timeout");
        if (args.Error is not null || args.Arg(0) is not { } url)
        {
            return UsageError(output, args.Error);
        }

        await NetFormats.EnsureLoadedAsync(output.Pickle).ConfigureAwait(false);
        var headers = new List<KeyValuePair<string, string>>();
        foreach (var header in new[] { args.Value("H"), args.Value("header") }.OfType<string>())
        {
            var colon = header.IndexOf(':', StringComparison.Ordinal);
            headers.Add(colon > 0 ? new(header[..colon].Trim(), header[(colon + 1)..].Trim()) : throw new ArgumentException($"Header '{header}' needs 'Name: value'."));
        }

        var report = await HttpProbe.RunAsync(url, args.Value("X") ?? args.Value("method") ?? "GET", headers, NetArgs.Timeout(args, TimeSpan.FromSeconds(30)), !args.Has("no-follow"), cancellationToken).ConfigureAwait(false);
        foreach (var hop in report.Redirects)
        {
            output.Muted($"{hop.Status} {hop.Url} → {hop.Location}");
        }

        var status = $"{report.Status} {report.Reason}";
        output.Line(output.Accent(report.Version + " ") + (report.Status >= 400 ? output.Warn(status) : status) + output.Dim($"  {report.Url}"));
        output.Line(output.Dim($"dns {NetArgs.Ms(report.Dns)} ms · connect {NetArgs.Ms(report.Connect)} ms · first byte {NetArgs.Ms(report.FirstByte)} ms · total {NetArgs.Ms(report.Total)} ms · {report.RemoteAddress}"));
        if (args.Has("headers", "i"))
        {
            foreach (var (key, value) in report.Headers)
            {
                output.Line(output.Accent(key + ": ") + value);
            }
        }

        output.Object(NetFormats.Row("HttpRow", report, "Status", DisplayColumn.Note("Ms", NetArgs.Ms(report.Total)), "ContentType", "ContentLength", "Server", "Url"));
        return report.Status < 400 ? 0 : 1;
    }
}

internal sealed class WolCommand : PickleCommandBase
{
    public override string Name => "wol";

    public override string Description => "Wake a PC with a Wake-on-LAN magic packet";

    public override string Usage => "pk wol <mac> [--broadcast 192.168.1.255] [--port 9]";

    protected override async Task<int> RunAsync(CommandOutput output, IReadOnlyList<string> raw, CancellationToken cancellationToken)
    {
        var args = CommandArgs.Parse(raw, "broadcast", "port");
        if (args.Error is not null || args.Arg(0) is not { } mac)
        {
            return UsageError(output, args.Error);
        }

        IPAddress? broadcast = null;
        if (args.Value("broadcast") is { } text && !NetTargets.TryParseLiteral(text, out broadcast))
        {
            throw new ArgumentException($"--broadcast: '{text}' is not an address.");
        }

        var port = NetArgs.Int(args, "port", 9, 1, 65535);
        await WakeOnLan.SendAsync(mac, broadcast, port, cancellationToken).ConfigureAwait(false);
        output.Success($"Sent a magic packet for {mac} to {broadcast ?? IPAddress.Broadcast}:{port}.");
        return 0;
    }
}

internal sealed class IpCommand : PickleCommandBase
{
    public override string Name => "ip";

    public override string Description => "This machine's addresses, gateways and DNS servers (and the public address with --public)";

    public override string Usage => "pk ip [--public] [--all]";

    protected override async Task<int> RunAsync(CommandOutput output, IReadOnlyList<string> raw, CancellationToken cancellationToken)
    {
        var args = CommandArgs.Parse(raw);
        await NetFormats.EnsureLoadedAsync(output.Pickle).ConfigureAwait(false);
        foreach (var nic in LocalNetwork.Interfaces(includeDown: args.Has("all", "a")))
        {
            output.Object(NetFormats.Row(
                "IpRow",
                nic,
                "Name",
                DisplayColumn.Note("IPv4", string.Join(", ", nic.IPv4)),
                DisplayColumn.Note("Gateway", string.Join(", ", nic.Gateways)),
                DisplayColumn.Note("DNS", string.Join(", ", nic.DnsServers)),
                "Mac"));
        }

        if (args.Has("public", "p"))
        {
            output.Line(output.Accent("Public address: ") + await LocalNetwork.PublicAddressAsync(TimeSpan.FromSeconds(10), cancellationToken).ConfigureAwait(false));
        }

        return 0;
    }
}
