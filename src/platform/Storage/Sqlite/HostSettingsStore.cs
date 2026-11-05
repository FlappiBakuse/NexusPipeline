using System.Collections.Immutable;
using System.Security.Cryptography;
using Microsoft.Data.Sqlite;
using NexusPipeline.Core.Operations;
using NexusPipeline.Core.Settings;
using NexusPipeline.Platform.Serialization;
using NexusPipeline.Platform.Storage.Settings;

namespace NexusPipeline.Platform.Storage.Sqlite;

public sealed class HostSettingsStore(StateDatabase database) : IHostSettingsStore
{
    public static OperationIdentity Identity(Guid operationId, string subjectId, HostSettingsChange change) =>
        new(operationId, subjectId, Digest(change), "settings.save");

    public HostSettingsSnapshot Read()
    {
        using var connection = DatabaseConnections.Open(database.DatabasePath, readOnly: true);
        using var transaction = connection.BeginTransaction(deferred: true);
        return HostSettingsRows.ReadCurrent(connection, transaction);
    }

    internal HostSettingsSnapshot ReadRevision(long revision)
    {
        if (revision is < 0 or > 9007199254740991) throw new HostSettingsException("settings_revision_invalid");
        using var connection = DatabaseConnections.Open(database.DatabasePath, readOnly: true);
        using var transaction = connection.BeginTransaction(deferred: true);
        return HostSettingsRows.Read(connection, transaction, revision);
    }

    public SettingsSaveResult Commit(OperationIdentity operation, HostSettingsChange change, DateTimeOffset acceptedAt)
        => Commit(operation, change, acceptedAt, null);

    internal SettingsSaveResult Commit(OperationIdentity operation, HostSettingsChange change, DateTimeOffset acceptedAt, StartupIntentDraft? startup)
    {
        VerifyIdentity(operation, change);
        var replay = Lookup(operation, change);
        if (replay is not null) return replay;
        return database.Write((connection, transaction) =>
        {
            var accepted = OperationRows.Accept(connection, transaction, operation, acceptedAt);
            if (!accepted.IsNew) return ReadResult(connection, transaction, operation.OperationId);
            var before = HostSettingsRows.ReadCurrent(connection, transaction);
            var next = HostSettingsPolicy.Next(before, change.ExpectedRevision, change.Values);
            if (before.Values.AutoStart != next.Values.AutoStart && startup is null)
                throw new HostSettingsException("settings_startup_effect_required");
            if (StartupIntentRows.ReadAll(connection, transaction, database.InstanceId, pendingOnly: true).Count != 0)
                throw new HostSettingsException("settings_startup_recovery_required");
            HostSettingsRows.Insert(connection, transaction, next);
            HostSettingsRows.Execute(connection, transaction, "UPDATE settings_current SET revision=$0 WHERE singleton=1;", next.Revision);
            HostSettingsRows.Execute(connection, transaction, "INSERT INTO settings_mutations VALUES($0,$1);", operation.OperationId, next.Revision);
            if (startup is not null) StartupIntentRows.Register(connection, transaction, startup, database.InstanceId, operation.OperationId, before, next);
            else HostSettingsRows.Execute(connection, transaction, "UPDATE operations SET status='completed' WHERE operation_id=$0;", operation.OperationId);
            return ReadResult(connection, transaction, operation.OperationId) with { IsNew = true };
        });
    }

    internal SettingsExtensionIntent ReadExtensionIntent(string extensionId, bool firstInstallation)
    {
        var before = Read();
        return ExtensionIntent(before, extensionId, firstInstallation);
    }

    internal static void RequireExtensionIntentInside(SqliteConnection connection, SqliteTransaction transaction,
        string extensionId, SettingsExtensionIntent expected)
    {
        var before = HostSettingsRows.ReadCurrent(connection, transaction);
        var actual = ExtensionIntent(before, extensionId, expected.InitializeEnabledPreference);
        if (actual != expected) throw new HostSettingsException("settings_revision_conflict");
    }

    internal static long CommitExtensionPreferenceInside(SqliteConnection connection, SqliteTransaction transaction,
        Guid journalId, string extensionId, SettingsExtensionIntent expected)
    {
        using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = "SELECT EXISTS(SELECT 1 FROM operation_journals j JOIN operations o USING(operation_id) " +
                "WHERE j.journal_id=$journal AND j.operation_kind='extensionApply' AND j.phase='applied' " +
                "AND o.operation_kind='extension.apply' AND o.status='accepted' AND o.initial_journal_id=j.journal_id " +
                "AND EXISTS(SELECT 1 FROM journal_resource_claims c WHERE c.journal_id=j.journal_id " +
                "AND c.resource_key=$extension AND c.mode=$mutation) " +
                "AND EXISTS(SELECT 1 FROM journal_resource_claims c WHERE c.journal_id=j.journal_id " +
                "AND c.resource_key='settings:host' AND (c.mode=$settingsMode OR c.mode=$mutation)));";
            command.Parameters.AddWithValue("$journal", journalId.ToString("D"));
            command.Parameters.AddWithValue("$extension", "extension:" + extensionId);
            command.Parameters.AddWithValue("$mutation", (int)NexusPipeline.Core.Maintenance.ResourceClaimMode.Mutation);
            command.Parameters.AddWithValue("$settingsMode", (int)(expected.InitializeEnabledPreference
                ? NexusPipeline.Core.Maintenance.ResourceClaimMode.Mutation
                : NexusPipeline.Core.Maintenance.ResourceClaimMode.Reference));
            if ((long)command.ExecuteScalar()! != 1) throw new IOException("extension_settings_publication_unbound");
        }
        JournalEffectGate.Require(connection, transaction, journalId, NexusPipeline.Core.Configuration.EffectGateState.Entered);
        var before = HostSettingsRows.ReadCurrent(connection, transaction);
        if (ExtensionIntent(before, extensionId, expected.InitializeEnabledPreference) != expected)
            throw new HostSettingsException("settings_revision_conflict");
        if (!expected.InitializeEnabledPreference) return before.Revision;
        var values = before.Values with
        {
            Extensions = before.Values.Extensions.Add(new(extensionId, expected.DesiredEnabled))
                .OrderBy(item => item.ExtensionId, StringComparer.Ordinal).ToImmutableArray()
        };
        var next = HostSettingsPolicy.Next(before, expected.SettingsRevision, values);
        HostSettingsRows.Insert(connection, transaction, next);
        using var advance = connection.CreateCommand(); advance.Transaction = transaction;
        advance.CommandText = "UPDATE settings_current SET revision=$next WHERE singleton=1 AND revision=$expected;";
        advance.Parameters.AddWithValue("$next", next.Revision);
        advance.Parameters.AddWithValue("$expected", before.Revision);
        if (advance.ExecuteNonQuery() != 1) throw new HostSettingsException("settings_revision_conflict");
        return next.Revision;
    }

    private static SettingsExtensionIntent ExtensionIntent(HostSettingsSnapshot before, string extensionId,
        bool firstInstallation)
    {
        if (extensionId is not { Length: >= 1 and <= 80 } || extensionId[0] is not (>= 'a' and <= 'z') ||
            extensionId[^1] == '-' || extensionId.Contains("--", StringComparison.Ordinal) ||
            extensionId.Any(c => c is not (>= 'a' and <= 'z' or >= '0' and <= '9' or '-')))
            throw new HostSettingsException("settings_extension_preferences_invalid");
        var existing = before.Values.Extensions.SingleOrDefault(item => item.ExtensionId == extensionId);
        if (existing is not null) return new(before.Revision, existing.Enabled, false);
        if (!firstInstallation) throw new HostSettingsException("settings_extension_preference_required");
        if (before.Values.Extensions.Length >= 256) throw new HostSettingsException("settings_extension_preferences_invalid");
        return new(before.Revision, true, true);
    }

    internal SettingsSaveResult? Lookup(OperationIdentity operation, HostSettingsChange change)
    {
        VerifyIdentity(operation, change);
        using var connection = DatabaseConnections.Open(database.DatabasePath, readOnly: true);
        using var transaction = connection.BeginTransaction(deferred: true);
        return OperationRows.ReadExisting(connection, transaction, operation) is null ? null : ReadResult(connection, transaction, operation.OperationId);
    }

    private SettingsSaveResult ReadResult(SqliteConnection connection, SqliteTransaction transaction, Guid operationId)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT result_revision FROM settings_mutations WHERE operation_id=$id;";
        command.Parameters.AddWithValue("$id", operationId.ToString("D"));
        var revision = command.ExecuteScalar() ?? throw new IOException("settings_operation_requires_reconciliation");
        return new(operationId, HostSettingsRows.Read(connection, transaction, Convert.ToInt64(revision)), false)
            { StartupEffect = StartupIntentRows.Read(connection, transaction, operationId, database.InstanceId)?.Status };
    }

    private static void VerifyIdentity(OperationIdentity operation, HostSettingsChange change)
    {
        if (operation.Kind != "settings.save" || operation.RequestDigest != Digest(change)) throw new OperationConflictException();
    }

    private static string Digest(HostSettingsChange change) => Convert.ToHexStringLower(SHA256.HashData(BoundedJson.Serialize(change)));
}

internal sealed record SettingsExtensionIntent(long SettingsRevision, bool DesiredEnabled, bool InitializeEnabledPreference);
