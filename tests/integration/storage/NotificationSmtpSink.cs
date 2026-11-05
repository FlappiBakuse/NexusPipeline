using System.Runtime.Versioning;
using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using NexusPipeline.Platform.Processes;

namespace NexusPipeline.StorageTests;

[SupportedOSPlatform("windows")]
internal sealed class NotificationSmtpSink : IAsyncDisposable
{
    private readonly OwnedWorkerJob _job = new();
    private readonly string _root;
    private FileStream? _scriptFile;
    private int _certificateChecks;
    internal int Port { get; }
    internal X509Certificate2 Certificate { get; }
    internal int Messages => State().GetProperty("messages").GetInt32();
    internal int Authenticated => State().GetProperty("authenticated").GetInt32();
    internal bool TlsNegotiated => State().GetProperty("tls").GetBoolean();
    internal int TlsAttempts => State().GetProperty("tlsAttempts").GetInt32();
    internal int CertificateChecks => Volatile.Read(ref _certificateChecks);
    internal IEnumerable<string> Failures => State().GetProperty("failures").EnumerateArray().Select(item => item.GetString()!).ToArray();
    internal IReadOnlyList<(long Size, string Hash, string Prefix)> Received => State().GetProperty("received").EnumerateArray()
        .Select(item => (item.GetProperty("size").GetInt64(), item.GetProperty("hash").GetString()!, item.GetProperty("prefix").GetString()!)).ToArray();

    internal NotificationSmtpSink(bool implicitTls, bool startTls, bool dropDataReply = false)
    {
        _root = Path.Combine(Environment.GetEnvironmentVariable("NEXUS_TEST_ROOT")!, "smtp-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        using var key = RSA.Create(2048);
        var request = new CertificateRequest("CN=localhost", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var names = new SubjectAlternativeNameBuilder(); names.AddIpAddress(IPAddress.Loopback); names.AddDnsName("localhost");
        request.CertificateExtensions.Add(names.Build());
        Certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddHours(1));
        File.WriteAllText(Path.Combine(_root, "certificate.pem"), Certificate.ExportCertificatePem(), new UTF8Encoding(false));
        File.WriteAllText(Path.Combine(_root, "private-key.pem"), key.ExportPkcs8PrivateKeyPem(), new UTF8Encoding(false));
        File.WriteAllBytes(Path.Combine(_root, "input.json"), JsonSerializer.SerializeToUtf8Bytes(new { implicitTls, startTls, dropDataReply }));
        try
        {
            var python = Environment.GetEnvironmentVariable("NEXUS_TEST_PYTHON_EXE") ?? throw new IOException("smtp_fixture_python_missing");
            var script = Path.Combine(AppContext.BaseDirectory, "fixtures", "notification_smtp_sink.py");
            _scriptFile = new FileStream(script, FileMode.Open, FileAccess.Read, FileShare.Read);
            File.WriteAllBytes(Path.Combine(_root, "script.json"), JsonSerializer.SerializeToUtf8Bytes(new
            {
                path = script, sha256 = Convert.ToHexStringLower(SHA256.HashData(_scriptFile))
            }));
            _ = _job.Start(python, ["-B", script, _root], _root, Guid.NewGuid(), Guid.NewGuid(), identity =>
                File.WriteAllBytes(Path.Combine(_root, "owner.json"), JsonSerializer.SerializeToUtf8Bytes(identity)));
            var ready = OwnedCrashProcess.ReadReportAsync(Path.Combine(_root, "ready.json")).GetAwaiter().GetResult();
            if (ready.GetProperty("status").GetString() != "ready") throw new IOException("smtp_fixture_start_failed");
            Port = ready.GetProperty("port").GetInt32();
        }
        catch
        {
            _job.Dispose(); _scriptFile?.Dispose(); Certificate.Dispose(); File.Delete(Path.Combine(_root, "private-key.pem")); throw;
        }
    }

    private JsonElement State()
    {
        using var input = new FileStream(Path.Combine(_root, "state.json"), FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var document = JsonDocument.Parse(input);
        return document.RootElement.Clone();
    }

    internal bool VerifyCertificate(X509Certificate? certificate)
    {
        Interlocked.Increment(ref _certificateChecks);
        return certificate is not null && SHA256.HashData(certificate.GetRawCertData()).AsSpan()
            .SequenceEqual(SHA256.HashData(Certificate.RawData));
    }

    public async ValueTask DisposeAsync()
    {
        File.WriteAllBytes(Path.Combine(_root, "stop"), []);
        if (!await _job.StopAsync(TimeSpan.FromSeconds(5))) throw new IOException("smtp_fixture_job_stop_unconfirmed");
        var accounting = _job.GetAccounting();
        if (accounting.ActiveProcesses != 0 || _job.ActiveProcessIds().Count != 0)
            throw new IOException("smtp_fixture_job_stop_unconfirmed");
        File.WriteAllBytes(Path.Combine(_root, "stopped.json"), JsonSerializer.SerializeToUtf8Bytes(accounting));
        _job.Dispose(); _scriptFile?.Dispose(); Certificate.Dispose(); File.Delete(Path.Combine(_root, "private-key.pem"));
    }
}
