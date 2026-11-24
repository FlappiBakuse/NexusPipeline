using System.Runtime.Versioning;
using System.Security.Cryptography;
using NexusPipeline.Platform.Security;
using NexusPipeline.Platform.Storage.Sqlite;

namespace NexusPipeline.Platform.Storage.Secrets;

[SupportedOSPlatform("windows")]
internal sealed class DraftSecretFiles : IDisposable
{
    private readonly RuntimeInstance _instance;
    private readonly StateDatabase _database;
    private readonly List<string> _markers = [];
    private readonly List<SecretPayloadLease> _holds = [];

    internal DraftSecretFiles(RuntimeInstance instance, StateDatabase database, IEnumerable<DraftProjectSecrets> candidates)
    {
        _instance = instance; _database = database;
        try
        {
            foreach (var value in candidates.SelectMany(candidate => candidate.Payloads)) Install(value.Id, value.Payload);
        }
        catch (Exception error) { Release(error); throw; }
    }

    private void Install(Guid id, ProtectedSecretPayload payload)
    {
        var directory = Staging(_instance);
        Directory.CreateDirectory(directory);
        var files = new SecretPayloadFiles(_instance, new());
        var identity = new SecretPayloadIdentity(id, payload.ProtectionScheme, payload.OwnerSid, payload.Sha256, payload.Ciphertext.Length);
        var destination = files.PathFor(identity);
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        var marker = PhysicalPathPolicy.NormalizeWithoutLinks(Path.Combine(directory, $"{id:D}-{payload.Sha256}.pending"));
        if (File.Exists(marker)) Cleanup(_instance, _database, marker);
        var part = PhysicalPathPolicy.NormalizeWithoutLinks(Path.ChangeExtension(marker, ".part"));
        if (File.Exists(destination) || File.Exists(part)) throw new IOException("secret_candidate_path_occupied");
        using (var created = new FileStream(marker, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        {
            _markers.Add(marker);
            created.Flush(true);
        }
        using (var output = new FileStream(part, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        {
            output.Write(payload.Ciphertext.Span); output.Flush(true);
        }
        File.Move(part, destination);
        _holds.Add(files.HoldVerified(identity, trackPath: true));
    }

    internal void RequireCurrent()
    {
        foreach (var hold in _holds) hold.RequireCurrent();
    }

    internal static void Recover(RuntimeInstance instance, StateDatabase database)
    {
        var directory = Staging(instance);
        if (!Directory.Exists(directory)) return;
        foreach (var marker in Directory.EnumerateFiles(directory, "*.pending")) Cleanup(instance, database, marker);
    }

    private static string Staging(RuntimeInstance instance) => PhysicalPathPolicy.NormalizeWithoutLinks(
        Path.Combine(instance.RuntimeRoot, "secrets", "binding-drafts"));

    private static void Cleanup(RuntimeInstance instance, StateDatabase database, string marker)
    {
        var name = Path.GetFileNameWithoutExtension(marker);
        if (name.Length != 101 || name[36] != '-' || !Guid.TryParseExact(name[..36], "D", out var id) || id == Guid.Empty ||
            name[37..].Any(c => c is not (>= 'a' and <= 'f' or >= '0' and <= '9'))) return;
        marker = PhysicalPathPolicy.NormalizeWithoutLinks(marker);
        var digest = name[37..];
        using var connection = DatabaseConnections.Open(database.DatabasePath, readOnly: true, domains: DatabaseDomain.Runtime | DatabaseDomain.System);
        using var query = connection.CreateCommand();
        query.CommandText = "SELECT digest FROM system.secret_payloads WHERE secret_id=$id;";
        query.Parameters.AddWithValue("$id", id.ToString("D"));
        var stored = query.ExecuteScalar() as string;
        if (stored is not null && stored != digest) throw new IOException("secret_candidate_owner_changed");
        var path = PhysicalPathPolicy.NormalizeWithoutLinks(Path.Combine(instance.RuntimeRoot, "secrets", "payloads", name + ".bin"));
        if (stored is null && File.Exists(path))
        {
            using (var input = File.OpenRead(path))
                if (Convert.ToHexStringLower(SHA256.HashData(input)) != digest) throw new IOException("secret_candidate_bytes_changed");
            File.Delete(path);
        }
        File.Delete(PhysicalPathPolicy.NormalizeWithoutLinks(Path.ChangeExtension(marker, ".part")));
        File.Delete(marker);
    }

    public void Dispose() => Release();

    internal void Release(Exception? primary = null)
    {
        var failures = new CleanupFailures();
        foreach (var hold in _holds) failures.Attempt(hold.Dispose);
        foreach (var marker in _markers) failures.Attempt(() => Cleanup(_instance, _database, marker));
        failures.Preserve(primary);
    }
}
