using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;
using NexusPipeline.Core.Maintenance;

namespace NexusPipeline.Platform.Storage.Maintenance;

internal sealed record SavedUpdateFeed(UpdateFeedIdentity Identity, string KeyId, BlobReference Feed, BlobReference Signature);

internal static class UpdateFeedRows
{
    internal static SavedUpdateFeed? ReadHead(SqliteConnection connection, SqliteTransaction transaction, HostUpdateChannel channel)
    {
        using var command = connection.CreateCommand(); command.Transaction = transaction;
        command.CommandText = "SELECT f.sequence,f.feed_sha256,f.key_id,f.feed_size,f.signature_sha256,f.signature_size," +
            "o.request_digest,o.operation_kind,o.status FROM trusted_update_feed_heads h LEFT JOIN trusted_update_feeds f " +
            "ON f.format=h.format AND f.channel=h.channel AND f.sequence=h.sequence AND f.feed_sha256=h.feed_sha256 " +
            "LEFT JOIN operations o ON o.operation_id=f.first_operation_id WHERE h.format='nexus.update.feed' AND h.channel=$channel;";
        command.Parameters.AddWithValue("$channel", Channel(channel));
        SavedUpdateFeed? result;
        using (var row = command.ExecuteReader()) result = row.Read() ? Read(row, channel) : null;
        command.CommandText = "SELECT sequence,feed_sha256 FROM trusted_update_feeds WHERE format='nexus.update.feed' " +
            "AND channel=$channel ORDER BY sequence DESC LIMIT 1;";
        using var latest = command.ExecuteReader();
        if (latest.Read())
        {
            if (result is null || result.Identity.Sequence != latest.GetInt64(0) || result.Identity.Sha256 != latest.GetString(1)) throw Invalid();
        }
        else if (result is not null) throw Invalid();
        return result;
    }

    internal static void RequireAccepted(SqliteConnection connection, SqliteTransaction transaction, SavedUpdateFeed expected)
    {
        using var command = connection.CreateCommand(); command.Transaction = transaction;
        command.CommandText = "SELECT f.sequence,f.feed_sha256,f.key_id,f.feed_size,f.signature_sha256,f.signature_size," +
            "o.request_digest,o.operation_kind,o.status FROM trusted_update_feeds f LEFT JOIN operations o " +
            "ON o.operation_id=f.first_operation_id WHERE f.format='nexus.update.feed' AND f.channel=$channel AND f.sequence=$seq;";
        command.Parameters.AddWithValue("$channel", Channel(expected.Identity.Channel));
        command.Parameters.AddWithValue("$seq", expected.Identity.Sequence);
        using var row = command.ExecuteReader();
        if (!row.Read() || Read(row, expected.Identity.Channel) != expected) throw Invalid();
    }

    internal static void Accept(SqliteConnection connection, SqliteTransaction transaction, SavedUpdateFeed proposed, long floor, Guid operationId)
    {
        var current = ReadHead(connection, transaction, proposed.Identity.Channel);
        UpdateFeedPolicy.Require(proposed.Identity, floor, current?.Identity);
        if (current?.Identity.Sequence == proposed.Identity.Sequence)
        {
            if (current != proposed) throw Invalid();
            return;
        }
        using var command = connection.CreateCommand(); command.Transaction = transaction;
        command.CommandText = "INSERT INTO trusted_update_feeds(format,channel,sequence,feed_sha256,key_id,feed_size," +
            "signature_sha256,signature_size,first_operation_id) VALUES('nexus.update.feed',$channel,$seq,$sha,$key,$size,$sig,$sigsize,$operation);";
        command.Parameters.AddWithValue("$channel", Channel(proposed.Identity.Channel));
        command.Parameters.AddWithValue("$seq", proposed.Identity.Sequence);
        command.Parameters.AddWithValue("$sha", proposed.Feed.Sha256);
        command.Parameters.AddWithValue("$key", proposed.KeyId);
        command.Parameters.AddWithValue("$size", proposed.Feed.SizeBytes);
        command.Parameters.AddWithValue("$sig", proposed.Signature.Sha256);
        command.Parameters.AddWithValue("$sigsize", proposed.Signature.SizeBytes);
        command.Parameters.AddWithValue("$operation", operationId.ToString("D"));
        command.ExecuteNonQuery();
        command.CommandText = "INSERT INTO trusted_update_feed_heads(format,channel,sequence,feed_sha256) " +
            "VALUES('nexus.update.feed',$channel,$seq,$sha) ON CONFLICT(format,channel) " +
            "DO UPDATE SET sequence=excluded.sequence,feed_sha256=excluded.feed_sha256;";
        command.ExecuteNonQuery();
    }

    private static SavedUpdateFeed Read(SqliteDataReader row, HostUpdateChannel channel)
    {
        if (row.IsDBNull(0)) throw Invalid();
        var identity = new UpdateFeedIdentity(channel, row.GetInt64(0), row.GetString(1));
        UpdateFeedPolicy.Require(identity, 1, null);
        var result = new SavedUpdateFeed(identity, row.GetString(2), Plan(identity.Sha256, row.GetInt64(3)),
            Plan(row.GetString(4), row.GetInt64(5)));
        if (row.IsDBNull(6) || row.GetString(6) != RequestDigest(result) || row.GetString(7) != "maintenance.feed.accept" ||
            row.GetString(8) != "completed") throw Invalid();
        return result;
    }

    private static BlobReference Plan(string sha, long size)
    {
        if (sha is not { Length: 64 } || sha.Any(c => c is not (>= 'a' and <= 'f' or >= '0' and <= '9')) ||
            size is < 1 or > 2097152) throw Invalid();
        return new($"blobs/plans/{sha}.json", size, sha);
    }

    internal static string RequestDigest(SavedUpdateFeed value) => Convert.ToHexStringLower(SHA256.HashData(
        Encoding.UTF8.GetBytes(Channel(value.Identity.Channel) + "\n" + value.Feed.Sha256 + "\n" + value.Signature.Sha256)));
    internal static string Channel(HostUpdateChannel value) => value switch
    { HostUpdateChannel.Stable => "stable", HostUpdateChannel.Prerelease => "prerelease", _ => throw Invalid() };
    private static IOException Invalid() => new("update_feed_cache_unbound");
}
