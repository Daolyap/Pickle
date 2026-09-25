using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;

namespace Pickle.Network;

public sealed record CertInfo(string Subject, string Issuer, DateTime NotBefore, DateTime NotAfter, string Thumbprint, string KeyAlgorithm);

public sealed record CertReport(
    string Host,
    int Port,
    string Protocol,
    string Cipher,
    string Subject,
    string Issuer,
    DateTime NotBefore,
    DateTime NotAfter,
    int DaysLeft,
    IReadOnlyList<string> Names,
    string Thumbprint,
    bool Trusted,
    string Problems,
    IReadOnlyList<CertInfo> Chain);

/// <summary>Connects with TLS and reports the server's certificate and chain (expiry, names, trust problems).</summary>
public static class CertInspector
{
    public static async Task<CertReport> InspectAsync(string host, int port, TimeSpan timeout, string? serverName = null, CancellationToken cancellationToken = default)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(timeout);
        X509Certificate2? leaf = null;
        var chain = new List<CertInfo>();
        var errors = SslPolicyErrors.None;
        var chainStatus = new List<string>();
        try
        {
            using var tcp = new TcpClient();
            await tcp.ConnectAsync(host, port, cts.Token).ConfigureAwait(false);

            // Inspection only: nothing is sent after the handshake, so an untrusted certificate is reported, not refused.
            using var ssl = new SslStream(tcp.GetStream(), leaveInnerStreamOpen: false, (_, certificate, x509Chain, policyErrors) =>
            {
                errors = policyErrors;
                if (certificate is not null)
                {
                    leaf = new X509Certificate2(certificate);
                }

                if (x509Chain is not null)
                {
                    chain.AddRange(x509Chain.ChainElements.Select(e => Info(e.Certificate)));
                    chainStatus.AddRange(x509Chain.ChainStatus.Where(s => s.Status != X509ChainStatusFlags.NoError).Select(s => s.StatusInformation.Trim()));
                }

                return true;
            });
            await ssl.AuthenticateAsClientAsync(
                new SslClientAuthenticationOptions { TargetHost = serverName ?? host, EnabledSslProtocols = SslProtocols.None },
                cts.Token).ConfigureAwait(false);

            if (leaf is null)
            {
                throw new InvalidOperationException($"{host}:{port} sent no certificate.");
            }

            using (leaf)
            {
                var problems = new List<string>();
                if (errors.HasFlag(SslPolicyErrors.RemoteCertificateNameMismatch))
                {
                    problems.Add($"name mismatch (not valid for {serverName ?? host})");
                }

                if (errors.HasFlag(SslPolicyErrors.RemoteCertificateChainErrors))
                {
                    problems.AddRange(chainStatus.Count > 0 ? chainStatus.Distinct() : ["untrusted chain"]);
                }

                var daysLeft = (int)Math.Floor((leaf.NotAfter.ToUniversalTime() - DateTime.UtcNow).TotalDays);
                if (daysLeft < 0)
                {
                    problems.Add("expired");
                }

                return new CertReport(
                    host,
                    port,
                    ssl.SslProtocol.ToString(),
#pragma warning disable SYSLIB0058 // The cipher suite is what a report shows; the replacement API is not on every platform.
                    ssl.NegotiatedCipherSuite.ToString(),
#pragma warning restore SYSLIB0058
                    leaf.GetNameInfo(X509NameType.SimpleName, false),
                    leaf.GetNameInfo(X509NameType.SimpleName, true),
                    leaf.NotBefore,
                    leaf.NotAfter,
                    daysLeft,
                    SubjectAlternativeNames(leaf),
                    leaf.Thumbprint,
                    errors == SslPolicyErrors.None,
                    string.Join("; ", problems),
                    chain);
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException($"{host}:{port} didn't complete a TLS handshake within {timeout.TotalSeconds:0} s.");
        }
    }

    private static CertInfo Info(X509Certificate2 c) =>
        new(c.GetNameInfo(X509NameType.SimpleName, false), c.GetNameInfo(X509NameType.SimpleName, true), c.NotBefore, c.NotAfter, c.Thumbprint, c.PublicKey.Oid.FriendlyName ?? c.PublicKey.Oid.Value ?? "?");

    internal static IReadOnlyList<string> SubjectAlternativeNames(X509Certificate2 certificate)
    {
        var names = new List<string>();
        foreach (var extension in certificate.Extensions.OfType<X509SubjectAlternativeNameExtension>())
        {
            names.AddRange(extension.EnumerateDnsNames());
            names.AddRange(extension.EnumerateIPAddresses().Select(a => a.ToString()));
        }

        return names;
    }
}
