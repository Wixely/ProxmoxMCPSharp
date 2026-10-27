using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Microsoft.Extensions.Options;
using ProxmoxMCPSharp.Configuration;
using ProxmoxMCPSharp.Services;
using Xunit;

namespace ProxmoxMCPSharp.Tests;

// Real TLS on loopback: a leaf issued by a private CA, presented for 127.0.0.1.
public sealed class CertificateHostnameTests
{
    [Fact]
    public async Task CaCertificateAcceptsMismatchedNameByDefault()
    {
        await using var server = await TlsServer.StartAsync(sanForLoopback: false);
        await NewClient(server, verifyHostname: false).GetAsync("version");
    }

    [Fact]
    public async Task VerifyCertificateHostnameRejectsMismatchedName()
    {
        await using var server = await TlsServer.StartAsync(sanForLoopback: false);
        var ex = await Assert.ThrowsAnyAsync<HttpRequestException>(() => NewClient(server, verifyHostname: true).GetAsync("version"));
        var endpoint = Assert.IsType<ProxmoxEndpointException>(ex);
        Assert.Equal("tls-failure", Assert.Single(endpoint.Failures).Failure);
    }

    [Fact]
    public async Task VerifyCertificateHostnameAcceptsMatchingName()
    {
        await using var server = await TlsServer.StartAsync(sanForLoopback: true);
        await NewClient(server, verifyHostname: true).GetAsync("version");
    }

    [Fact]
    public async Task CertificateFromAnotherCaIsRejectedEitherWay()
    {
        await using var server = await TlsServer.StartAsync(sanForLoopback: true);
        using var other = TlsServer.CreateCa("Other CA");
        var otherCa = TlsServer.WriteCaFile(other);
        try
        {
            foreach (var verify in new[] { false, true })
            {
                var pve = NewClient(server, verify, otherCa);
                await Assert.ThrowsAnyAsync<HttpRequestException>(() => pve.GetAsync("version"));
            }
        }
        finally
        {
            File.Delete(otherCa);
        }
    }

    private static ProxmoxClient NewClient(TlsServer server, bool verifyHostname, string? caPath = null) =>
        new(Options.Create(new ProxmoxOptions
        {
            BaseUrl = $"https://127.0.0.1:{server.Port}/",
            ApiTokenId = "probe@pve!tk",
            ApiTokenSecret = "not-a-secret",
            CaCertificatePath = caPath ?? server.CaPath,
            VerifyCertificateHostname = verifyHostname,
            EndpointConnectTimeoutSeconds = 5,
            RequestTimeoutSeconds = 15,
        }));

    private sealed class TlsServer : IAsyncDisposable
    {
        private readonly TcpListener _listener;
        private readonly X509Certificate2 _leaf;
        private readonly CancellationTokenSource _stop = new();
        private readonly Task _loop;

        public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;
        public string CaPath { get; }

        private TlsServer(X509Certificate2 leaf, string caPath)
        {
            _leaf = leaf;
            CaPath = caPath;
            _listener = new TcpListener(IPAddress.Loopback, 0);
            _listener.Start();
            _loop = Task.Run(AcceptLoopAsync);
        }

        public static Task<TlsServer> StartAsync(bool sanForLoopback)
        {
            using var ca = CreateCa("Test Cluster CA");
            var leaf = CreateLeaf(ca, sanForLoopback);
            return Task.FromResult(new TlsServer(leaf, WriteCaFile(ca)));
        }

        public static X509Certificate2 CreateCa(string name)
        {
            using var key = RSA.Create(2048);
            var request = new CertificateRequest($"CN={name}", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            request.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
            request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign, true));
            request.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(request.PublicKey, false));
            return request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(30));
        }

        public static string WriteCaFile(X509Certificate2 ca)
        {
            var path = Path.Combine(Path.GetTempPath(), $"pmx-ca-{Guid.NewGuid():N}.pem");
            File.WriteAllText(path, ca.ExportCertificatePem());
            return path;
        }

        private static X509Certificate2 CreateLeaf(X509Certificate2 ca, bool sanForLoopback)
        {
            using var key = RSA.Create(2048);
            var request = new CertificateRequest("CN=pve1.test", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            var san = new SubjectAlternativeNameBuilder();
            san.AddDnsName("pve1.test");
            if (sanForLoopback) san.AddIpAddress(IPAddress.Loopback);
            request.CertificateExtensions.Add(san.Build());
            request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, false));
            request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension([new Oid("1.3.6.1.5.5.7.3.1")], false));
            request.CertificateExtensions.Add(X509AuthorityKeyIdentifierExtension.CreateFromCertificate(ca, true, false));
            using var issued = request.Create(ca, DateTimeOffset.UtcNow.AddHours(-1), DateTimeOffset.UtcNow.AddDays(7), RandomNumberGenerator.GetBytes(8));
            using var withKey = issued.CopyWithPrivateKey(key);
            // SChannel needs a key it can load, so round-trip through PKCS#12.
            return X509CertificateLoader.LoadPkcs12(withKey.Export(X509ContentType.Pkcs12), null);
        }

        private async Task AcceptLoopAsync()
        {
            while (!_stop.IsCancellationRequested)
            {
                TcpClient tcp;
                try { tcp = await _listener.AcceptTcpClientAsync(_stop.Token); }
                catch { return; }
                _ = Task.Run(() => ServeAsync(tcp));
            }
        }

        private async Task ServeAsync(TcpClient tcp)
        {
            using (tcp)
            {
                try
                {
                    await using var tls = new SslStream(tcp.GetStream());
                    await tls.AuthenticateAsServerAsync(_leaf);
                    var buffer = new byte[8192];
                    var received = new StringBuilder();
                    while (!received.ToString().Contains("\r\n\r\n"))
                    {
                        var n = await tls.ReadAsync(buffer);
                        if (n == 0) return;
                        received.Append(Encoding.ASCII.GetString(buffer, 0, n));
                    }
                    var body = "{\"data\":{\"version\":\"8.4\",\"release\":\"8.4\"}}";
                    var response = $"HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nContent-Length: {body.Length}\r\nConnection: close\r\n\r\n{body}";
                    await tls.WriteAsync(Encoding.ASCII.GetBytes(response));
                }
                catch
                {
                    // Rejected handshakes are expected in the negative tests.
                }
            }
        }

        public async ValueTask DisposeAsync()
        {
            _stop.Cancel();
            _listener.Stop();
            try { await _loop; } catch { }
            _leaf.Dispose();
            try { File.Delete(CaPath); } catch { }
        }
    }
}
