using System.Net;
using System.Net.Sockets;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Pickle.Abstractions;
using Pickle.Abstractions.Services;

namespace Pickle.Modules.Nmap;

public sealed record NmapProfile(string Id, string Name, IReadOnlyList<string> Arguments, string Description, bool BetterAsRoot = false);

public sealed record NmapScript(string Id, string Output);

public sealed record NmapPort(int Port, string Protocol, string State, string Service, string Version, IReadOnlyList<NmapScript> Scripts);

public sealed record NmapHost(string Address, string? Hostname, string Status, string? Mac, string? Vendor, string? Os, IReadOnlyList<NmapPort> Ports)
{
    public IEnumerable<NmapPort> OpenPorts => Ports.Where(p => p.State.StartsWith("open", StringComparison.Ordinal));
}

public sealed record NmapScan(IReadOnlyList<NmapHost> Hosts, string Summary);

/// <summary>nmap through an argument list. Targets and port lists are validated so nothing a user types can become an nmap option.</summary>
public sealed partial class NmapService(IProgramRunner runner)
{
    public static IReadOnlyList<NmapProfile> Profiles { get; } =
    [
        new("quick", "Quick scan", ["-T4", "-F"], "The 100 most common ports, fast."),
        new("ping", "Ping sweep", ["-sn"], "Which hosts are up; no port scan."),
        new("services", "Service versions", ["-sV", "-T4", "--top-ports", "1000"], "The 1000 most common ports with service and version detection."),
        new("intense", "Intense", ["-T4", "-A"], "Versions, OS guess, default scripts and traceroute. Needs root for the OS guess.", BetterAsRoot: true),
        new("allports", "All TCP ports", ["-p-", "-T4"], "Every TCP port. Slow."),
        new("vuln", "Vulnerability scripts", ["-sV", "--script", "vuln"], "The 'vuln' NSE category. Intrusive; only against systems you own."),
        new("udp", "Top UDP ports", ["-sU", "--top-ports", "100"], "The 100 most common UDP ports. Needs root.", BetterAsRoot: true),
    ];

    public bool IsAvailable => runner.Find("nmap") is not null;

    public static NmapProfile Profile(string id) =>
        Profiles.FirstOrDefault(p => string.Equals(p.Id, id, StringComparison.OrdinalIgnoreCase))
        ?? throw new ArgumentException($"'{id}' is not a scan profile ({string.Join(", ", Profiles.Select(p => p.Id))}).");

    /// <summary>The full argument list for a scan (also what the panel hands to the shell to run with sudo).</summary>
    public static IReadOnlyList<string> Arguments(string target, NmapProfile profile, string? ports = null, bool xml = true)
    {
        var args = new List<string>(profile.Arguments);
        if (!string.IsNullOrWhiteSpace(ports))
        {
            if (!Ports().IsMatch(ports))
            {
                throw new ArgumentException($"'{ports}' is not a port list (like 22,80,443 or 1-1024 or T:80,U:53).");
            }

            args.RemoveAll(a => a is "-F" or "-p-" or "--top-ports" or "1000" or "100");
            args.AddRange(["-p", ports]);
        }

        if (xml)
        {
            args.AddRange(["-oX", "-"]);
        }

        args.Add("--");
        args.AddRange(Targets(target));
        return args;
    }

    public async Task<NmapScan> ScanAsync(string target, NmapProfile profile, string? ports, CancellationToken cancellationToken)
    {
        var result = await runner.RunAsync("nmap", Arguments(target, profile, ports), new ProgramRunOptions { Timeout = TimeSpan.FromMinutes(20) }, cancellationToken).ConfigureAwait(false);
        if (!result.WasFound)
        {
            throw new InvalidOperationException("nmap was not found. Windows: pk tool install nmap; Linux/macOS: install it with your package manager.");
        }

        if (!result.Success && !result.StdOut.Contains("<nmaprun", StringComparison.Ordinal))
        {
            throw new InvalidOperationException(result.Message);
        }

        return Parse(result.StdOut);
    }

    /// <summary>Splits a target list (spaces or commas) and rejects anything that is not a host name, address, range or CIDR block.</summary>
    public static IReadOnlyList<string> Targets(string target)
    {
        var parts = target.Split([' ', ',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length is 0 or > 32)
        {
            throw new ArgumentException("Give one to 32 targets (host names, addresses, ranges like 10.0.0.1-50, or CIDR blocks like 10.0.0.0/24).");
        }

        foreach (var part in parts)
        {
            if (!Target().IsMatch(part))
            {
                throw new ArgumentException($"'{part}' is not a scan target (letters, digits and . : / - _ only, not starting with a dash).");
            }
        }

        return parts;
    }

    /// <summary>True when every target is on a private, loopback or link-local network (no authorization question needed).</summary>
    public static bool IsLocalOnly(IEnumerable<string> targets) => targets.All(t =>
    {
        var host = t.Split('/')[0].Split('-')[0];
        if (host.Equals("localhost", StringComparison.OrdinalIgnoreCase) || host.EndsWith(".local", StringComparison.OrdinalIgnoreCase) || host.EndsWith(".lan", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return IPAddress.TryParse(host, out var ip) && IsPrivate(ip);
    });

    public static bool IsPrivate(IPAddress ip)
    {
        if (IPAddress.IsLoopback(ip))
        {
            return true;
        }

        if (ip.AddressFamily == AddressFamily.InterNetwork)
        {
            var b = ip.GetAddressBytes();
            return b[0] == 10 || (b[0] == 172 && b[1] is >= 16 and <= 31) || (b[0] == 192 && b[1] == 168) || (b[0] == 169 && b[1] == 254) || (b[0] == 100 && b[1] is >= 64 and <= 127);
        }

        return ip.AddressFamily == AddressFamily.InterNetworkV6 && (ip.IsIPv6LinkLocal || ip.IsIPv6SiteLocal || (ip.GetAddressBytes()[0] & 0xFE) == 0xFC);
    }

    public static NmapScan Parse(string xml)
    {
        XDocument document;
        try
        {
            document = XDocument.Parse(xml);
        }
        catch (System.Xml.XmlException)
        {
            return new NmapScan([], "No readable nmap output.");
        }

        var hosts = new List<NmapHost>();
        foreach (var host in document.Descendants("host"))
        {
            var addresses = host.Elements("address").ToList();
            var ip = addresses.FirstOrDefault(a => a.Attribute("addrtype")?.Value is "ipv4" or "ipv6")?.Attribute("addr")?.Value ?? "?";
            var mac = addresses.FirstOrDefault(a => a.Attribute("addrtype")?.Value == "mac");
            var ports = host.Element("ports")?.Elements("port").Select(p =>
            {
                var service = p.Element("service");
                var version = string.Join(' ', new[] { service?.Attribute("product")?.Value, service?.Attribute("version")?.Value, service?.Attribute("extrainfo")?.Value }.Where(s => !string.IsNullOrEmpty(s)));
                return new NmapPort(
                    int.TryParse(p.Attribute("portid")?.Value, out var number) ? number : 0,
                    p.Attribute("protocol")?.Value ?? "tcp",
                    p.Element("state")?.Attribute("state")?.Value ?? "unknown",
                    service?.Attribute("name")?.Value ?? string.Empty,
                    version,
                    [.. p.Elements("script").Select(s => new NmapScript(s.Attribute("id")?.Value ?? "script", s.Attribute("output")?.Value ?? string.Empty))]);
            }).Where(p => p.Port > 0).ToList() ?? [];
            hosts.Add(new NmapHost(
                ip,
                host.Element("hostnames")?.Elements("hostname").Select(h => h.Attribute("name")?.Value).FirstOrDefault(n => !string.IsNullOrEmpty(n)),
                host.Element("status")?.Attribute("state")?.Value ?? "unknown",
                mac?.Attribute("addr")?.Value,
                mac?.Attribute("vendor")?.Value,
                host.Element("os")?.Elements("osmatch").Select(o => o.Attribute("name")?.Value).FirstOrDefault(n => !string.IsNullOrEmpty(n)),
                ports));
        }

        var summary = document.Descendants("runstats").Descendants("finished").FirstOrDefault()?.Attribute("summary")?.Value ?? $"{hosts.Count} host(s)";
        return new NmapScan([.. hosts.OrderBy(h => IpSort(h.Address), StringComparer.Ordinal)], summary);
    }

    private static string IpSort(string address) =>
        IPAddress.TryParse(address, out var ip) ? string.Concat(ip.GetAddressBytes().Select(b => b.ToString("x2", System.Globalization.CultureInfo.InvariantCulture))) : address;

    [GeneratedRegex(@"^[A-Za-z0-9._:][A-Za-z0-9._:/\-]{0,253}$")]
    private static partial Regex Target();

    [GeneratedRegex(@"^[TUtu]?:?[0-9,\-]+(,[TUtu]:[0-9,\-]+)*$")]
    private static partial Regex Ports();
}
