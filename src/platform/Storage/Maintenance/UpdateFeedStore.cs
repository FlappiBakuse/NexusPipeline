using NexusPipeline.Core.Maintenance;
using NexusPipeline.Core.Operations;
using NexusPipeline.Platform.Serialization;
using NexusPipeline.Platform.Storage.Sqlite;

namespace NexusPipeline.Platform.Storage.Maintenance;

internal sealed class UpdateFeedStore
{
    private readonly RuntimeInstance _instance;
    internal RuntimeInstance Instance => _instance;
    internal string PayloadProfile => _verifier.PayloadProfile;
    private readonly StateDatabase _database;
    private readonly InstanceLease _lease;
    private readonly ResourceLeaseManager _resources;
    private readonly UpdateFeedVerifier _verifier;
    private readonly ImmutableBlobStore _blobs;
    private static readonly ResourceClaim[] WriteClaims = [new("maintenance:feed", ResourceClaimMode.Mutation), new("blobs:content", ResourceClaimMode.Reference)];
    private static readonly ResourceClaim[] ReadClaims = [new("maintenance:feed", ResourceClaimMode.Reference), new("blobs:content", ResourceClaimMode.Reference)];

    internal UpdateFeedStore(RuntimeInstance instance, StateDatabase database, InstanceLease lease,
        ResourceLeaseManager resources, ContractReader contracts, UpdateFeedVerifier verifier)
    {
        lease.VerifyHeld(instance);
        if (database.InstanceId != instance.InstanceId || !StringComparer.OrdinalIgnoreCase.Equals(database.RuntimeRoot, instance.RuntimeRoot)) throw Invalid();
        _instance = instance; _database = database; _lease = lease; _resources = resources; _verifier = verifier; _blobs = new(instance, contracts);
    }

    internal VerifiedUpdateFeed Accept(Guid operationId, string subject, VerifiedUpdateFeed input)
    {
        _lease.VerifyHeld(_instance);
        var verified = _verifier.Verify(input.FeedBytes(), input.SignatureBytes(), input.Feed.Identity.Channel);
        using var root = _resources.TryAcquire(operationId, WriteClaims).Lease ?? throw Invalid();
        using var borrow = root.Borrow(_resources, operationId, WriteClaims);
        return borrow.Use(() => _lease.PublishExecution(() =>
        {
            var saved = new SavedUpdateFeed(verified.Feed.Identity, verified.KeyId,
                _blobs.PersistPlan(verified.FeedBytes()), _blobs.PersistPlan(verified.SignatureBytes()));
            var identity = new OperationIdentity(operationId, subject, UpdateFeedRows.RequestDigest(saved), "maintenance.feed.accept");
            using var feed = _blobs.OpenVerified(saved.Feed);
            using var signature = _blobs.OpenVerified(saved.Signature);
            _database.Write((connection, transaction) =>
            {
                var prior = OperationRows.ReadExisting(connection, transaction, identity);
                _ = OperationRows.Accept(connection, transaction, identity, DateTimeOffset.UtcNow);
                using var command = connection.CreateCommand(); command.Transaction = transaction;
                command.CommandText = "UPDATE operations SET status='completed' WHERE operation_id=$operation;";
                command.Parameters.AddWithValue("$operation", operationId.ToString("D"));
                if (command.ExecuteNonQuery() != 1 || prior is { Status: not "completed" }) throw Invalid();
                UpdateFeedRows.Accept(connection, transaction, saved, _verifier.Floor(saved.Identity.Channel), operationId);
                return true;
            });
            return verified;
        }));
    }

    internal VerifiedUpdateFeed? ReadCached(HostUpdateChannel channel)
    {
        _lease.VerifyHeld(_instance); var operationId = Guid.NewGuid();
        using var root = _resources.TryAcquire(operationId, ReadClaims).Lease ?? throw Invalid();
        using var borrow = root.Borrow(_resources, operationId, ReadClaims);
        return borrow.Use(() =>
        {
            SavedUpdateFeed? saved;
            using (var connection = DatabaseConnections.Open(_database.DatabasePath, readOnly: true))
            using (var transaction = connection.BeginTransaction(deferred: true))
            { saved = UpdateFeedRows.ReadHead(connection, transaction, channel); transaction.Commit(); }
            if (saved is null) return null;
            var verified = _verifier.Verify(Read(saved.Feed), Read(saved.Signature), channel);
            if (verified.Feed.Identity != saved.Identity || verified.KeyId != saved.KeyId) throw Invalid();
            using var fresh = DatabaseConnections.Open(_database.DatabasePath, readOnly: true);
            using var tx = fresh.BeginTransaction(deferred: true);
            if (UpdateFeedRows.ReadHead(fresh, tx, channel) != saved) throw Invalid();
            tx.Commit(); return verified;
        });
    }

    internal (SavedUpdateFeed Saved, HostUpdateRelease Release, HostUpdateFeed Feed) RequireRelease(ResourceOperationLease root,
        UpdateFeedIdentity expected, string hostVersion)
    {
        using var borrow = BorrowCandidate(root, root.OperationId);
        return borrow.Use(() =>
        {
            var current = ReadCached(expected.Channel) ?? throw Invalid();
            if (current.Feed.Identity != expected) throw Invalid();
            var release = current.Feed.Releases.SingleOrDefault(item => item.HostVersion == hostVersion) ?? throw Invalid();
            if (!Version.TryParse(release.MinReaderVersion.Split('-')[0], out var minimum) || minimum > _verifier.ReaderVersion) throw Invalid();
            using var connection = DatabaseConnections.Open(_database.DatabasePath, readOnly: true);
            using var transaction = connection.BeginTransaction(deferred: true);
            var saved = UpdateFeedRows.ReadHead(connection, transaction, expected.Channel) ?? throw Invalid();
            if (saved.Identity != expected || saved.KeyId != current.KeyId) throw Invalid();
            transaction.Commit(); return (saved, release, current.Feed);
        });
    }

    internal ResourceOperationBorrow BorrowCandidate(ResourceOperationLease root, Guid operationId)
    {
        _lease.VerifyHeld(_instance);
        return root.Borrow(_resources, operationId, ReadClaims);
    }

    private byte[] Read(BlobReference reference)
    {
        using var input = _blobs.OpenVerified(reference);
        var bytes = new byte[checked((int)input.Length)]; input.ReadExactly(bytes); return bytes;
    }
    private static IOException Invalid() => new("update_feed_cache_unavailable");
}
