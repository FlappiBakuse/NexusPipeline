using System.ComponentModel;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using NexusPipeline.Core.Operations;
using NexusPipeline.Core.Secrets;
using NexusPipeline.Platform.Security;
using NexusPipeline.Platform.Storage.Secrets;

namespace NexusPipeline.Platform.Storage.Sqlite;

[SupportedOSPlatform("windows")]
public sealed class SecretFieldStore : ISecretFieldStore
{
    private readonly StateDatabase _database;
    private readonly RuntimeInstance _instance;
    private readonly UserSecretProtector _protector = new();
    private readonly SecretPayloadFiles _files;
    private readonly SecretOperationFlights _flights = new();
    private readonly ISecretPreparationObserver? _observer;

    public SecretFieldStore(RuntimeInstance instance, StateDatabase database, ISecretPreparationObserver? observer = null)
    {
        if (instance.InstanceId != database.InstanceId || !string.Equals(database.DatabasePath,
            Path.Combine(instance.RuntimeRoot, "state", "nexus.db"), StringComparison.OrdinalIgnoreCase))
            throw new IOException("secret_store_instance_unbound");
        _instance = instance;
        _database = database;
        _files = new(instance, _protector);
        _observer = observer;
    }

    public ValueTask<SecretFieldStatus> GetStateAsync(SecretFieldKey key, CancellationToken cancellationToken)
    {
        RequireInstanceKey(key);
        cancellationToken.ThrowIfCancellationRequested();
        using var connection = DatabaseConnections.Open(_database.DatabasePath, readOnly: true);
        return ValueTask.FromResult(Status(SecretFieldRows.ReadField(connection, null, key)));
    }

    public ValueTask<SecretValue> ReadAsync(SecretFieldKey key, CancellationToken cancellationToken)
    {
        RequireInstanceKey(key);
        key.RequireGenericRead();
        cancellationToken.ThrowIfCancellationRequested();
        using var connection = DatabaseConnections.Open(_database.DatabasePath, readOnly: true);
        var state = SecretFieldRows.ReadField(connection, null, key);
        if (state.SecretId is not { } id) throw new IOException("secret_not_configured");
        var value = _files.Read(ReadPayload(connection, null, id));
        if (cancellationToken.IsCancellationRequested) { value.Dispose(); cancellationToken.ThrowIfCancellationRequested(); }
        return ValueTask.FromResult(value);
    }

    internal SecretValue ReadHostProxy(SecretFieldReference expected)
    {
        ArgumentNullException.ThrowIfNull(expected);
        var key = SecretFieldKey.HostProxyPassword(_instance.InstanceId);
        using var connection = DatabaseConnections.Open(_database.DatabasePath, readOnly: true);
        SecretFieldRows.RequireReference(connection, null, key, expected);
        if (expected.SecretId is not { } id) throw new IOException("secret_not_configured");
        return _files.Read(ReadPayload(connection, null, id));
    }

    internal SecretValue ReadProjectInput(SecretFieldKey key, SecretFieldReference expected)
    {
        RequireInstanceKey(key); key.RequireExtensionOwner(key.ExtensionId!);
        using var connection = DatabaseConnections.Open(_database.DatabasePath, readOnly: true);
        SecretFieldRows.RequireReference(connection, null, key, expected);
        if (expected.SecretId is not { } id) throw new IOException("secret_not_configured");
        return _files.Read(ReadPayload(connection, null, id));
    }

    internal SecretValue ReadControlBearer(SecretFieldReference expected)
    {
        ArgumentNullException.ThrowIfNull(expected);
        var key = SecretFieldKey.HostControlBearer(_instance.InstanceId);
        using var connection = DatabaseConnections.Open(_database.DatabasePath, readOnly: true);
        SecretFieldRows.RequireReference(connection, null, key, expected);
        if (expected.SecretId is not { } id) throw new IOException("secret_not_configured");
        return _files.Read(ReadPayload(connection, null, id));
    }

    internal SecretPayloadLease HoldControlBearer(SecretFieldReference expected)
    {
        var key = SecretFieldKey.HostControlBearer(_instance.InstanceId);
        using var connection = DatabaseConnections.Open(_database.DatabasePath, readOnly: true);
        SecretFieldRows.RequireReference(connection, null, key, expected);
        if (expected.SecretId is not { } id) throw new IOException("secret_not_configured");
        return _files.HoldVerified(ReadPayload(connection, null, id), trackPath: true);
    }

    public async ValueTask<SecretFieldStatus> ApplyAsync(Guid operationId, SecretFieldKey key, long expectedRevision,
        SecretDisposition disposition, SecretValue? value, CancellationToken cancellationToken)
    {
        using var flight = await ReserveOperationAsync(operationId, cancellationToken).ConfigureAwait(false);
        return await ApplyReservedAsync(flight, operationId, key, expectedRevision, disposition, value, cancellationToken).ConfigureAwait(false);
    }

    internal ValueTask<SecretOperationFlights.Lease> ReserveOperationAsync(Guid operationId, CancellationToken cancellationToken)
    {
        if (operationId == Guid.Empty) throw new ArgumentException("secret_mutation_invalid");
        return _flights.EnterAsync(operationId, cancellationToken);
    }

    internal ValueTask<SecretFieldStatus> ApplyReservedAsync(SecretOperationFlights.Lease reservation, Guid operationId, SecretFieldKey key,
        long expectedRevision, SecretDisposition disposition, SecretValue? value, CancellationToken cancellationToken)
    {
        reservation.Require(_flights, operationId);
        return ApplyCore(operationId, key, expectedRevision, disposition, value, cancellationToken);
    }

    private ValueTask<SecretFieldStatus> ApplyCore(Guid operationId, SecretFieldKey key, long expectedRevision,
        SecretDisposition disposition, SecretValue? value, CancellationToken cancellationToken)
    {
        RequireInstanceKey(key);
        key.RequireStandaloneMutation();
        if (operationId == Guid.Empty || expectedRevision is < 0 or > SecretFieldReference.MaximumRevision || !Enum.IsDefined(disposition)
            || (disposition == SecretDisposition.Set) != (value is not null)) throw new ArgumentException("secret_mutation_invalid");
        cancellationToken.ThrowIfCancellationRequested();
        var identity = Identity(operationId, key, expectedRevision, disposition);
        string? proof;
        using (var connection = DatabaseConnections.Open(_database.DatabasePath, readOnly: true))
        {
            proof = value is null ? null : SecretReplayProof.Compute(_instance, connection, identity, value);
            var replay = ReadReplay(connection, null, identity, proof);
            if (replay is not null) return ValueTask.FromResult(replay);
        }
        if (value is not null)
        {
            using var draft = new PreparationPlanCodec(_instance, new(), _database).Freeze(identity, Guid.NewGuid(), key, expectedRevision, value, cancellationToken);
            return ValueTask.FromResult(new SecretPreparationExecutor(_instance, new(), _database, _observer).Execute(identity, draft));
        }
        var prior = GetStateAsync(key, cancellationToken).GetAwaiter().GetResult();
        using var retirement = disposition != SecretDisposition.Keep && prior.SecretId is { } previousId
            ? SecretFieldRetirement.Prepare(_instance, _database, operationId, previousId) : null;
        SecretPayloadIdentity? candidate = null;
        var result = _database.Write((connection, transaction) =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            var accepted = OperationRows.Accept(connection, transaction, identity, DateTimeOffset.UtcNow);
            if (!accepted.IsNew)
                return (State: ReadReplay(connection, transaction, identity, proof) ?? throw new IOException("secret_operation_result_missing"), IsNew: false);
            var before = SecretFieldRows.ReadField(connection, transaction, key);
            var next = SecretMutationPolicy.Apply(before, expectedRevision, disposition, candidate?.SecretId);
            if (disposition == SecretDisposition.Keep && (before.Revision != prior.Revision || before.SecretId != prior.SecretId))
                throw new InvalidOperationException("secret_revision_conflict");
            var readable = disposition == SecretDisposition.Set || disposition == SecretDisposition.Keep && prior.IsReadable;
            WriteResult(connection, transaction, operationId, key, next, candidate, readable, proof);
            retirement?.RegisterInside(connection, transaction);
            return (State: new SecretFieldStatus(next.Revision, next.SecretId, next.SecretId is not null, readable), IsNew: true);
        });
        if (result.IsNew) retirement?.Materialize(_database);
        return ValueTask.FromResult(result.State);
    }

    internal SecretFieldStatus? Lookup(Guid operationId, SecretFieldKey key, long expectedRevision,
        SecretDisposition disposition, SecretValue? value, CancellationToken cancellationToken)
    {
        RequireInstanceKey(key);
        key.RequireStandaloneMutation();
        if (operationId == Guid.Empty || expectedRevision is < 0 or > SecretFieldReference.MaximumRevision || !Enum.IsDefined(disposition)
            || (disposition == SecretDisposition.Set) != (value is not null)) throw new ArgumentException("secret_mutation_invalid");
        cancellationToken.ThrowIfCancellationRequested();
        var identity = Identity(operationId, key, expectedRevision, disposition);
        using var connection = DatabaseConnections.Open(_database.DatabasePath, readOnly: true);
        var proof = value is null ? null : SecretReplayProof.Compute(_instance, connection, identity, value);
        return ReadReplay(connection, null, identity, proof);
    }

    private SecretFieldStatus Status(SecretFieldReference state)
    {
        if (state.SecretId is not { } id) return new(state.Revision, null, false, false);
        try
        {
            using var connection = DatabaseConnections.Open(_database.DatabasePath, readOnly: true);
            using var material = _files.Read(ReadPayload(connection, null, id));
            return new(state.Revision, id, true, true);
        }
        catch (Exception error) when (error is IOException or CryptographicException or Win32Exception or UnauthorizedAccessException)
        { return new(state.Revision, id, true, false); }
    }

    public static OperationIdentity Identity(Guid id, SecretFieldKey key, long revision, SecretDisposition disposition) =>
        SecretFieldOperation.Identity(id, key, revision, disposition);

    internal static SecretFieldStatus? ReadReplay(SqliteConnection connection, SqliteTransaction? transaction, OperationIdentity identity, string? proof)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT o.subject_id,o.request_digest,o.operation_kind,m.result_revision,m.result_secret_id,m.result_readable,m.replay_proof " +
            "FROM operations o LEFT JOIN secret_mutations m ON m.operation_id=o.operation_id WHERE o.operation_id=$id;";
        command.Parameters.AddWithValue("$id", identity.OperationId.ToString("D"));
        using var reader = command.ExecuteReader();
        if (!reader.Read()) return null;
        if (reader.GetString(0) != identity.SubjectId || reader.GetString(1) != identity.RequestDigest || reader.GetString(2) != identity.Kind)
            throw new OperationConflictException();
        if (reader.IsDBNull(3))
        {
            reader.Dispose();
            var pending = SecretPreparationRows.Read(connection, transaction, identity.OperationId, proof);
            throw new IOException(pending == SecretPreparationState.Cancelled ? "secret_preparation_not_executed" : "secret_preparation_recovery_pending");
        }
        if (!SecretReplayProof.Matches(reader.IsDBNull(6) ? null : reader.GetString(6), proof)) throw new OperationConflictException();
        return new(reader.GetInt64(3), reader.IsDBNull(4) ? null : Guid.Parse(reader.GetString(4)), !reader.IsDBNull(4), reader.GetBoolean(5));
    }

    internal static SecretPayloadIdentity ReadPayload(SqliteConnection connection, SqliteTransaction? transaction, Guid id, bool allowRetired = false)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT protection_scheme,owner_sid,digest,size_bytes,state FROM secret_payloads WHERE secret_id=$id;";
        command.Parameters.AddWithValue("$id", id.ToString("D"));
        using var reader = command.ExecuteReader();
        if (!reader.Read() || reader.GetString(4) != "committed" && !(allowRetired && reader.GetString(4) == "retired")) throw new IOException("secret_payload_reference_unverified");
        return new(id, reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetInt64(3));
    }

    internal static void WriteResult(SqliteConnection connection, SqliteTransaction transaction, Guid operationId, SecretFieldKey key,
        SecretFieldReference next, SecretPayloadIdentity? candidate, bool readable, string? proof)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        if (candidate is not null)
        {
            command.CommandText = "UPDATE secret_payloads SET state='committed' WHERE secret_id=$secret AND state='prepared';";
            command.Parameters.AddWithValue("$secret", candidate.SecretId.ToString("D"));
            if (command.ExecuteNonQuery() != 1) throw new IOException("secret_candidate_state_changed");
            command.Parameters.Clear();
        }
        SecretFieldRows.RequireDatabaseKey(connection, transaction, key);
        command.CommandText = "INSERT INTO secret_fields(owner_kind,owner_id,subject_id,field,revision,secret_id) VALUES($kind,$owner,$subject,$field,$revision,$secret) " +
            "ON CONFLICT(owner_kind,owner_id,subject_id,field) DO UPDATE SET revision=excluded.revision,secret_id=excluded.secret_id;";
        SecretFieldRows.BindKey(command, key);
        command.Parameters.AddWithValue("$revision", next.Revision);
        command.Parameters.AddWithValue("$secret", next.SecretId is { } secret ? secret.ToString("D") : DBNull.Value);
        command.ExecuteNonQuery();
        command.Parameters.Clear();
        command.CommandText = "INSERT INTO secret_mutations(operation_id,result_revision,result_secret_id,result_readable,replay_proof) " +
            "VALUES($operation,$revision,$secret,$readable,$proof);";
        command.Parameters.AddWithValue("$revision", next.Revision);
        command.Parameters.AddWithValue("$secret", next.SecretId is { } resultSecret ? resultSecret.ToString("D") : DBNull.Value);
        command.Parameters.AddWithValue("$operation", operationId.ToString("D"));
        command.Parameters.AddWithValue("$readable", readable);
        command.Parameters.AddWithValue("$proof", (object?)proof ?? DBNull.Value);
        command.ExecuteNonQuery();
        command.CommandText = "UPDATE operations SET status='completed' WHERE operation_id=$operation;";
        command.ExecuteNonQuery();
    }

    public static void VerifyInventory(StateDatabase database)
    {
        using var connection = DatabaseConnections.Open(database.DatabasePath, readOnly: true);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT owner_kind,owner_id,subject_id,field,revision,secret_id FROM secret_fields;";
        using var rows = command.ExecuteReader();
        while (rows.Read())
        {
            var kind = rows.GetString(0) switch
            {
                "extension" => FieldOwnerKind.Extension, "host" => FieldOwnerKind.Host,
                "notificationTask" => FieldOwnerKind.NotificationTask,
                _ => throw new IOException("secret_field_owner_invalid")
            };
            var subject = Guid.ParseExact(rows.GetString(2), "D");
            if (rows.GetString(2) != subject.ToString("D")) throw new IOException("secret_field_identity_invalid");
            Guid? ownerInstance = kind == FieldOwnerKind.NotificationTask ? Guid.ParseExact(rows.GetString(1), "D") : null;
            _ = new SecretFieldKey(kind, kind == FieldOwnerKind.Extension ? rows.GetString(1) : null, subject, rows.GetString(3), ownerInstance);
            if (kind == FieldOwnerKind.NotificationTask)
            {
                if (rows.GetString(1) != database.InstanceId.ToString("D")) throw new IOException("secret_notification_instance_unbound");
                NotificationCatalogRows.RequireFieldProvenance(connection, null, database.InstanceId, subject, rows.GetString(3),
                    new(rows.GetInt64(4), rows.IsDBNull(5) ? null : Guid.ParseExact(rows.GetString(5), "D")));
            }
            if (kind == FieldOwnerKind.Host && (rows.GetString(1) != subject.ToString("D") || subject != database.InstanceId))
                throw new IOException("secret_host_instance_unbound");
            _ = new SecretFieldReference(rows.GetInt64(4), rows.IsDBNull(5) ? null : Guid.ParseExact(rows.GetString(5), "D"));
            if (!rows.IsDBNull(5) && rows.GetString(5) != Guid.ParseExact(rows.GetString(5), "D").ToString("D"))
                throw new IOException("secret_payload_reference_unverified");
        }
    }

    private void RequireInstanceKey(SecretFieldKey key)
    {
        ArgumentNullException.ThrowIfNull(key);
        if (key.OwnerKind == FieldOwnerKind.Host && key.SubjectId != _instance.InstanceId)
            throw new IOException("secret_host_instance_unbound");
        if (key.OwnerKind == FieldOwnerKind.NotificationTask && key.OwnerInstanceId != _instance.InstanceId)
            throw new IOException("secret_notification_instance_unbound");
    }

}


