using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography;

namespace NexusPipeline.Platform.Storage.Maintenance;

internal static class HostUpdateTrust
{
    internal static void RequireCandidateStartup(string version)
    {
        if (Environment.GetEnvironmentVariable("NEXUS_TEST_UPDATE_FAIL_VERSION") == version)
            throw new IOException("maintenance_candidate_fixture_failure");
    }

    internal static UpdateReleaseTrust Create() => new(
        new Dictionary<string, byte[]>
        {
            ["fixture-only"] = Convert.FromBase64String("MFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAE6uyjBJaylz5W+08fYBHMupD7vnjEOsNOFQGlLksjWMd+NAK85+0rrHkg8zoubm64PNjUvrMV7JOhQKRVUdWDBg==")
        },
        new("https://updates.example.test/metadata/update/1/"), new("https://store.example.test/metadata/store/1/"),
        [new("https://assets.example.test/releases/download/")], 1, 1, "test");

    internal static Func<HttpClient> FixtureTransport(Func<HttpClient> normal)
    {
        var endpoint = Environment.GetEnvironmentVariable("NEXUS_TEST_UPDATE_ENDPOINT");
        if (endpoint is null) return normal;
        var pin = Environment.GetEnvironmentVariable("NEXUS_TEST_UPDATE_CERT_SHA256");
        if (!int.TryParse(endpoint, out var port) || port is < 1024 or > 65535 || pin is not { Length: 64 } ||
            pin.Any(character => character is not (>= '0' and <= '9' or >= 'a' and <= 'f')))
            throw new ArgumentException("maintenance_test_transport_invalid");
        return () => new(new SocketsHttpHandler
        {
            AllowAutoRedirect = false, UseProxy = false,
            ConnectCallback = (context, cancellation) => ConnectAsync(context, port, cancellation),
            SslOptions = new SslClientAuthenticationOptions
            {
                RemoteCertificateValidationCallback = (_, certificate, _, _) =>
                    certificate is not null && Convert.ToHexStringLower(SHA256.HashData(certificate.GetRawCertData())) == pin
            }
        });
    }

    private static async ValueTask<Stream> ConnectAsync(SocketsHttpConnectionContext context, int port, CancellationToken cancellation)
    {
        if (context.DnsEndPoint.Port != 443 || context.DnsEndPoint.Host is not ("updates.example.test" or "assets.example.test"))
            throw new IOException("maintenance_test_host_invalid");
        var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        try
        {
            await socket.ConnectAsync(IPAddress.Loopback, port, cancellation);
            return new NetworkStream(socket, ownsSocket: true);
        }
        catch { socket.Dispose(); throw; }
    }
}
