using System.Runtime.Versioning;
using NexusPipeline.Core.Maintenance;
using NexusPipeline.Platform.Serialization;
using NexusPipeline.Platform.Storage.Journals;
using NexusPipeline.Platform.Storage.Sqlite;

namespace NexusPipeline.Platform.Storage.Maintenance;

[SupportedOSPlatform("windows")]
internal sealed class HostUpdateService : IAsyncDisposable
{
    private readonly RuntimeInstance _instance;
    private readonly StateDatabase _database;
    private readonly InstanceLease _lease;
    private readonly ResourceLeaseManager _resources;
    private readonly ContractReader _contracts;
    private readonly PayloadApplyRoots _roots;
    private readonly UpdateFeedStore? _feeds;
    private readonly UpdateFeedVerifier? _verifier;
    private readonly UpdateHttpTransport? _transport;
    private readonly Func<string?> _readSourceRoot;
    private readonly SemaphoreSlim _serial = new(1, 1);
    private readonly CancellationTokenSource _stopping = new();
    internal bool HasReleaseTrust => _feeds is not null;

    internal HostUpdateService(RuntimeInstance instance, StateDatabase database, InstanceLease lease,
        ResourceLeaseManager resources, ContractReader contracts, PayloadApplyRoots roots,
        UpdateReleaseTrust? trust, Func<HttpClient> createClient, Func<string?>? readSourceRoot = null)
    {
        lease.VerifyHeld(instance);
        if (database.InstanceId != instance.InstanceId || database.RuntimeRoot != instance.RuntimeRoot)
            throw new IOException("maintenance_service_unbound");
        _readSourceRoot = readSourceRoot ?? (() => null);
        _instance = instance; _database = database; _lease = lease; _resources = resources; _contracts = contracts; _roots = roots;
        if (trust is not null)
        {
            _verifier = new(contracts, trust.Policy);
            _feeds = new(instance, database, lease, resources, contracts, _verifier);
            _transport = new(trust.Policy, createClient);
        }
    }

    internal HostUpdateFeed? ReadCached(HostUpdateChannel channel)
    { RequireAvailable(); return _feeds!.ReadCached(channel)?.Feed; }

    internal async Task<HostUpdateFeed> RefreshAsync(string subject, HostUpdateChannel channel, CancellationToken cancellation)
    {
        RequireAvailable();
        if (subject is not { Length: >= 1 and <= 256 } || subject.Any(char.IsControl) || !Enum.IsDefined(channel))
            throw new ArgumentException("maintenance_feed_request_invalid");
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellation, _stopping.Token);
        await _serial.WaitAsync(linked.Token);
        try
        {
            RequireAvailable();
            var feed = await _transport!.Feed(channel, _verifier!, linked.Token, _readSourceRoot());
            linked.Token.ThrowIfCancellationRequested();
            return _feeds!.Accept(Guid.NewGuid(), subject, feed).Feed;
        }
        finally { _serial.Release(); }
    }

    internal async Task<HostUpdateOperation> PrepareAsync(HostUpdateRequest request, CancellationToken cancellation)
    {
        RequireAvailable();
        var identity = UpdateRequestIdentity.Create(request);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellation, _stopping.Token);
        await _serial.WaitAsync(linked.Token);
        try
        {
            RequireAvailable();
            var pending = new MaintenancePendingStore(_instance, _database, _lease, _resources, _contracts, _roots, _verifier);
            using (var connection = DatabaseConnections.Open(_database.DatabasePath, readOnly: true))
            using (var transaction = connection.BeginTransaction(deferred: true))
            {
                if (OperationRows.ReadExisting(connection, transaction, identity) is not null)
                {
                    var row = MaintenanceOperationRows.ReadOwned(connection, transaction, request.OperationId, request.Subject)
                        ?? throw new IOException("maintenance_operation_unbound");
                    var result = pending.Snapshot(row); transaction.Commit(); return result;
                }
                transaction.Commit();
            }
            using var root = _resources.TryAcquire(request.OperationId, PreparedHostUpdate.PreparationClaims).Lease
                ?? throw new IOException("maintenance_preparation_unavailable");
            var accepted = _feeds!.RequireRelease(root, request.Feed, request.HostVersion);
            using var download = await OwnedUpdateDownload.Receive(_instance, _lease, _resources, request.OperationId,
                accepted.Release.Artifact, (destination, token) => _transport!.Artifact(accepted.Release.Artifact, destination, token), linked.Token);
            using var candidate = VerifiedUpdateCandidate.Prepare(_instance, _feeds, root, request.Feed,
                request.HostVersion, _contracts, linked.Token);
            using var prepared = PreparedHostUpdate.Prepare(_instance, _lease, _database, _resources, root, request,
                candidate, _roots, _contracts, linked.Token);
            linked.Token.ThrowIfCancellationRequested();
            return pending.Accept(prepared, DateTimeOffset.UtcNow);
        }
        finally { _serial.Release(); }
    }

    private void RequireAvailable()
    {
        _lease.VerifyHeld(_instance);
        if (_stopping.IsCancellationRequested) throw new IOException("maintenance_service_stopping");
        if (_feeds is null) throw new IOException("maintenance_release_trust_unavailable");
    }

    public async ValueTask DisposeAsync()
    {
        _stopping.Cancel();
        if (!await _serial.WaitAsync(TimeSpan.FromSeconds(30))) throw new IOException("maintenance_transfer_stop_pending");
        _serial.Release(); _serial.Dispose(); _stopping.Dispose();
    }
}
