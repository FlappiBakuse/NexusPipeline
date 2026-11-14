using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using Microsoft.Data.Sqlite;
using NexusPipeline.Core.Operations;
using NexusPipeline.Core.Secrets;
using NexusPipeline.Platform.Security;

namespace NexusPipeline.Platform.Storage.Secrets;

internal sealed class ReplayKeyLease(Guid id, FileStream stream) : IDisposable
{
    internal Guid Id { get; } = id;
    internal FileStream Stream { get; } = stream;
    public void Dispose() => Stream.Dispose();
}

[SupportedOSPlatform("windows")]
internal static class SecretReplayProof
{
    internal static void Initialize(RuntimeInstance instance, SqliteConnection connection, SqliteTransaction transaction)
    {
        WindowsSecretDirectory.Initialize(instance);
        var id = Guid.NewGuid();
        var key = RandomNumberGenerator.GetBytes(32);
        byte[] ciphertext;
        try { ciphertext = WindowsDataProtection.Transform(key, Entropy(instance.InstanceId, id), protect: true); }
        finally { CryptographicOperations.ZeroMemory(key); }
        using var user = WindowsIdentity.GetCurrent();
        var sid = user.User?.Value ?? throw new IOException("secret_replay_owner_unavailable");
        var digest = Convert.ToHexStringLower(SHA256.HashData(ciphertext));
        var path = PathFor(instance, id, digest);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using (var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
        {
            output.Write(ciphertext);
            output.Flush(flushToDisk: true);
        }
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "INSERT INTO system.secret_replay_key VALUES(1,$id,$owner,$digest,$size);";
        command.Parameters.AddWithValue("$id", id.ToString("D"));
        command.Parameters.AddWithValue("$owner", sid);
        command.Parameters.AddWithValue("$digest", digest);
        command.Parameters.AddWithValue("$size", ciphertext.Length);
        command.ExecuteNonQuery();
    }

    internal static ReplayKeyLease Hold(RuntimeInstance instance, SqliteConnection connection)
    {
        Guid id;
        string owner, digest;
        long size;
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT key_id,owner_sid,digest,size_bytes FROM system.secret_replay_key WHERE singleton=1;";
            using var row = command.ExecuteReader();
            if (!row.Read() || !Guid.TryParseExact(row.GetString(0), "D", out id) || id == Guid.Empty)
                throw new IOException("secret_replay_key_missing");
            owner = row.GetString(1); digest = row.GetString(2); size = row.GetInt64(3);
        }
        using var user = WindowsIdentity.GetCurrent();
        if (owner != user.User?.Value || size is < 1 or > 4096 || digest.Length != 64 ||
            digest.Any(character => character is not (>= 'a' and <= 'f' or >= '0' and <= '9')))
            throw new IOException("secret_replay_key_unverified");
        var input = new FileStream(PathFor(instance, id, digest), FileMode.Open, FileAccess.Read, FileShare.Read);
        try
        {
            if (input.Length != size || Convert.ToHexStringLower(SHA256.HashData(input)) != digest) throw new IOException("secret_replay_key_bytes_unverified");
            input.Position = 0;
            return new(id, input);
        }
        catch { input.Dispose(); throw; }
    }

    internal static string Compute(RuntimeInstance instance, SqliteConnection connection, OperationIdentity identity, SecretValue value)
        => Hash(instance, connection, $"nexus.secret.replay-proof/1/{identity.OperationId:D}/{identity.SubjectId}/{identity.RequestDigest}/", value);

    internal static string Identity(RuntimeInstance instance, SqliteConnection connection, SecretFieldKey key, string version, SecretValue value)
    {
        key.RequireExtensionOwner(key.ExtensionId ?? throw new ArgumentException("work_identity_owner_invalid"));
        if (version is not { Length: >= 1 and <= 32 } || version.Any(c => c is not (>= 'a' and <= 'z' or >= '0' and <= '9' or '-')))
            throw new ArgumentException("work_identity_version_invalid");
        return Hash(instance, connection, $"nexus.work.credential-identity/1/{key.ExtensionId}/{key.Field}/{version}/", value);
    }

    private static string Hash(RuntimeInstance instance, SqliteConnection connection, string domain, SecretValue value)
    {
        using var held = Hold(instance, connection);
        var ciphertext = new byte[checked((int)held.Stream.Length)];
        held.Stream.ReadExactly(ciphertext);
        var key = WindowsDataProtection.Transform(ciphertext, Entropy(instance.InstanceId, held.Id), protect: false);
        try
        {
            if (key.Length != 32) throw new IOException("secret_replay_key_shape_invalid");
            using var hash = IncrementalHash.CreateHMAC(HashAlgorithmName.SHA256, key);
            hash.AppendData(Encoding.UTF8.GetBytes(domain));
            value.Use(chars => hash.AppendData(MemoryMarshal.AsBytes(chars)));
            return Convert.ToHexStringLower(hash.GetHashAndReset());
        }
        finally { CryptographicOperations.ZeroMemory(key); }
    }

    internal static bool Matches(string? expected, string? actual) => expected is null ? actual is null :
        actual is not null && expected.Length == 64 && actual.Length == 64 &&
        CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(expected), Encoding.ASCII.GetBytes(actual));

    private static byte[] Entropy(Guid instanceId, Guid id) => Encoding.UTF8.GetBytes($"nexus.secret.replay-key/1/{instanceId:D}/{id:D}");
    private static string PathFor(RuntimeInstance instance, Guid id, string digest) => PhysicalPathPolicy.NormalizeWithoutLinks(
        Path.Combine(instance.RuntimeRoot, "secrets", "keys", $"{id:D}-{digest}.bin"));
}
