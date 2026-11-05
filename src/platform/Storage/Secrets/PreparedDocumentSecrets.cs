using System.Collections.Immutable;
using System.Runtime.Versioning;
using NexusPipeline.Core.Maintenance;
using NexusPipeline.Core.Operations;
using NexusPipeline.Core.Secrets;
using NexusPipeline.Platform.Storage.Sqlite;

namespace NexusPipeline.Platform.Storage.Secrets;

[SupportedOSPlatform("windows")]
internal sealed class PreparedDocumentSecrets : IDisposable
{
    private readonly Guid _instanceId;
    private readonly string _extensionId;
    private readonly OperationIdentity _parent;
    private readonly ImmutableArray<SecretDocumentMutation> _fields;
    private readonly ResourceOperationBorrow _borrow;

    internal PreparedDocumentSecrets(RuntimeInstance instance, string extensionId, OperationIdentity parent,
        ImmutableArray<SecretDocumentMutation> fields, ResourceOperationLease root, ResourceLeaseManager resources)
    {
        if (parent.OperationId == Guid.Empty || parent.Kind != "extension.document.write" || fields.IsDefault)
            throw SecretDocumentFormat.Invalid();
        _instanceId = instance.InstanceId; _extensionId = extensionId; _parent = parent; _fields = fields;
        _borrow = root.Borrow(resources, parent.OperationId,
            [new("extension-data:" + extensionId, ResourceClaimMode.Mutation), new("secrets:fields", ResourceClaimMode.Mutation)]);
    }

    internal (Guid ParentOperationId, string ParentRequestDigest) Binding => (_parent.OperationId, _parent.RequestDigest);

    internal T Use<T>(RuntimeInstance instance, Guid subjectId, string field, Func<OperationIdentity, SecretFieldKey, long, T> action)
    {
        if (instance.InstanceId != _instanceId) throw SecretDocumentFormat.Invalid();
        var change = _fields.Single(value => value.SubjectId == subjectId && value.Field == field);
        if (change.Disposition != SecretDisposition.Set) throw SecretDocumentFormat.Invalid();
        var key = new SecretFieldKey(_extensionId, subjectId, field);
        return _borrow.Use(() => action(SecretFieldStore.Identity(change.OperationId, key, change.Before.Revision, SecretDisposition.Set), key, change.Before.Revision));
    }

    public void Dispose() => _borrow.Dispose();
}
