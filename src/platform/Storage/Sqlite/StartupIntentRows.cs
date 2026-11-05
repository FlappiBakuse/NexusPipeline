using Microsoft.Data.Sqlite;
using NexusPipeline.Core.Settings;
using NexusPipeline.Platform.Storage.Settings;

namespace NexusPipeline.Platform.Storage.Sqlite;

internal sealed record StoredStartupIntent(StartupIntentDocument Document, StartupEffectStatus Status);

internal static class StartupIntentRows
{
    internal static byte[]? ReadCurrent(SqliteConnection connection, SqliteTransaction? transaction)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT command_json FROM startup_registration_current WHERE singleton=1;";
        using var row = command.ExecuteReader();
        if (!row.Read()) throw new IOException("startup_registration_current_missing");
        return row.IsDBNull(0) ? null : row.GetFieldValue<byte[]>(0);
    }

    internal static void Register(SqliteConnection connection, SqliteTransaction transaction, StartupIntentDraft draft,
        Guid instanceId, Guid operationId, HostSettingsSnapshot before, HostSettingsSnapshot next)
    {
        var document = draft.Read();
        var current = ReadCurrent(connection, transaction);
        if (document.InstanceId != instanceId || document.OperationId != operationId || document.ExpectedRevision != before.Revision ||
            document.Enable != next.Values.AutoStart || document.BeforeRegistrationDigest != (current is null ? null : StartupIntentCodec.Hash(current)))
            throw new IOException("startup_intent_settings_unbound");
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "INSERT INTO startup_registration_intents VALUES($id,$revision,$document,$digest,1);";
        var bytes = draft.Export();
        command.Parameters.AddWithValue("$id", operationId.ToString("D"));
        command.Parameters.AddWithValue("$revision", next.Revision);
        command.Parameters.AddWithValue("$document", bytes);
        command.Parameters.AddWithValue("$digest", StartupIntentCodec.Hash(bytes));
        command.ExecuteNonQuery();
    }

    internal static StoredStartupIntent? Read(SqliteConnection connection, SqliteTransaction? transaction, Guid operationId, Guid instanceId)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT settings_revision,document,document_digest,phase FROM startup_registration_intents WHERE operation_id=$id;";
        command.Parameters.AddWithValue("$id", operationId.ToString("D"));
        using var row = command.ExecuteReader();
        if (!row.Read()) return null;
        var document = StartupIntentCodec.Decode(row.GetFieldValue<byte[]>(1), row.GetString(2));
        var revision = row.GetInt64(0);
        var phase = (StartupEffectState)row.GetInt32(3);
        if (document.InstanceId != instanceId || document.OperationId != operationId || revision != document.ExpectedRevision + 1 ||
            phase is not (StartupEffectState.Prepared or StartupEffectState.Entered or StartupEffectState.Completed or StartupEffectState.Quarantined))
            throw new IOException("startup_intent_sql_identity_invalid");
        return new(document, new(operationId, revision, document.Enable, phase));
    }

    internal static IReadOnlyList<StoredStartupIntent> ReadAll(SqliteConnection connection, SqliteTransaction? transaction, Guid instanceId, bool pendingOnly)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT operation_id FROM startup_registration_intents " + (pendingOnly ? "WHERE phase<>3 " : "") + "ORDER BY settings_revision LIMIT 10001;";
        var ids = new List<Guid>();
        using (var rows = command.ExecuteReader()) while (rows.Read()) ids.Add(Guid.Parse(rows.GetString(0)));
        if (ids.Count > 10000 || pendingOnly && ids.Count > 1) throw new IOException("startup_intent_inventory_unrecognized");
        return ids.Select(id => Read(connection, transaction, id, instanceId) ?? throw new IOException("startup_intent_missing")).ToArray();
    }

    internal static void Advance(SqliteConnection connection, SqliteTransaction transaction, Guid instanceId, Guid operationId,
        StartupEffectState before, StartupEffectState after)
    {
        var intent = Read(connection, transaction, operationId, instanceId) ?? throw new IOException("startup_intent_missing");
        var document = intent.Document;
        if (intent.Status.State != before || after is not (StartupEffectState.Entered or StartupEffectState.Completed or StartupEffectState.Quarantined) ||
            before == StartupEffectState.Completed || before == StartupEffectState.Prepared && after == StartupEffectState.Completed ||
            before == StartupEffectState.Quarantined && after != StartupEffectState.Completed)
            throw new IOException("startup_intent_transition_invalid");
        var saved = HostSettingsRows.ReadCurrent(connection, transaction);
        if (saved.Revision != intent.Status.SettingsRevision || saved.Values.AutoStart != document.Enable)
            throw new IOException("startup_intent_saved_settings_changed");
        var current = ReadCurrent(connection, transaction);
        if (document.BeforeRegistrationDigest != (current is null ? null : StartupIntentCodec.Hash(current)))
            throw new IOException("startup_intent_prior_registration_changed");
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "UPDATE startup_registration_intents SET phase=$phase WHERE operation_id=$id AND phase=$before;";
        command.Parameters.AddWithValue("$phase", (int)after);
        command.Parameters.AddWithValue("$id", operationId.ToString("D"));
        command.Parameters.AddWithValue("$before", (int)before);
        if (command.ExecuteNonQuery() != 1) throw new IOException("startup_intent_phase_conflict");
        if (after != StartupEffectState.Completed) return;
        command.CommandText = "UPDATE startup_registration_current SET command_json=$command WHERE singleton=1;";
        command.Parameters.AddWithValue("$command", document.Enable ? StartupIntentCodec.EncodeCommand(document.Command) : DBNull.Value);
        if (command.ExecuteNonQuery() != 1) throw new IOException("startup_registration_current_missing");
        command.CommandText = "UPDATE operations SET status='completed' WHERE operation_id=$id;";
        command.ExecuteNonQuery();
    }

    internal static void Verify(SqliteConnection connection, Guid instanceId)
    {
        var current = ReadCurrent(connection, null);
        if (current is not null && StartupIntentCodec.DecodeCommand(current).Definition.InstanceId != instanceId)
            throw new IOException("startup_registration_instance_unbound");
        var intents = ReadAll(connection, null, instanceId, pendingOnly: false);
        foreach (var intent in intents)
        {
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT result_revision FROM settings_mutations WHERE operation_id=$id;";
            command.Parameters.AddWithValue("$id", intent.Status.OperationId.ToString("D"));
            if (command.ExecuteScalar() is not long revision || revision != intent.Status.SettingsRevision)
                throw new IOException("startup_intent_settings_result_unbound");
            using var transaction = connection.BeginTransaction(deferred: true);
            var saved = HostSettingsRows.Read(connection, transaction, revision);
            if (saved.Values.AutoStart != intent.Status.Enable) throw new IOException("startup_intent_target_unbound");
        }
        var completed = intents.LastOrDefault(intent => intent.Status.State == StartupEffectState.Completed);
        var expectedCurrent = completed is { Status.Enable: true } ? StartupIntentCodec.EncodeCommand(completed.Document.Command) : null;
        if ((current is null) != (expectedCurrent is null) || current is not null && !current.AsSpan().SequenceEqual(expectedCurrent))
            throw new IOException("startup_registration_history_unbound");
        var pending = intents.Where(intent => intent.Status.State != StartupEffectState.Completed).ToArray();
        if (pending.Length > 1) throw new IOException("startup_intent_inventory_unrecognized");
        using var view = connection.BeginTransaction(deferred: true);
        var latestSettings = HostSettingsRows.ReadCurrent(connection, view);
        if (pending.Length == 1)
        {
            var intent = pending[0];
            if (intent.Status.SettingsRevision != latestSettings.Revision || latestSettings.Values.AutoStart != intent.Status.Enable ||
                intent.Document.BeforeRegistrationDigest != (current is null ? null : StartupIntentCodec.Hash(current)))
                throw new IOException("startup_intent_pending_snapshot_unbound");
        }
        else if (latestSettings.Values.AutoStart != (current is not null)) throw new IOException("startup_registration_saved_value_unbound");
    }
}
