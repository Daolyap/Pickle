using System.Buffers.Binary;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;

namespace Pickle.Network.Tests;

/// <summary>Real sockets against listeners on 127.0.0.1: no network access needed.</summary>
public sealed class LoopbackTests
{
    private static readonly CancellationToken Ct = TestContext.Current.CancellationToken;

    [Fact]
    public async Task ScannerFindsOpenPortsReadsBannersAndSeesClosedOnes()
    {
        using var ssh = new TcpListener(IPAddress.Loopback, 0);
        ssh.Start();
        var open = ((IPEndPoint)ssh.LocalEndpoint).Port;
        var server = Task.Run(async () =>
        {
            using var client = await ssh.AcceptTcpClientAsync(Ct);
            await client.GetStream().WriteAsync(Encoding.ASCII.GetBytes("SSH-2.0-PickleTest\r\n"), Ct);
            await Task.Delay(500, Ct);
        });
        var closed = FreePort();

        var results = new List<PortScanResult>();
        // Windows retries a refused loopback connect for ~2 s before reporting it.
        await foreach (var result in PortScanner.ScanAsync(["127.0.0.1"], [open, closed], new PortScanOptions(TimeSpan.FromSeconds(6), GrabBanner: true, IncludeClosed: true), Ct))
        {
            results.Add(result);
        }

        var hit = Assert.Single(results, r => r.Port == open);
        Assert.Equal((PortState.Open, "SSH-2.0-PickleTest"), (hit.State, hit.Banner));
        Assert.Equal(PortState.Closed, Assert.Single(results, r => r.Port == closed).State);
        await server;
    }

    [Fact]
    public async Task ScannerReportsProgressAndOnlyOpenPortsByDefault()
    {
        var progress = new List<(int Done, int Total)>();
        var ports = Enumerable.Range(0, 60).Select(_ => FreePort()).Distinct().ToList();
        var results = new List<PortScanResult>();
        await foreach (var result in PortScanner.ScanAsync(["127.0.0.1"], ports, new PortScanOptions(TimeSpan.FromSeconds(2), Progress: new SyncProgress(progress)), Ct))
        {
            results.Add(result);
        }

        Assert.Empty(results);
        Assert.Contains((ports.Count, ports.Count), progress);
    }

    [Fact]
    public async Task DnsQueryParsesAnswersAndFallsBackToTcpWhenTruncated()
    {
        using var udp = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        var endpoint = (IPEndPoint)udp.Client.LocalEndPoint!;
        using var tcp = new TcpListener(IPAddress.Loopback, endpoint.Port);
        tcp.Start();

        var udpServer = Task.Run(async () =>
        {
            var request = await udp.ReceiveAsync(Ct);
            await udp.SendAsync(Reply(request.Buffer, truncated: true, []), request.RemoteEndPoint, Ct);
        });
        var tcpServer = Task.Run(async () =>
        {
            using var client = await tcp.AcceptTcpClientAsync(Ct);
            var stream = client.GetStream();
            var length = new byte[2];
            await stream.ReadExactlyAsync(length, Ct);
            var query = new byte[BinaryPrimitives.ReadUInt16BigEndian(length)];
            await stream.ReadExactlyAsync(query, Ct);
            var reply = Reply(query, truncated: false, [MxRecord(10, "mail.example.test"), TxtRecord("v=spf1 -all")]);
            var framed = new byte[reply.Length + 2];
            BinaryPrimitives.WriteUInt16BigEndian(framed, (ushort)reply.Length);
            reply.CopyTo(framed, 2);
            await stream.WriteAsync(framed, Ct);
        });

        var response = await DnsClient.QueryAsync("example.test", DnsRecordType.MX, endpoint, TimeSpan.FromSeconds(5), Ct);

        Assert.Equal(("NOERROR", "tcp"), (response.Status, response.Transport));
        Assert.Equal(["10 mail.example.test.", "\"v=spf1 -all\""], response.Answers.Select(a => a.Data));
        Assert.Equal("example.test.", response.Answers.First().Name);
        await Task.WhenAll(udpServer, tcpServer);
    }

    [Fact]
    public async Task DnsTimesOutWhenNobodyAnswers()
    {
        using var silent = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        await Assert.ThrowsAsync<TimeoutException>(() =>
            DnsClient.QueryAsync("example.test", DnsRecordType.A, (IPEndPoint)silent.Client.LocalEndPoint!, TimeSpan.FromMilliseconds(300), Ct));
    }

    [Fact]
    public async Task CertificateReportFlagsASelfSignedCertificate()
    {
        using var certificate = SelfSigned("pickle.test");
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var server = Task.Run(async () =>
        {
            using var client = await listener.AcceptTcpClientAsync(Ct);
            using var ssl = new SslStream(client.GetStream());
            await ssl.AuthenticateAsServerAsync(new SslServerAuthenticationOptions { ServerCertificate = certificate }, Ct);
            await Task.Delay(300, Ct);
        });

        var report = await CertInspector.InspectAsync("127.0.0.1", port, TimeSpan.FromSeconds(10), "pickle.test", Ct);

        Assert.Equal("pickle.test", report.Subject);
        Assert.Contains("pickle.test", report.Names);
        Assert.False(report.Trusted);
        Assert.InRange(report.DaysLeft, 28, 30);
        Assert.NotEmpty(report.Problems);
        await server;
    }

    [Fact]
    public async Task HttpProbeFollowsRedirectsAndTimesTheRequest()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var server = Task.Run(async () =>
        {
            foreach (var response in new[]
            {
                "HTTP/1.1 301 Moved Permanently\r\nLocation: /final\r\nContent-Length: 0\r\nConnection: close\r\n\r\n",
                "HTTP/1.1 200 OK\r\nServer: pickle-test\r\nContent-Type: text/plain\r\nContent-Length: 2\r\nConnection: close\r\n\r\nok",
            })
            {
                using var client = await listener.AcceptTcpClientAsync(Ct);
                var stream = client.GetStream();
                var buffer = new byte[4096];
                var read = 0;
                while (!Encoding.ASCII.GetString(buffer, 0, read).Contains("\r\n\r\n", StringComparison.Ordinal))
                {
                    read += await stream.ReadAsync(buffer.AsMemory(read), Ct);
                }

                await stream.WriteAsync(Encoding.ASCII.GetBytes(response), Ct);
            }
        });

        var report = await HttpProbe.RunAsync($"http://127.0.0.1:{port}/start", cancellationToken: Ct);

        Assert.Equal((200, "pickle-test", "text/plain"), (report.Status, report.Server, report.ContentType));
        var hop = Assert.Single(report.Redirects);
        Assert.Equal((301, $"http://127.0.0.1:{port}/final"), (hop.Status, hop.Location));
        Assert.Equal("127.0.0.1", report.RemoteAddress);
        Assert.True(report.Total >= report.FirstByte);
        await server;
    }

    private static int FreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    private static X509Certificate2 SelfSigned(string name)
    {
        using var key = RSA.Create(2048);
        var request = new CertificateRequest($"CN={name}", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var san = new SubjectAlternativeNameBuilder();
        san.AddDnsName(name);
        request.CertificateExtensions.Add(san.Build());
        using var ephemeral = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(29.5));

        // SChannel (Windows) can't use an ephemeral key for a server; a PFX round trip gives it a usable one.
        return X509CertificateLoader.LoadPkcs12(ephemeral.Export(X509ContentType.Pfx), null);
    }

    private static byte[] Reply(byte[] query, bool truncated, IReadOnlyList<byte[]> answers)
    {
        var questionEnd = 12;
        while (query[questionEnd] != 0)
        {
            questionEnd += query[questionEnd] + 1;
        }

        questionEnd += 5;
        var reply = new List<byte>();
        reply.AddRange(query[..2]);
        reply.AddRange(truncated ? [0x83, 0x80] : [0x81, 0x80]);
        reply.AddRange([0, 1, 0, (byte)answers.Count, 0, 0, 0, 0]);
        reply.AddRange(query[12..questionEnd]);
        foreach (var answer in answers)
        {
            reply.AddRange(answer);
        }

        return [.. reply];
    }

    // Answers name the question by a compression pointer to offset 12.
    private static byte[] Record(ushort type, byte[] data) =>
        [0xC0, 12, (byte)(type >> 8), (byte)type, 0, 1, 0, 0, 0x0E, 0x10, (byte)(data.Length >> 8), (byte)data.Length, .. data];

    private static byte[] MxRecord(ushort preference, string host) =>
        Record(15, [(byte)(preference >> 8), (byte)preference, .. Name(host)]);

    private static byte[] TxtRecord(string text) =>
        Record(16, [(byte)text.Length, .. Encoding.ASCII.GetBytes(text)]);

    private static byte[] Name(string name) =>
        [.. name.Split('.').SelectMany(label => new[] { (byte)label.Length }.Concat(Encoding.ASCII.GetBytes(label))), 0];

    private sealed class SyncProgress(List<(int Done, int Total)> into) : IProgress<(int Done, int Total)>
    {
        public void Report((int Done, int Total) value)
        {
            lock (into)
            {
                into.Add(value);
            }
        }
    }
}
