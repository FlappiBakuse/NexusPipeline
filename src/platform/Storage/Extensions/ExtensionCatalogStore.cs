using NexusPipeline.Core.Configuration;
using NexusPipeline.Core.Extensions;
using NexusPipeline.Core.Maintenance;
using NexusPipeline.Core.Operations;
using NexusPipeline.Platform.Extensions;
using NexusPipeline.Platform.Serialization;
using NexusPipeline.Platform.Storage.Sqlite;

namespace NexusPipeline.Platform.Storage.Extensions;

internal sealed class ExtensionCatalogStore
{
    private readonly RuntimeInstance _instance;
    internal RuntimeInstance Instance => _instance;
    private readonly StateDatabase _database;
    private readonly InstanceLease _lease;
    private readonly ResourceLeaseManager _resources;
    private readonly ExtensionCatalogVerifier _verifier;
    private readonly ImmutableBlobStore _blobs;
    private static readonly ResourceClaim[] WriteClaims = [new("extensions:catalog", ResourceClaimMode.Mutation),
        new("blobs:content", ResourceClaimMode.Reference)];
    private static readonly ResourceClaim[] ReadClaims = [new("extensions:catalog", ResourceClaimMode.Reference),
        new("blobs:content", ResourceClaimMode.Reference)];

    internal ExtensionCatalogStore(RuntimeInstance instance, StateDatabase database, InstanceLease lease,
        ResourceLeaseManager resources, ContractReader contracts, ExtensionCatalogVerifier verifier)
    {
        lease.VerifyHeld(instance);
        if (database.InstanceId != instance.InstanceId ||
            !StringComparer.OrdinalIgnoreCase.Equals(database.RuntimeRoot, instance.RuntimeRoot)) throw Invalid();
        _instance = instance; _database = database; _lease = lease; _resources = resources; _verifier = verifier;
        _blobs = new(instance, contracts);
    }

    internal VerifiedExtensionCatalog Accept(Guid operationId, string subject, VerifiedExtensionCatalog input)
    {
        ArgumentNullException.ThrowIfNull(input);
        _lease.VerifyHeld(_instance);
        var verified = _verifier.Verify(input.PointerBytes(), input.CatalogBytes(), input.SignatureBytes(), input.Identity.Channel);
        using var root = _resources.TryAcquire(operationId, WriteClaims).Lease ?? throw Invalid();
        using var borrow = root.Borrow(_resources, operationId, WriteClaims);
        return borrow.Use(() => _lease.PublishExecution(() =>
        {
            var saved = new ExtensionCatalogSaved(verified.Identity, verified.KeyId,
                _blobs.PersistPlan(verified.PointerBytes()), _blobs.PersistPlan(verified.CatalogBytes()),
                _blobs.PersistPlan(verified.SignatureBytes()));
            var identity = new OperationIdentity(operationId, subject, ExtensionCatalogRows.RequestDigest(saved), "extension.catalog.accept");
            using var pointer = _blobs.OpenVerified(saved.Pointer);
            using var catalog = _blobs.OpenVerified(saved.Catalog);
            using var signature = _blobs.OpenVerified(saved.Signature);
            _database.Write((connection, transaction) =>
            {
                var prior = OperationRows.ReadExisting(connection, transaction, identity);
                _ = OperationRows.Accept(connection, transaction, identity, DateTimeOffset.UtcNow);
                using var command = connection.CreateCommand(); command.Transaction = transaction;
                command.CommandText = "UPDATE main.operations SET status='completed' WHERE operation_id=$operation;";
                command.Parameters.AddWithValue("$operation", operationId.ToString("D"));
                if (command.ExecuteNonQuery() != 1 || prior is { Status: not "completed" }) throw Invalid();
                ExtensionCatalogRows.AcceptInside(connection, transaction, saved, _verifier.Floor(saved.Identity.Channel), operationId);
                return true;
            });
            return verified;
        }));
    }

    internal VerifiedExtensionCatalog? ReadCached(ExtensionStoreChannel channel)
    {
        _lease.VerifyHeld(_instance);
        var operationId = Guid.NewGuid();
        using var root = _resources.TryAcquire(operationId, ReadClaims).Lease ?? throw Invalid();
        using var borrow = root.Borrow(_resources, operationId, ReadClaims);
        return borrow.Use(() =>
        {
            ExtensionCatalogSaved? saved;
            using (var connection = DatabaseConnections.Open(_database.DatabasePath, readOnly: true, domains: DatabaseDomain.Runtime | DatabaseDomain.Extensions))
            using (var transaction = connection.BeginTransaction(deferred: true))
            { saved = ExtensionCatalogRows.ReadHead(connection, transaction, channel); transaction.Commit(); }
            if (saved is null) return null;
            var verified = _verifier.Verify(Read(saved.Pointer), Read(saved.Catalog), Read(saved.Signature), channel);
            if (verified.Identity != saved.Identity || verified.KeyId != saved.KeyId) throw Invalid();
            using var fresh = DatabaseConnections.Open(_database.DatabasePath, readOnly: true, domains: DatabaseDomain.Runtime | DatabaseDomain.Extensions);
            using var tx = fresh.BeginTransaction(deferred: true);
            if (ExtensionCatalogRows.ReadHead(fresh, tx, channel) != saved) throw Invalid();
            tx.Commit(); return verified;
        });
    }

    internal InstalledHistory ProtectInstalledHistory(ResourceOperationLease root, ExtensionCatalogSaved original, ExtensionCatalogEntry entry)
    {
        using var timing = StartupProfile.Measure("catalog.verify");
        _lease.VerifyHeld(_instance);
        var borrow = root.Borrow(_resources, root.OperationId, ReadClaims);
        var held = new List<IDisposable>();
        try
        {
            return borrow.Use(() =>
            {
                ExtensionCatalogSaved current;
                using (var connection = DatabaseConnections.Open(_database.DatabasePath, readOnly: true, domains: DatabaseDomain.Runtime | DatabaseDomain.Extensions))
                using (var transaction = connection.BeginTransaction(deferred: true))
                {
                    ExtensionCatalogRows.RequireAcceptedInside(connection, transaction, original);
                    current = ExtensionCatalogRows.ReadHead(connection, transaction, original.Identity.Channel) ?? throw Invalid();
                    transaction.Commit();
                }
                held.Add(StableDirectoryLease.Acquire(Path.Combine(_instance.RuntimeRoot, "blobs", "plans")));
                var bytes = new Dictionary<BlobReference, byte[]>();
                foreach (var reference in new[] { original.Pointer, original.Catalog, original.Signature,
                    current.Pointer, current.Catalog, current.Signature }.Distinct())
                {
                    var input = _blobs.OpenVerified(reference); held.Add(input);
                    var value = new byte[checked((int)input.Length)]; input.ReadExactly(value); bytes.Add(reference, value);
                }
                _verifier.RequireRecorded(bytes[original.Pointer], bytes[original.Catalog], bytes[original.Signature],
                    original.Identity, original.KeyId, entry);
                if (current != original)
                    _verifier.RequireRecorded(bytes[current.Pointer], bytes[current.Catalog], bytes[current.Signature],
                        current.Identity, current.KeyId, null);
                if (current.Identity.Sequence < original.Identity.Sequence) throw Invalid();
                var sql = new DatabaseValidationLease(_database, "catalog.sql.validate", DatabaseDomain.Extensions);
                held.Add(sql);
                var result = new InstalledHistory(this, borrow, original, current, held, sql);
                result.RequireCurrent(); return result;
            });
        }
        catch
        {
            for (var index = held.Count - 1; index >= 0; index--) held[index].Dispose();
            borrow.Dispose(); throw;
        }
    }

    internal sealed class InstalledHistory(ExtensionCatalogStore owner, ResourceOperationBorrow borrow,
        ExtensionCatalogSaved original, ExtensionCatalogSaved current, IReadOnlyList<IDisposable> held,
        DatabaseValidationLease sql) : IDisposable
    {
        private int _disposed;

        internal void RequireCurrent()
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
            owner._lease.VerifyHeld(owner._instance);
            borrow.Use(() =>
            {
                sql.RequireCurrent((connection, transaction) =>
                {
                    ExtensionCatalogRows.RequireAcceptedInside(connection, transaction, original);
                    if (ExtensionCatalogRows.ReadHead(connection, transaction, original.Identity.Channel) != current) throw Invalid();
                });
                return true;
            });
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            try { for (var index = held.Count - 1; index >= 0; index--) held[index].Dispose(); }
            finally { borrow.Dispose(); }
        }
    }

    internal ResourceOperationBorrow BorrowPackage(ResourceOperationLease root, Guid operationId)
    {
        _lease.VerifyHeld(_instance);
        return root.Borrow(_resources, operationId, ReadClaims);
    }

    private byte[] Read(BlobReference reference)
    {
        using var input = _blobs.OpenVerified(reference);
        var bytes = new byte[checked((int)input.Length)]; input.ReadExactly(bytes); return bytes;
    }
    private static IOException Invalid() => new("extension_catalog_cache_unavailable");
}
