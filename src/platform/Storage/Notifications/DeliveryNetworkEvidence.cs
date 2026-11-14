using Microsoft.Data.Sqlite;
using NexusPipeline.Core.Secrets;
using NexusPipeline.Platform.Security;
using NexusPipeline.Platform.Storage.Sqlite;
using NexusPipeline.Platform.Storage.Secrets;

namespace NexusPipeline.Platform.Storage.Notifications;

internal static class DeliveryNetworkEvidence
{
    internal static void RequireShape(NotificationDeliveryNetwork network)
    {
        var reference = network.ProxyPassword;
        if (network.SettingsRevision is < 0 or > 9007199254740991 || reference is null ||
            reference.Revision is < 0 or > SecretFieldReference.MaximumRevision ||
            (reference.SecretId is null) != (network.ProxyPayload is null) ||
            reference.SecretId == Guid.Empty || reference.Revision == 0 && reference.SecretId is not null)
            throw Invalid();
        if (network.ProxyPayload is not { } payload) return;
        if (payload.SecretId != reference.SecretId || payload.SizeBytes is < 1 or > 1048576 ||
            payload.Scheme != UserSecretProtector.Scheme || payload.Digest is null || payload.Digest.Length != 64 ||
            payload.Digest.Any(c => c is not (>= 'a' and <= 'f' or >= '0' and <= '9')) ||
            string.IsNullOrEmpty(payload.OwnerSid) || payload.OwnerSid.Length > 184 ||
            !payload.OwnerSid.StartsWith("S-1-", StringComparison.Ordinal) || payload.OwnerSid.Contains('\0')) throw Invalid();
    }

    internal static NotificationDeliveryNetwork CaptureCurrent(SqliteConnection connection, SqliteTransaction transaction, Guid instanceId)
    {
        var settings = HostSettingsRows.ReadCurrent(connection, transaction);
        var reference = SecretFieldRows.ReadField(connection, transaction, SecretFieldKey.HostProxyPassword(instanceId));
        NexusPipeline.Platform.Storage.Secrets.SecretPayloadIdentity? payload = null;
        if (reference.SecretId is { } id)
        {
            using var command = NotificationDeliveryRows.Command(connection, transaction,
                "SELECT protection_scheme,owner_sid,digest,size_bytes,state FROM system.secret_payloads WHERE secret_id=$0;", id.ToString("D"));
            using var row = command.ExecuteReader();
            if (!row.Read() || row.GetString(4) != "committed") throw Invalid();
            payload = new(id, row.GetString(0), row.GetString(1), row.GetString(2), row.GetInt64(3));
        }
        var result = new NotificationDeliveryNetwork(settings.Revision, reference, payload);
        RequireCurrent(connection, transaction, instanceId, result);
        return result;
    }

    internal static void RequireCurrent(SqliteConnection connection, SqliteTransaction transaction, Guid instanceId,
        NotificationDeliveryNetwork network)
    {
        RequireHistorical(connection, transaction, instanceId, network);
        if (HostSettingsRows.ReadCurrent(connection, transaction).Revision != network.SettingsRevision) throw Invalid();
        SecretFieldRows.RequireReference(connection, transaction, SecretFieldKey.HostProxyPassword(instanceId), network.ProxyPassword);
        if (network.ProxyPayload is { } payload)
            NotificationDeliveryRows.RequirePayloadMetadata(connection, transaction, payload, allowRetired: false);
    }

    internal static void RequireHistorical(SqliteConnection connection, SqliteTransaction transaction, Guid instanceId,
        NotificationDeliveryNetwork network)
    {
        RequireShape(network);
        _ = HostSettingsRows.Read(connection, transaction, network.SettingsRevision);
        var reference = network.ProxyPassword;
        if (network.ProxyPayload is { } payload)
            NotificationDeliveryRows.RequirePayloadMetadata(connection, transaction, payload, allowRetired: true);
        if (reference.Revision == 0 && reference.SecretId is null) return;
        using var command = NotificationDeliveryRows.Command(connection, transaction,
            "SELECT m.operation_id,o.request_digest FROM system.secret_mutations m JOIN main.operations o ON o.operation_id=m.operation_id " +
            "WHERE o.operation_kind='secret.change' AND o.status='completed' AND o.subject_id=$0 " +
            "AND m.result_revision=$1 AND m.result_secret_id IS $2;",
            $"host/{instanceId:D}/proxy-password", reference.Revision,
            reference.SecretId is { } id ? id.ToString("D") : DBNull.Value);
        using var row = command.ExecuteReader(); var proven = false;
        while (row.Read())
        {
            var operation = Guid.ParseExact(row.GetString(0), "D");
            if (operation == Guid.Empty || operation.ToString("D") != row.GetString(0)) throw Invalid();
            foreach (var disposition in Enum.GetValues<SecretDisposition>())
            {
                var before = disposition == SecretDisposition.Keep ? reference.Revision : reference.Revision - 1;
                if (before < 0) continue;
                var identity = SecretFieldOperation.Identity(operation, SecretFieldKey.HostProxyPassword(instanceId), before, disposition);
                proven |= identity.RequestDigest == row.GetString(1);
            }
        }
        if (!proven) throw Invalid();
    }

    private static IOException Invalid() => new("notification_delivery_network_unbound");
}
