using System.Runtime.Versioning;
using NexusPipeline.Core.Maintenance;
using NexusPipeline.Core.Configuration;
using NexusPipeline.Core.Secrets;
using NexusPipeline.Platform.Security;
using NexusPipeline.Platform.Serialization;
using NexusPipeline.Platform.Storage.Notifications;
using NexusPipeline.Platform.Storage.Sqlite;

namespace NexusPipeline.Platform.Storage.Secrets;

[SupportedOSPlatform("windows")]
internal sealed class PreparedDeliverySecrets : IDisposable
{
    private readonly RuntimeInstance _instance;
    private readonly StateDatabase _database;
    private readonly InstanceLease _lease;
    private readonly ContractReader _contracts;
    private readonly ResourceOperationBorrow _borrow;
    private readonly NotificationDeliverySnapshot _snapshot;
    private readonly Guid _generation;
    private readonly List<IDisposable> _held;

    private PreparedDeliverySecrets(RuntimeInstance instance, StateDatabase database, InstanceLease lease,
        ContractReader contracts, ResourceOperationBorrow borrow, NotificationDeliverySnapshot snapshot,
        Guid generation, List<IDisposable> held)
    {
        _instance = instance; _database = database; _lease = lease; _contracts = contracts;
        _borrow = borrow; _snapshot = snapshot; _generation = generation; _held = held;
    }

    internal static PreparedDeliverySecrets Acquire(RuntimeInstance instance, StateDatabase database,
        InstanceLease lease, ContractReader contracts, ResourceOperationLease senderRoot, ResourceLeaseManager manager,
        BlobReference reference)
    {
        lease.VerifyHeld(instance);
        if (database.InstanceId != instance.InstanceId || database.RuntimeRoot != instance.RuntimeRoot ||
            !StringComparer.OrdinalIgnoreCase.Equals(database.DatabasePath, Path.Combine(instance.RuntimeRoot, "state", "nexus.db")))
            throw new IOException("notification_delivery_instance_unbound");
        var snapshot = new NotificationDeliveryEvidence(instance, database, contracts).Read(reference);
        if (NotificationDeliveryOrigins.Reference(snapshot.Document) is null) throw new IOException("notification_delivery_registration_required");
        var borrow = senderRoot.Borrow(manager, snapshot.Document.OperationId,
            [new("notifications:catalog", ResourceClaimMode.Reference), new("secrets:fields", ResourceClaimMode.Reference)]);
        var held = new List<IDisposable>();
        try
        {
            var result = new PreparedDeliverySecrets(instance, database, lease, contracts, borrow,
                snapshot, lease.StartupGeneration, held);
            result.RequireSending();
            var files = new SecretPayloadFiles(instance, new UserSecretProtector());
            foreach (var payload in snapshot.Document.Secrets.Select(item => item.Payload).OfType<SecretPayloadIdentity>())
                held.Add(files.HoldVerified(payload));
            return result;
        }
        catch { foreach (var item in held) item.Dispose(); borrow.Dispose(); throw; }
    }

    internal bool IsConfigured(NotificationSecretField field)
    {
        RequireSending();
        return _snapshot.Document.Secrets.Single(item => item.Field == field).Payload is not null;
    }

    internal void Use(NotificationSecretField field, SecretValueReader reader)
    {
        ArgumentNullException.ThrowIfNull(reader);
        _borrow.Use(() =>
        {
            RequireSending();
            var payload = _snapshot.Document.Secrets.Single(item => item.Field == field).Payload ??
                throw new IOException("notification_secret_not_configured");
            using var value = new SecretPayloadFiles(_instance, new UserSecretProtector()).Read(payload);
            value.Use(reader);
            return true;
        });
    }

    private void RequireSending()
    {
        _lease.VerifyHeld(_instance);
        _borrow.VerifyHeld();
        _ = new NotificationDeliveryEvidence(_instance, _database, _contracts).Read(_snapshot.Reference);
        using var connection = DatabaseConnections.Open(_database.DatabasePath, readOnly: true);
        var state = NotificationDeliveryRows.ReadState(connection, null, _snapshot.Document.OperationId);
        if (state.State != NotificationDeliveryState.Sending || state.OwnerGeneration != _generation ||
            state.Sender != NotificationDeliveryOwner.Capture(JournalWriterStore.CurrentWriter))
            throw new IOException("notification_delivery_sender_unbound");
    }

    public void Dispose()
    {
        foreach (var item in _held) item.Dispose();
        _held.Clear();
        _borrow.Dispose();
    }
}
