using System.Security.Cryptography;
using System.Text.Json;
using NexusPipeline.Core.Operations;
using NexusPipeline.Core.Secrets;

namespace NexusPipeline.Platform.Storage.Secrets;

internal static class SecretFieldOperation
{
    internal static OperationIdentity Identity(Guid id, SecretFieldKey key, long revision, SecretDisposition disposition)
    {
        ArgumentNullException.ThrowIfNull(key);
        var owner = key.OwnerKind switch
        {
            FieldOwnerKind.Host => "host",
            FieldOwnerKind.Extension => "extension/" + key.ExtensionId,
            FieldOwnerKind.NotificationTask => "notificationTask/" + key.OwnerInstanceId!.Value.ToString("D"),
            _ => throw new ArgumentException("secret_field_owner_invalid")
        };
        var subject = $"{owner}/{key.SubjectId:D}/{key.Field}";
        var bytes = JsonSerializer.SerializeToUtf8Bytes(new { subject, revision, disposition = (int)disposition });
        return new(id, subject, Convert.ToHexStringLower(SHA256.HashData(bytes)), "secret.change");
    }

}
