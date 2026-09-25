using System.Net.Sockets;
using System.Text;

namespace Pickle.Network;

/// <param name="Servers">The whois servers asked, in order (IANA, then the registry, then the registrar).</param>
/// <param name="Fields">Common fields pulled out of the last answer (registrar, dates, name servers, status, org…).</param>
public sealed record WhoisResult(string Query, IReadOnlyList<string> Servers, string Text, IReadOnlyDictionary<string, string> Fields);

/// <summary>Whois over TCP 43, following IANA's referral to the registry and, for thin registries, to the registrar.</summary>
public static class Whois
{
    public const string IanaServer = "whois.iana.org";
    private const int MaxLength = 256 * 1024;

    private static readonly (string Label, string[] Keys)[] Summary =
    [
        ("Domain", ["Domain Name", "domain"]),
        ("Registrar", ["Registrar", "registrar", "Sponsoring Registrar"]),
        ("Created", ["Creation Date", "created", "Registered on", "RegDate"]),
        ("Updated", ["Updated Date", "last-modified", "changed", "Last updated", "Updated"]),
        ("Expires", ["Registry Expiry Date", "Registrar Registration Expiration Date", "Expiry date", "expires", "paid-till"]),
        ("Status", ["Domain Status", "status"]),
        ("Name servers", ["Name Server", "nserver", "Name servers"]),
        ("Organisation", ["Registrant Organization", "OrgName", "org-name", "organisation", "descr"]),
        ("Network", ["NetRange", "inetnum", "inet6num", "CIDR", "route"]),
        ("Country", ["Registrant Country", "Country", "country"]),
        ("Abuse", ["OrgAbuseEmail", "abuse-mailbox", "Registrar Abuse Contact Email"]),
    ];

    public static async Task<WhoisResult> LookupAsync(string query, TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        query = query.Trim();
        var servers = new List<string>();
        var server = IanaServer;
        var text = string.Empty;
        for (var hop = 0; hop < 3 && server is not null; hop++)
        {
            servers.Add(server);
            text = await QueryAsync(server, query, timeout, cancellationToken).ConfigureAwait(false);
            var next = Referral(text);
            server = next is not null && !servers.Contains(next, StringComparer.OrdinalIgnoreCase) ? next : null;
        }

        return new WhoisResult(query, servers, text, ParseFields(text));
    }

    public static async Task<string> QueryAsync(string server, string query, TimeSpan timeout, CancellationToken cancellationToken)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(timeout);
        try
        {
            using var tcp = new TcpClient();
            await tcp.ConnectAsync(server, 43, cts.Token).ConfigureAwait(false);
            var stream = tcp.GetStream();
            await stream.WriteAsync(Encoding.ASCII.GetBytes(query + "\r\n"), cts.Token).ConfigureAwait(false);
            using var buffer = new MemoryStream();
            var chunk = new byte[8192];
            int read;
            while (buffer.Length < MaxLength && (read = await stream.ReadAsync(chunk, cts.Token).ConfigureAwait(false)) > 0)
            {
                buffer.Write(chunk, 0, read);
            }

            return Encoding.UTF8.GetString(buffer.ToArray());
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException($"{server} didn't answer within {timeout.TotalSeconds:0} s.");
        }
    }

    /// <summary>The next server to ask: IANA's "refer:", or a registry's "Registrar WHOIS Server:".</summary>
    internal static string? Referral(string text)
    {
        foreach (var line in Lines(text))
        {
            var (key, value) = line;
            if (key.Equals("refer", StringComparison.OrdinalIgnoreCase) || key.Equals("whois", StringComparison.OrdinalIgnoreCase)
                || key.Equals("Registrar WHOIS Server", StringComparison.OrdinalIgnoreCase) || key.Equals("ReferralServer", StringComparison.OrdinalIgnoreCase))
            {
                var host = value.Replace("whois://", string.Empty, StringComparison.OrdinalIgnoreCase).Replace("rwhois://", string.Empty, StringComparison.OrdinalIgnoreCase).Trim().TrimEnd('/');
                var colon = host.IndexOf(':', StringComparison.Ordinal);
                host = colon > 0 ? host[..colon] : host;
                if (NetTargets.IsHostName(host))
                {
                    return host.ToLowerInvariant();
                }
            }
        }

        return null;
    }

    internal static IReadOnlyDictionary<string, string> ParseFields(string text)
    {
        var lines = Lines(text).ToList();
        var fields = new Dictionary<string, string>();
        foreach (var (label, keys) in Summary)
        {
            var values = lines.Where(l => keys.Contains(l.Key, StringComparer.OrdinalIgnoreCase)).Select(l => l.Value).Where(v => v.Length > 0)
                .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            if (values.Count > 0)
            {
                fields[label] = label is "Name servers" or "Status" ? string.Join(", ", values.Select(v => v.Split(' ')[0].ToLowerInvariant())) : values[0];
            }
        }

        return fields;
    }

    private static IEnumerable<(string Key, string Value)> Lines(string text)
    {
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line[0] is '%' or '#' or '>')
            {
                continue;
            }

            var colon = line.IndexOf(':', StringComparison.Ordinal);
            if (colon > 0)
            {
                yield return (line[..colon].Trim(), line[(colon + 1)..].Trim());
            }
        }
    }
}
