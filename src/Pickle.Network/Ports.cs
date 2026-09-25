namespace Pickle.Network;

/// <summary>
/// Port lists: <c>22,80,443</c>, <c>1-1024</c>, and named sets (<c>top100</c>, <c>top1000</c>-style <c>common</c>,
/// <c>web</c>, <c>windows</c>, <c>db</c>, <c>mail</c>, <c>remote</c>, <c>all</c>), combinable: <c>web,db,8081</c>.
/// </summary>
public static class PortSpec
{
    /// <summary>nmap's 100 most common TCP ports (<c>nmap -F</c>).</summary>
    public static readonly int[] Top100 =
    [
        7, 9, 13, 21, 22, 23, 25, 26, 37, 53, 79, 80, 81, 88, 106, 110, 111, 113, 119, 135, 139, 143, 144, 179, 199,
        389, 427, 443, 444, 445, 465, 513, 514, 515, 543, 544, 548, 554, 587, 631, 646, 873, 990, 993, 995, 1025, 1026,
        1027, 1028, 1029, 1110, 1433, 1720, 1723, 1755, 1900, 2000, 2001, 2049, 2121, 2717, 3000, 3128, 3306, 3389,
        3986, 4899, 5000, 5009, 5051, 5060, 5101, 5190, 5357, 5432, 5631, 5666, 5800, 5900, 6000, 6001, 6646, 7070,
        8000, 8008, 8009, 8080, 8081, 8443, 8888, 9100, 9999, 10000, 32768, 49152, 49153, 49154, 49155, 49156, 49157,
    ];

    public static readonly IReadOnlyDictionary<string, int[]> Sets = new Dictionary<string, int[]>(StringComparer.OrdinalIgnoreCase)
    {
        ["top100"] = Top100,
        ["top"] = Top100,
        ["fast"] = Top100,
        ["common"] = [.. Top100.Concat(PortCatalog.Known.Keys).Distinct().Order()],
        ["web"] = [80, 443, 8000, 8008, 8080, 8081, 8443, 8888, 3000, 5000, 9000, 9443],
        ["windows"] = [53, 88, 135, 139, 389, 445, 464, 593, 636, 3268, 3269, 3389, 5985, 5986, 9389],
        ["db"] = [1433, 1521, 3306, 5432, 5984, 6379, 7474, 8086, 9042, 9200, 11211, 27017],
        ["mail"] = [25, 110, 143, 465, 587, 993, 995],
        ["remote"] = [22, 23, 3389, 5900, 5938, 5985, 5986, 6568],
        ["printers"] = [515, 631, 9100],
        ["all"] = [.. Enumerable.Range(1, 65535)],
    };

    public static IReadOnlyList<int> Parse(string spec)
    {
        var ports = new SortedSet<int>();
        foreach (var part in spec.Split([',', ' ', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (Sets.TryGetValue(part, out var set))
            {
                ports.UnionWith(set);
                continue;
            }

            var dash = part.IndexOf('-', StringComparison.Ordinal);
            if (dash >= 0)
            {
                var from = dash == 0 ? 1 : ParsePort(part[..dash]);
                var to = dash == part.Length - 1 ? 65535 : ParsePort(part[(dash + 1)..]);
                if (to < from)
                {
                    throw new ArgumentException($"Port range '{part}' ends before it starts.");
                }

                ports.UnionWith(Enumerable.Range(from, to - from + 1));
                continue;
            }

            ports.Add(ParsePort(part));
        }

        return ports.Count > 0 ? [.. ports] : throw new ArgumentException("No ports given.");
    }

    private static int ParsePort(string text) =>
        int.TryParse(text, out var port) && port is >= 1 and <= 65535
            ? port
            : throw new ArgumentException($"'{text}' is not a port (1–65535) or a set ({string.Join(", ", Sets.Keys.Where(k => k is not "top" and not "fast"))}).");
}

/// <summary>Well-known service names by TCP port.</summary>
public static class PortCatalog
{
    public static readonly IReadOnlyDictionary<int, string> Known = new Dictionary<int, string>
    {
        [7] = "echo",
        [20] = "ftp-data",
        [21] = "ftp",
        [22] = "ssh",
        [23] = "telnet",
        [25] = "smtp",
        [37] = "time",
        [53] = "dns",
        [67] = "dhcp",
        [69] = "tftp",
        [79] = "finger",
        [80] = "http",
        [81] = "http-alt",
        [88] = "kerberos",
        [110] = "pop3",
        [111] = "rpcbind",
        [113] = "ident",
        [119] = "nntp",
        [123] = "ntp",
        [135] = "msrpc",
        [137] = "netbios-ns",
        [138] = "netbios-dgm",
        [139] = "netbios-ssn",
        [143] = "imap",
        [161] = "snmp",
        [162] = "snmptrap",
        [179] = "bgp",
        [389] = "ldap",
        [427] = "svrloc",
        [443] = "https",
        [445] = "smb",
        [464] = "kpasswd",
        [465] = "smtps",
        [500] = "isakmp",
        [502] = "modbus",
        [512] = "exec",
        [513] = "login",
        [514] = "shell",
        [515] = "printer",
        [548] = "afp",
        [554] = "rtsp",
        [587] = "submission",
        [593] = "http-rpc-epmap",
        [623] = "ipmi",
        [631] = "ipp",
        [636] = "ldaps",
        [873] = "rsync",
        [902] = "vmware-auth",
        [989] = "ftps-data",
        [990] = "ftps",
        [993] = "imaps",
        [995] = "pop3s",
        [1080] = "socks",
        [1194] = "openvpn",
        [1433] = "mssql",
        [1434] = "mssql-browser",
        [1521] = "oracle",
        [1701] = "l2tp",
        [1723] = "pptp",
        [1812] = "radius",
        [1883] = "mqtt",
        [1900] = "upnp",
        [2049] = "nfs",
        [2082] = "cpanel",
        [2083] = "cpanel-ssl",
        [2181] = "zookeeper",
        [2375] = "docker",
        [2376] = "docker-tls",
        [2379] = "etcd",
        [3000] = "dev-http",
        [3128] = "squid-proxy",
        [3268] = "globalcatalog",
        [3269] = "globalcatalog-ssl",
        [3306] = "mysql",
        [3389] = "rdp",
        [3478] = "stun",
        [4369] = "epmd",
        [4443] = "https-alt",
        [4500] = "ipsec-nat-t",
        [4899] = "radmin",
        [5000] = "upnp/http",
        [5060] = "sip",
        [5061] = "sips",
        [5353] = "mdns",
        [5357] = "wsdapi",
        [5432] = "postgresql",
        [5555] = "adb",
        [5601] = "kibana",
        [5672] = "amqp",
        [5800] = "vnc-http",
        [5900] = "vnc",
        [5938] = "teamviewer",
        [5984] = "couchdb",
        [5985] = "winrm",
        [5986] = "winrm-https",
        [6000] = "x11",
        [6379] = "redis",
        [6443] = "kubernetes-api",
        [6568] = "anydesk",
        [6881] = "bittorrent",
        [7001] = "weblogic",
        [7474] = "neo4j",
        [8000] = "http-alt",
        [8006] = "proxmox",
        [8008] = "http-alt",
        [8009] = "ajp13",
        [8080] = "http-proxy",
        [8081] = "http-alt",
        [8086] = "influxdb",
        [8088] = "http-alt",
        [8443] = "https-alt",
        [8883] = "mqtt-tls",
        [8888] = "http-alt",
        [9000] = "http-alt",
        [9042] = "cassandra",
        [9090] = "prometheus",
        [9092] = "kafka",
        [9100] = "jetdirect",
        [9200] = "elasticsearch",
        [9389] = "adws",
        [9443] = "https-alt",
        [10000] = "webmin",
        [10250] = "kubelet",
        [11211] = "memcached",
        [15672] = "rabbitmq-mgmt",
        [27017] = "mongodb",
        [32400] = "plex",
        [49152] = "ms-dynamic",
        [51820] = "wireguard",
    };

    public static string Name(int port) => Known.TryGetValue(port, out var name) ? name : string.Empty;

    /// <summary>Ports whose service speaks TLS first (no plain-text banner to read).</summary>
    public static bool IsTls(int port) => port is 443 or 465 or 636 or 993 or 995 or 990 or 2083 or 2376 or 3269 or 4443 or 5061 or 5986 or 6443 or 8443 or 8883 or 9443 or 10250;

    /// <summary>Ports that answer HTTP (the scanner asks <c>HEAD /</c> to read a Server header).</summary>
    public static bool IsHttp(int port) => port is 80 or 81 or 3000 or 5000 or 8000 or 8008 or 8080 or 8081 or 8088 or 8888 or 9000 or 9090 or 9200 or 10000;
}
