using System.Diagnostics;
using System.Net;
using System.Net.Sockets;

namespace Pickle.Network;

public sealed record HttpHop(string Url, int Status, string? Location);

public sealed record HttpReport(
    string Url,
    int Status,
    string Reason,
    string Version,
    string? RemoteAddress,
    TimeSpan Dns,
    TimeSpan Connect,
    TimeSpan FirstByte,
    TimeSpan Total,
    long? ContentLength,
    string? ContentType,
    string? Server,
    IReadOnlyList<KeyValuePair<string, string>> Headers,
    IReadOnlyList<HttpHop> Redirects);

/// <summary>One HTTP request with timings (DNS, connect, first byte, total), headers and the redirect chain.</summary>
public static class HttpProbe
{
    public static async Task<HttpReport> RunAsync(
        string url,
        string method = "GET",
        IReadOnlyList<KeyValuePair<string, string>>? headers = null,
        TimeSpan? timeout = null,
        bool followRedirects = true,
        CancellationToken cancellationToken = default)
    {
        if (!url.Contains("://", StringComparison.Ordinal))
        {
            url = "https://" + url;
        }

        var redirects = new List<HttpHop>();
        var current = new Uri(url);
        for (var hop = 0; ; hop++)
        {
            var timing = new Timing();
            using var handler = new SocketsHttpHandler { AllowAutoRedirect = false, ConnectCallback = timing.ConnectAsync, AutomaticDecompression = DecompressionMethods.All };
            using var client = new HttpClient(handler) { Timeout = timeout ?? TimeSpan.FromSeconds(30) };
            using var request = new HttpRequestMessage(new HttpMethod(method.ToUpperInvariant()), current);
            request.Headers.UserAgent.ParseAdd("pickle-http/1.0");
            foreach (var (key, value) in headers ?? [])
            {
                if (!request.Headers.TryAddWithoutValidation(key, value))
                {
                    throw new ArgumentException($"Header '{key}' can't be set on a request.");
                }
            }

            var total = Stopwatch.StartNew();
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            var firstByte = total.Elapsed;
            await response.Content.CopyToAsync(Stream.Null, cancellationToken).ConfigureAwait(false);
            var status = (int)response.StatusCode;
            var location = response.Headers.Location;
            if (followRedirects && status is >= 300 and < 400 && location is not null && hop < 10)
            {
                var next = location.IsAbsoluteUri ? location : new Uri(current, location);
                redirects.Add(new HttpHop(current.ToString(), status, next.ToString()));
                current = next;
                continue;
            }

            var allHeaders = response.Headers.Concat(response.Content.Headers)
                .Select(h => new KeyValuePair<string, string>(h.Key, string.Join(", ", h.Value))).ToList();
            return new HttpReport(
                current.ToString(),
                status,
                response.ReasonPhrase ?? string.Empty,
                "HTTP/" + response.Version,
                timing.Remote,
                timing.Dns,
                timing.Connect,
                firstByte,
                total.Elapsed,
                response.Content.Headers.ContentLength,
                response.Content.Headers.ContentType?.ToString(),
                response.Headers.Server.ToString() is { Length: > 0 } server ? server : null,
                allHeaders,
                redirects);
        }
    }

    private sealed class Timing
    {
        public TimeSpan Dns { get; private set; }

        public TimeSpan Connect { get; private set; }

        public string? Remote { get; private set; }

        public async ValueTask<Stream> ConnectAsync(SocketsHttpConnectionContext context, CancellationToken cancellationToken)
        {
            var watch = Stopwatch.StartNew();
            var addresses = await System.Net.Dns.GetHostAddressesAsync(context.DnsEndPoint.Host, cancellationToken).ConfigureAwait(false);
            Dns = watch.Elapsed;
            var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
            try
            {
                watch.Restart();
                await socket.ConnectAsync(addresses, context.DnsEndPoint.Port, cancellationToken).ConfigureAwait(false);
                Connect = watch.Elapsed;
                Remote = (socket.RemoteEndPoint as IPEndPoint)?.Address.ToString();
                return new NetworkStream(socket, ownsSocket: true);
            }
            catch
            {
                socket.Dispose();
                throw;
            }
        }
    }
}
