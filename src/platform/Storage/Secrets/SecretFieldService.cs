using System.Runtime.Versioning;
using NexusPipeline.Core.Configuration;
using NexusPipeline.Core.Maintenance;
using NexusPipeline.Core.Secrets;
using NexusPipeline.Platform.Serialization;
using NexusPipeline.Platform.Storage.Journals;
using NexusPipeline.Platform.Storage.Sqlite;

namespace NexusPipeline.Platform.Storage.Secrets;

[SupportedOSPlatform("windows")]
public sealed class SecretFieldService : ISecretFieldStore
{
    private readonly RuntimeInstance _instance;
    private readonly StateDatabase _database;
    private readonly InstanceLease _instanceLease;
    private readonly ResourceLeaseManager _resources;
    private readonly RecoveryIndex _index;
    private readonly ContractReader _contracts;
    private readonly SecretFieldStore _store;
    private ControlBearerVerifier? _controlBearer;

    public SecretFieldService(RuntimeInstance instance, StateDatabase database, InstanceLease instanceLease,
        ResourceLeaseManager resources, RecoveryIndex index, ContractReader contracts)
    {
        instanceLease.VerifyHeld(instance);
        _store = new(instance, database);
        _instance = instance; _database = database; _instanceLease = instanceLease;
        _resources = resources; _index = index; _contracts = contracts;
    }

    internal void RequireOwner(RuntimeInstance instance, StateDatabase database, InstanceLease lease, ResourceLeaseManager resources)
    {
        _instanceLease.VerifyHeld(_instance);
        if (_instance.InstanceId != instance.InstanceId || _database.DatabasePath != database.DatabasePath ||
            !ReferenceEquals(_instanceLease, lease) || !ReferenceEquals(_resources, resources))
            throw new IOException("secret_service_instance_unbound");
    }

    internal void BindControlBearer(RuntimeInstance instance, StateDatabase database, InstanceLease lease, ControlBearerVerifier verifier)
    {
        if (_instance.InstanceId != instance.InstanceId || _database.DatabasePath != database.DatabasePath ||
            !ReferenceEquals(_instanceLease, lease) || Interlocked.CompareExchange(ref _controlBearer, verifier, null) is not null)
            throw new IOException("control_credential_owner_unbound");
    }

    internal void ReleaseControlBearer(ControlBearerVerifier verifier) => Interlocked.CompareExchange(ref _controlBearer, null, verifier);

    internal ControlPublicationScope? BeginDocumentPublication(ResourceOperationLease root, string extensionId) =>
        Volatile.Read(ref _controlBearer)?.BeginPublication(root, _resources, extensionId);

    public ValueTask<SecretFieldStatus> GetStateAsync(SecretFieldKey key, CancellationToken cancellationToken)
    {
        _instanceLease.VerifyHeld(_instance);
        return _store.GetStateAsync(key, cancellationToken);
    }

    public NexusPipeline.Core.Operations.OperationSnapshot? ReadHostOperation(Guid operationId, string field)
    {
        if (field != "proxy-password") throw new ArgumentException("host_secret_field_invalid");
        var key = SecretFieldKey.HostProxyPassword(_instance.InstanceId);
        var subject = SecretFieldStore.Identity(operationId, key, 0, SecretDisposition.Clear).SubjectId;
        var result = new Operations.OperationQueryService(_instance, _instanceLease, _database).Read(operationId, subject);
        if (result is not null && result.Kind != "secret.change") throw new IOException("host_secret_operation_unbound");
        return result;
    }

    public ValueTask<SecretValue> ReadAsync(SecretFieldKey key, CancellationToken cancellationToken)
    {
        _instanceLease.VerifyHeld(_instance);
        return _store.ReadAsync(key, cancellationToken);
    }

    public SecretValue ReadLocalHost(string field)
    {
        if (field == "control-bearer")
            return (_controlBearer ?? throw new IOException("control_credential_owner_unbound")).ReadLocal();
        var key = field switch
        {
            "proxy-password" => SecretFieldKey.HostProxyPassword(_instance.InstanceId),
            _ => throw new ArgumentException("host_secret_field_invalid")
        };
        return _instanceLease.PublishExecution(() =>
        {
            _instanceLease.VerifyHeld(_instance);
            var status = _store.GetStateAsync(key, default).GetAwaiter().GetResult();
            var reference = new SecretFieldReference(status.Revision, status.SecretId);
            return _store.ReadHostProxy(reference);
        });
    }

    public PreparedProxyCredential PrepareProxyCredential(ResourceOperationLease root, Guid operationId)
    {
        ArgumentNullException.ThrowIfNull(root);
        _instanceLease.VerifyHeld(_instance);
        var borrow = root.Borrow(_resources, operationId, [new("secrets:fields", ResourceClaimMode.Reference)]);
        try
        {
            var key = SecretFieldKey.HostProxyPassword(_instance.InstanceId);
            var state = borrow.Use(() => _store.GetStateAsync(key, default).GetAwaiter().GetResult());
            if (state.IsConfigured && !state.IsReadable) throw new IOException("secret_unavailable");
            return new(_instance.InstanceId, operationId, new(state.Revision, state.SecretId), borrow, UseHostProxy);
        }
        catch { borrow.Dispose(); throw; }
    }

    private void UseHostProxy(SecretFieldReference expected, SecretValueReader reader)
    {
        _instanceLease.VerifyHeld(_instance);
        using var material = _store.ReadHostProxy(expected);
        material.Use(reader);
    }

    public async ValueTask<SecretFieldStatus> ApplyAsync(Guid operationId, SecretFieldKey key, long expectedRevision,
        SecretDisposition disposition, SecretValue? value, CancellationToken cancellationToken)
    {
        _instanceLease.VerifyHeld(_instance);
        ArgumentNullException.ThrowIfNull(key);
        key.RequireStandaloneMutation();
        using var reservation = await _store.ReserveOperationAsync(operationId, cancellationToken).ConfigureAwait(false);
        var replay = _store.Lookup(operationId, key, expectedRevision, disposition, value, cancellationToken);
        if (replay is not null) return replay;
        var admission = _resources.TryAcquire(operationId, [new("secrets:fields", ResourceClaimMode.Mutation)]);
        using var mutation = admission.Lease ?? throw new InvalidOperationException(admission.Denial == ResourceLeaseDenial.Conflict
            ? "secret_resource_conflict" : "secret_host_unavailable");
        using var credentialChange = key == SecretFieldKey.HostControlBearer(_instance.InstanceId)
            ? Volatile.Read(ref _controlBearer)?.BeginMutation() : null;
        try
        {
            var result = await _store.ApplyReservedAsync(reservation, operationId, key, expectedRevision, disposition, value, cancellationToken).ConfigureAwait(false);
            FinishOwnCleanup(operationId);
            return result;
        }
        finally { PublishRecovery(); }
    }

    private void FinishOwnCleanup(Guid operationId)
    {
        var journals = new RecoveryJournalRegistry(_database).ReadActiveSnapshot().Journals
            .Where(value => value.OperationId == operationId && value.Kind == JournalKind.FileCleanup).ToArray();
        if (journals.Length > 1) throw new IOException("secret_cleanup_inventory_unrecognized");
        foreach (var journal in journals)
        {
            var admission = _resources.TryAcquire(operationId, journal.ResourceKeys.Select(key => new ResourceClaim(key, ResourceClaimMode.Mutation)).ToArray());
            using var cleanup = admission.Lease;
            if (cleanup is null) continue;
            new OwnedCleanupExecutor(_instance, _contracts, _database, _index).Execute(journal.JournalId, _instanceLease, cleanup);
        }
    }

    private void PublishRecovery()
    {
        try
        {
            var evidence = new JournalRecoveryEvidence(_instance, new(_instance, _contracts), _database);
            new RecoveryCoordinator(new RecoveryJournalRegistry(_database), evidence, _index).VerifyAndPublish();
        }
        catch (Exception error) when (error is IOException or InvalidOperationException or Microsoft.Data.Sqlite.SqliteException)
        { _index.BlockUnknown("secret_cleanup_reconciliation_required"); }
    }
}
