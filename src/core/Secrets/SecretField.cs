using System.Text.Json.Serialization;
using NexusPipeline.Core.Extensions;

namespace NexusPipeline.Core.Secrets;

public enum SecretAccessKind { Status, Read, Write }
public enum SecretDisposition { Keep, Set, Clear }
public enum FieldOwnerKind { Unknown, Extension, Host, NotificationTask }
[Flags]
public enum SecretSubjectScopes { None = 0, OwnExtension = 1, SelectedBinding = 2 }

public sealed record SecretFieldKey
{
    public FieldOwnerKind OwnerKind { get; }
    public string? ExtensionId { get; }
    public Guid SubjectId { get; }
    public Guid? OwnerInstanceId { get; }
    public string Field { get; }
    public SecretFieldKey(string extensionId, Guid subjectId, string field)
        : this(FieldOwnerKind.Extension, extensionId, subjectId, field) { }

    [JsonConstructor]
    public SecretFieldKey(FieldOwnerKind ownerKind, string? extensionId, Guid subjectId, string field, Guid? ownerInstanceId = null)
    {
        if (ownerKind == FieldOwnerKind.Extension)
        {
            ExtensionIdentifiers.Require(extensionId!);
            if (ownerInstanceId is not null) throw new ArgumentException("secret_field_owner_invalid");
        }
        else if (ownerKind == FieldOwnerKind.Host)
        {
            if (extensionId is not null || field is not ("proxy-password" or "control-bearer") || ownerInstanceId is not null)
                throw new ArgumentException("secret_field_owner_invalid");
        }
        else if (ownerKind == FieldOwnerKind.NotificationTask)
        {
            if (extensionId is not null || ownerInstanceId is null || ownerInstanceId == Guid.Empty)
                throw new ArgumentException("secret_field_owner_invalid");
            _ = NotificationSecretFields.Parse(field);
        }
        else throw new ArgumentException("secret_field_owner_invalid");
        if (subjectId == Guid.Empty ||
            string.IsNullOrWhiteSpace(field) || field.Length > 80 ||
            field.Any(character => character is not (>= 'a' and <= 'z' or >= '0' and <= '9' or '-')))
            throw new ArgumentException("secret_field_identity_invalid");
        OwnerKind = ownerKind;
        ExtensionId = extensionId;
        SubjectId = subjectId;
        OwnerInstanceId = ownerInstanceId;
        Field = field;
    }

    public static SecretFieldKey HostProxyPassword(Guid instanceId) =>
        new(FieldOwnerKind.Host, null, instanceId, "proxy-password");

    public static SecretFieldKey HostControlBearer(Guid instanceId) =>
        new(FieldOwnerKind.Host, null, instanceId, "control-bearer");

    public static SecretFieldKey NotificationTask(Guid instanceId, Guid taskId, NotificationSecretField field) =>
        new(FieldOwnerKind.NotificationTask, null, taskId, NotificationSecretFields.Name(field), instanceId);

    public void RequireStandaloneMutation()
    {
        if (OwnerKind == FieldOwnerKind.NotificationTask)
            throw new UnauthorizedAccessException("notification_secret_publication_required");
    }

    public void RequireGenericRead()
    {
        if (OwnerKind == FieldOwnerKind.Host) throw new UnauthorizedAccessException("host_secret_reference_required");
        if (OwnerKind == FieldOwnerKind.NotificationTask)
            throw new UnauthorizedAccessException("notification_secret_reference_required");
    }

    public void RequireExtensionOwner(string extensionId)
    {
        ExtensionIdentifiers.Require(extensionId);
        if (OwnerKind != FieldOwnerKind.Extension || ExtensionId != extensionId)
            throw new UnauthorizedAccessException("secret_extension_owner_unbound");
    }
}

public sealed record SecretFieldStatus(long Revision, Guid? SecretId, bool IsConfigured, bool IsReadable);

public interface ISecretAccessPolicy
{
    void AssertAllowed(SecretFieldKey key, SecretAccessKind access, SecretSubjectScopes declaredScopes);
}

public interface ISecretFieldStore
{
    ValueTask<SecretFieldStatus> GetStateAsync(SecretFieldKey key, CancellationToken cancellationToken);
    ValueTask<SecretFieldStatus> ApplyAsync(Guid operationId, SecretFieldKey key, long expectedRevision,
        SecretDisposition disposition, SecretValue? value, CancellationToken cancellationToken);
    ValueTask<SecretValue> ReadAsync(SecretFieldKey key, CancellationToken cancellationToken);
}
