using System.Diagnostics;
using System.Security.Cryptography;
using NexusPipeline.Modules.Configuration.Recovery;
using NexusPipeline.Platform.Processes;
using System.Text;
using NexusPipeline.Platform.Storage;

namespace NexusPipeline.Modules.Execution;

internal sealed class ProviderWorkerArtifacts : IDisposable
{
    private static readonly object SessionsSync = new();
    private static readonly HashSet<string> ActiveSessions = new(StringComparer.OrdinalIgnoreCase);
    internal static string RootPath => Path.Combine(AppContext.BaseDirectory, ".nxp", "provider-workers");
    internal string DirectoryPath { get; }
    private readonly string _marker;
    private readonly byte[] _receipt;
    private readonly FileStream _ownership;

    internal ProviderWorkerArtifacts(string session, ProcessIdentity worker)
    {
        if (!Guid.TryParseExact(session, "N", out _)) throw new InvalidDataException("provider.artifacts_session");
        using var host = Process.GetCurrentProcess();
        ProcessIdentity hostIdentity = ProcessIdentity.Capture(host)
            ?? throw new IOException("provider.artifacts_host_identity_unavailable");
        DirectoryPath = Path.Combine(RootPath, session);
        _marker = Path.Combine(DirectoryPath, ".nxp-owned.json");
        _receipt = Encoding.UTF8.GetBytes(System.Text.Json.JsonSerializer.Serialize(new
        { owner = "NexusPipeline.ProviderWorker", session, host = hostIdentity, worker, createdAtUtc = DateTimeOffset.UtcNow }));
        lock (SessionsSync)
        {
            PayloadPathSafety.RequireLinkFree(DirectoryPath);
            if (Directory.Exists(DirectoryPath) || File.Exists(DirectoryPath)) throw new IOException("provider.artifacts_exists");
            FileStream? ownership = null;
            try
            {
                Directory.CreateDirectory(DirectoryPath);
                PayloadPathSafety.RequireLinkFree(DirectoryPath);
                _ownership = ownership = new(_marker, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.Read);
                _ownership.Write(_receipt); _ownership.Flush(true);
                ActiveSessions.Add(DirectoryPath);
            }
            catch
            {
                ownership?.Dispose();
                throw;
            }
        }
    }

    internal static IReadOnlyList<ConfigRecoveryIsolationProjection> SnapshotIsolation()
    {
        lock (SessionsSync)
        {
            try
            {
                PayloadPathSafety.RequireLinkFree(RootPath);
                if (File.Exists(RootPath)) return [ConfigRecoveryIsolationProjection.Unknown("provider-artifacts-invalid")];
                if (!Directory.Exists(RootPath)) return [];
                foreach (string path in Directory.EnumerateFileSystemEntries(RootPath))
                {
                    PayloadPathSafety.RequireLinkFree(path);
                    // A dead worker alone cannot prove that all of its descendants stopped.
                    if (!Directory.Exists(path) || !ActiveSessions.Contains(path))
                        return [ConfigRecoveryIsolationProjection.Unknown("provider-artifacts-writers-unconfirmed")];
                }
                return [];
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            { return [ConfigRecoveryIsolationProjection.Unknown("provider-artifacts-unreadable")]; }
        }
    }

    internal void Complete()
    {
        PayloadPathSafety.RequireLinkFree(DirectoryPath);
        _ownership.Position = 0;
        byte[] actual = new byte[_receipt.Length];
        _ownership.ReadExactly(actual);
        if (_ownership.Length != actual.Length || !actual.AsSpan().SequenceEqual(_receipt)) throw new IOException("provider.artifacts_identity_changed");
        var directories = new List<string>();
        void RemoveContents(string directory)
        {
            PayloadPathSafety.RequireLinkFree(directory);
            foreach (string path in Directory.EnumerateFileSystemEntries(directory))
            {
                PayloadPathSafety.RequireLinkFree(path);
                if (Directory.Exists(path)) { RemoveContents(path); directories.Add(path); }
                else if (path != _marker)
                {
                    long size; string hash;
                    using (var bytes = File.OpenRead(path)) { size = bytes.Length; hash = Convert.ToHexStringLower(SHA256.HashData(bytes)); }
                    VerifiedFileDeletion.Delete(path, size, hash);
                }
            }
        }
        RemoveContents(DirectoryPath);
        // The receipt stays locked until every descendant has been checked and removed.
        _ownership.Dispose();
        VerifiedFileDeletion.Delete(_marker, _receipt.Length, Convert.ToHexStringLower(SHA256.HashData(_receipt)));
        foreach (string path in directories) { PayloadPathSafety.RequireLinkFree(path); Directory.Delete(path); }
        Directory.Delete(DirectoryPath);
    }

    public void Dispose()
    {
        lock (SessionsSync)
        {
            try { _ownership.Dispose(); }
            finally { ActiveSessions.Remove(DirectoryPath); }
        }
    }
}
