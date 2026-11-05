using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;
using NexusPipeline.Core.Extensions;
using NexusPipeline.Platform.Extensions;

namespace NexusPipeline.Platform.Storage.Sqlite;

internal sealed record ExtensionCatalogSaved(ExtensionCatalogIdentity Identity, string KeyId,
    BlobReference Pointer, BlobReference Catalog, BlobReference Signature);

internal static class ExtensionCatalogRows
{
    internal static ExtensionCatalogSaved? ReadHead(SqliteConnection connection, SqliteTransaction? transaction, ExtensionStoreChannel channel)
    {
        using var command = connection.CreateCommand(); command.Transaction = transaction;
        command.CommandText = "SELECT c.generation_id,c.sequence,c.catalog_sha,c.key_id," +
            "c.pointer_sha,c.pointer_size,c.catalog_size,c.signature_sha,c.signature_size,o.request_digest,o.operation_kind,o.status " +
            "FROM extension_catalog_heads h LEFT JOIN extension_catalog_acceptances c " +
            "ON c.format=h.format AND c.channel=h.channel AND c.sequence=h.sequence AND c.catalog_sha=h.catalog_sha " +
            "LEFT JOIN operations o ON o.operation_id=c.first_operation_id WHERE h.format='nexus.store.catalog' AND h.channel=$channel;";
        command.Parameters.AddWithValue("$channel", Channel(channel));
        ExtensionCatalogSaved? saved;
        using (var row = command.ExecuteReader()) saved = row.Read() ? Read(row, channel) : null;
        command.CommandText = "SELECT sequence,catalog_sha FROM extension_catalog_acceptances " +
            "WHERE format='nexus.store.catalog' AND channel=$channel ORDER BY sequence DESC LIMIT 1;";
        using var last = command.ExecuteReader();
        if (last.Read())
        {
            if (saved is null || last.GetInt64(0) != saved.Identity.Sequence || last.GetString(1) != saved.Identity.Sha256)
                throw Invalid();
        }
        else if (saved is not null) throw Invalid();
        return saved;
    }

    internal static void RequireAcceptedInside(SqliteConnection connection, SqliteTransaction transaction,
        ExtensionCatalogSaved expected)
    {
        using var command = connection.CreateCommand(); command.Transaction = transaction;
        command.CommandText = "SELECT c.generation_id,c.sequence,c.catalog_sha,c.key_id," +
            "c.pointer_sha,c.pointer_size,c.catalog_size,c.signature_sha,c.signature_size,o.request_digest,o.operation_kind,o.status " +
            "FROM extension_catalog_acceptances c LEFT JOIN operations o ON o.operation_id=c.first_operation_id " +
            "WHERE c.format='nexus.store.catalog' AND c.channel=$channel AND c.sequence=$sequence;";
        command.Parameters.AddWithValue("$channel", Channel(expected.Identity.Channel));
        command.Parameters.AddWithValue("$sequence", expected.Identity.Sequence);
        using var row = command.ExecuteReader();
        if (!row.Read() || Read(row, expected.Identity.Channel) != expected) throw Invalid();
    }

    internal static void AcceptInside(SqliteConnection connection, SqliteTransaction transaction,
        ExtensionCatalogSaved candidate, long floor, Guid operationId)
    {
        var current = ReadHead(connection, transaction, candidate.Identity.Channel);
        CatalogSequencePolicy.Require(candidate.Identity, floor,
            current is null ? null : new(current.Identity.Sequence, current.Identity.Sha256));
        if (current?.Identity.Sequence == candidate.Identity.Sequence)
        {
            if (current != candidate) throw Invalid();
            return;
        }
        using var command = connection.CreateCommand(); command.Transaction = transaction;
        command.CommandText = "INSERT INTO extension_catalog_acceptances(format,channel,sequence,generation_id,catalog_sha,key_id," +
            "pointer_sha,pointer_size,catalog_size,signature_sha,signature_size,first_operation_id) " +
            "VALUES('nexus.store.catalog',$channel,$seq,$generation,$sha,$key,$pointer,$psize,$csize,$signature,$ssize,$operation);";
        command.Parameters.AddWithValue("$channel", Channel(candidate.Identity.Channel));
        command.Parameters.AddWithValue("$seq", candidate.Identity.Sequence);
        command.Parameters.AddWithValue("$generation", candidate.Identity.GenerationId.ToString("D"));
        command.Parameters.AddWithValue("$sha", candidate.Catalog.Sha256);
        command.Parameters.AddWithValue("$key", candidate.KeyId);
        command.Parameters.AddWithValue("$pointer", candidate.Pointer.Sha256);
        command.Parameters.AddWithValue("$psize", candidate.Pointer.SizeBytes);
        command.Parameters.AddWithValue("$csize", candidate.Catalog.SizeBytes);
        command.Parameters.AddWithValue("$signature", candidate.Signature.Sha256);
        command.Parameters.AddWithValue("$ssize", candidate.Signature.SizeBytes);
        command.Parameters.AddWithValue("$operation", operationId.ToString("D"));
        command.ExecuteNonQuery();
        command.CommandText = "INSERT INTO extension_catalog_heads(format,channel,sequence,catalog_sha) " +
            "VALUES('nexus.store.catalog',$channel,$seq,$sha) ON CONFLICT(format,channel) " +
            "DO UPDATE SET sequence=excluded.sequence,catalog_sha=excluded.catalog_sha;";
        command.ExecuteNonQuery();
    }

    private static ExtensionCatalogSaved Read(SqliteDataReader row, ExtensionStoreChannel channel)
    {
        if (row.IsDBNull(0) || !Guid.TryParseExact(row.GetString(0), "D", out var generation) || generation == Guid.Empty) throw Invalid();
        var identity = new ExtensionCatalogIdentity(channel, generation, row.GetInt64(1), row.GetString(2));
        CatalogSequencePolicy.Require(identity, 1, null);
        var saved = new ExtensionCatalogSaved(identity, row.GetString(3), Plan(row.GetString(4), row.GetInt64(5)),
            Plan(identity.Sha256, row.GetInt64(6)), Plan(row.GetString(7), row.GetInt64(8)));
        if (row.IsDBNull(9) || row.GetString(9) != RequestDigest(saved) || row.GetString(10) != "extension.catalog.accept" ||
            row.GetString(11) != "completed") throw Invalid();
        return saved;
    }

    private static BlobReference Plan(string sha, long size)
    {
        if (sha.Length != 64 || sha.Any(c => c is not (>= '0' and <= '9' or >= 'a' and <= 'f')) ||
            size is < 1 or > 2097152) throw Invalid();
        return new($"blobs/plans/{sha}.json", size, sha);
    }

    internal static string RequestDigest(ExtensionCatalogSaved value) => Convert.ToHexStringLower(SHA256.HashData(
        Encoding.UTF8.GetBytes(Channel(value.Identity.Channel) + "\n" + value.Pointer.Sha256 + "\n" +
            value.Catalog.Sha256 + "\n" + value.Signature.Sha256)));

    internal static string Channel(ExtensionStoreChannel value) => value switch
    { ExtensionStoreChannel.Stable => "stable", ExtensionStoreChannel.Develop => "develop", _ => throw Invalid() };
    private static IOException Invalid() => new("extension_catalog_cache_unbound");
}
