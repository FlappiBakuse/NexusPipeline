using System.Collections.Immutable;
using System.Runtime.Versioning;
using NexusPipeline.Core.Maintenance;
using NexusPipeline.Core.Operations;
using NexusPipeline.Core.Secrets;
using NexusPipeline.Platform.Storage.Notifications;
using NexusPipeline.Platform.Storage.Sqlite;

namespace NexusPipeline.Platform.Storage.Secrets;

internal sealed record NotificationSecretRequest(NotificationSecretField Field, SecretDisposition Disposition,
    SecretFieldReference Before, string? ReplayProof);

[SupportedOSPlatform("windows")]
internal sealed class PreparedSecretBatch : IDisposable
{
    private readonly Guid _instanceId;
    private readonly Guid _taskId;
    private readonly OperationIdentity _parent;
    private readonly ImmutableArray<NotificationSecretRequest> _fields;
    private readonly ResourceOperationBorrow _borrow;

    private PreparedSecretBatch(Guid instanceId, Guid taskId, OperationIdentity parent,
        ImmutableArray<NotificationSecretRequest> fields, ResourceOperationBorrow borrow)
    {
        _instanceId = instanceId; _taskId = taskId; _parent = parent; _fields = fields; _borrow = borrow;
    }

    internal static PreparedSecretBatch Acquire(RuntimeInstance instance, ResourceOperationLease root,
        ResourceLeaseManager manager, OperationIdentity parent, Guid taskId, ImmutableArray<NotificationSecretRequest> fields)
    {
        if (taskId == Guid.Empty || fields.IsDefault ||
            !fields.Select(item => item.Field).SequenceEqual(Enum.GetValues<NotificationSecretField>()) ||
            parent.Kind != "notification.task.save" || parent.OperationId == Guid.Empty)
            throw new IOException("notification_secret_batch_unbound");
        var borrow = root.Borrow(manager, parent.OperationId,
            [new("notifications:catalog", ResourceClaimMode.Mutation), new("secrets:fields", ResourceClaimMode.Mutation)]);
        return new(instance.InstanceId, taskId, parent, fields, borrow);
    }

    internal T Use<T>(RuntimeInstance instance, NotificationSecretField field, Func<OperationIdentity, SecretFieldKey, long, T> action)
    {
        if (instance.InstanceId != _instanceId) throw new IOException("notification_secret_batch_instance_unbound");
        var request = _fields.Single(item => item.Field == field);
        if (request.Disposition != SecretDisposition.Set) throw new IOException("notification_secret_batch_field_unbound");
        var key = SecretFieldKey.NotificationTask(_instanceId, _taskId, field);
        var child = PersistentIdentity(field, key, request.Before.Revision);
        return _borrow.Use(() => action(child, key, request.Before.Revision));
    }

    internal (Guid ParentOperationId, string ParentRequestDigest) Binding => (_parent.OperationId, _parent.RequestDigest);

    internal void RequirePrepared(NotificationSecretField field, FrozenSecretPreparation frozen)
    {
        var request = _fields.Single(item => item.Field == field);
        var plan = frozen.ReadWire();
        if (plan.BeforeSecretId != request.Before.SecretId?.ToString("D") ||
            !SecretReplayProof.Matches(plan.ReplayProof, request.ReplayProof) ||
            plan.NotificationPublication is not { } binding || binding.ParentOperationId != _parent.OperationId.ToString("D") ||
            binding.ParentRequestDigest != _parent.RequestDigest)
            throw new IOException("notification_secret_batch_candidate_unbound");
    }

    private OperationIdentity PersistentIdentity(NotificationSecretField field, SecretFieldKey key, long revision) =>
        SecretFieldStore.Identity(NotificationPublicationCodec.ChildOperation(_parent.OperationId, field), key, revision, SecretDisposition.Set);

    public void Dispose() => _borrow.Dispose();
}
