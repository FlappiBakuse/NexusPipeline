using System.Globalization;
using Microsoft.Data.Sqlite;
using NexusPipeline.Core.Configuration;
using NexusPipeline.Core.Operations;
using NexusPipeline.Core.Maintenance;
using NexusPipeline.Platform.Storage.Journals;

namespace NexusPipeline.Platform.Storage.Sqlite;

public sealed class RecoveryJournalRegistry(StateDatabase database) : IRecoveryRegistry
{
    internal StateDatabase Database => database;
    public (OperationAcceptance Acceptance, Guid JournalId) AcceptPrepared(OperationIdentity identity, RegisteredJournal journal,
        IReadOnlyList<FileEffect> effects, DateTimeOffset acceptedAt, IReadOnlyList<ResourceClaim>? resourceClaims = null)
    {
        var claims = resourceClaims?.ToArray();
        var frozenEffects = effects.ToArray();
        var writer = JournalWriterStore.CurrentWriter;
        return database.Write((connection, transaction) => AcceptPreparedInside(connection, transaction, identity, journal,
            frozenEffects, acceptedAt, claims, writer));
    }

    internal static (OperationAcceptance Acceptance, Guid JournalId) AcceptPreparedInside(SqliteConnection connection,
        SqliteTransaction transaction, OperationIdentity identity, RegisteredJournal journal, IReadOnlyList<FileEffect> effects,
        DateTimeOffset acceptedAt, IReadOnlyList<ResourceClaim>? resourceClaims, JournalWriter writer, RecoveryCapacityBudget? capacity = null)
    {
        Validate(journal);
        if (identity.OperationId != journal.OperationId) throw new ArgumentException("Operation and journal must be bound");
        if (resourceClaims is not null && (!resourceClaims.Select(claim => claim.ResourceKey).SequenceEqual(journal.ResourceKeys) ||
            resourceClaims.Any(claim => !Enum.IsDefined(claim.Mode)))) throw new IOException("journal_resource_claims_unbound");
        if (journal.Kind == JournalKind.FileCleanup && effects.Any(item => item.Action != FileEffectAction.RemoveOwned))
            throw new ArgumentException("Cleanup only accepts owned removal effects");
        var acceptance = OperationRows.Accept(connection, transaction, identity, acceptedAt);
        if (!acceptance.IsNew)
        {
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "SELECT initial_journal_id FROM operations WHERE operation_id=$operation;";
            command.Parameters.AddWithValue("$operation", identity.OperationId.ToString("D"));
            using var reader = command.ExecuteReader();
            if (!reader.Read() || reader.IsDBNull(0)) throw new IOException("accepted_operation_requires_reconciliation");
            return (acceptance, Guid.Parse(reader.GetString(0)));
        }
        RegisterInside(connection, transaction, journal, effects, writer, capacity);
        if (resourceClaims is not null) JournalResourceClaims.Insert(connection, transaction, journal.JournalId, resourceClaims);
        return (acceptance, journal.JournalId);
    }

    public void RegisterPrepared(RegisteredJournal journal)
    {
        Validate(journal);
        if (journal.Phase != JournalPhase.Prepared || journal.Revision != 1)
            throw new IOException("journal_initial_state_invalid");
        var writer = JournalWriterStore.CurrentWriter;
        database.Write((connection, transaction) =>
        {
            RegisterInside(connection, transaction, journal, [], writer);
            return true;
        });
    }

    internal static void RegisterInside(SqliteConnection connection, SqliteTransaction transaction, RegisteredJournal journal,
        IReadOnlyList<FileEffect> effects, JournalWriter writer, RecoveryCapacityBudget? capacity = null, Guid? capacityParentJournalId = null)
    {
        Validate(journal);
        if (journal.Kind == JournalKind.FileCleanup && effects.Any(item => item.Action != FileEffectAction.RemoveOwned))
            throw new ArgumentException("Cleanup only accepts owned removal effects");
        RecoveryCapacityStore.Register(connection, transaction, journal.JournalId,
            capacity ?? RecoveryCapacityBudget.ForJournal(journal, effects), capacityParentJournalId);
        InsertPrepared(connection, transaction, journal);
        JournalWriterStore.Insert(connection, transaction, journal.JournalId, writer);
        FileEffectRows.Insert(connection, transaction, journal.JournalId, effects);
        IncrementIndex(connection, transaction);
    }

    internal static RegisteredJournal ReadInside(SqliteConnection connection, SqliteTransaction transaction, Guid journalId) =>
        ReadJournalInside(connection, transaction, journalId);

    internal static RecoveryRegistrySnapshot ReadValidatedSnapshotInside(SqliteConnection connection, SqliteTransaction transaction) =>
        ReadSnapshot(connection, transaction);

    internal RegisteredJournal ReadJournal(Guid journalId)
    {
        using var connection = DatabaseConnections.Open(database.DatabasePath, readOnly: true);
        using var transaction = connection.BeginTransaction(deferred: true);
        var result = ReadJournalInside(connection, transaction, journalId);
        transaction.Commit(); return result;
    }

    internal RegisteredJournal? FindJournal(Guid journalId)
    {
        using var connection = DatabaseConnections.Open(database.DatabasePath, readOnly: true);
        using var transaction = connection.BeginTransaction(deferred: true);
        using var command = connection.CreateCommand(); command.Transaction = transaction;
        command.CommandText = "SELECT EXISTS(SELECT 1 FROM operation_journals WHERE journal_id=$id);";
        command.Parameters.AddWithValue("$id", journalId.ToString("D"));
        return (long)command.ExecuteScalar()! == 0 ? null : ReadJournalInside(connection, transaction, journalId);
    }

    internal RegisteredJournal ReadOperationJournal(Guid operationId)
    {
        using var connection = DatabaseConnections.Open(database.DatabasePath, readOnly: true);
        using var transaction = connection.BeginTransaction(deferred: true);
        using var command = connection.CreateCommand(); command.Transaction = transaction;
        command.CommandText = "SELECT initial_journal_id FROM operations WHERE operation_id=$id;";
        command.Parameters.AddWithValue("$id", operationId.ToString("D"));
        var id = command.ExecuteScalar() as string ?? throw new IOException("journal_not_registered");
        var journal = ReadJournalInside(connection, transaction, Guid.ParseExact(id, "D"));
        if (journal.OperationId != operationId) throw new IOException("operation_journal_unbound");
        return journal;
    }

    internal IReadOnlySet<Guid> ReadInactiveIds()
    {
        using var connection = DatabaseConnections.Open(database.DatabasePath, readOnly: true);
        using var transaction = connection.BeginTransaction(deferred: true);
        using var command = connection.CreateCommand(); command.Transaction = transaction;
        command.CommandText = "SELECT j.journal_id,j.operation_id,j.operation_kind,j.phase,j.revision,j.plan_path,j.plan_digest,k.resource_key " +
            "FROM operation_journals j JOIN recovery_capacity_members m ON m.journal_id=j.journal_id " +
            "JOIN recovery_capacity_reservations r USING(owner_journal_id) LEFT JOIN journal_resources k ON k.journal_id=j.journal_id " +
            "WHERE j.phase='committed' AND NOT " + RecoveryCapacityStore.ActiveReservation + " ORDER BY j.journal_id,k.resource_key;";
        var result = new HashSet<Guid>(); RegisteredJournal? current = null; var resources = new List<string>();
        using var rows = command.ExecuteReader();
        while (rows.Read())
        {
            var id = Guid.ParseExact(rows.GetString(0), "D");
            if (current?.JournalId != id)
            {
                Finish(); resources = [];
                if (!Enum.TryParse<JournalKind>(rows.GetString(2), true, out var kind) ||
                    !Enum.TryParse<JournalPhase>(rows.GetString(3), true, out var phase)) throw new IOException("recovery_registry_journal_invalid");
                current = new(id, Guid.ParseExact(rows.GetString(1), "D"), kind, phase, rows.GetInt64(4), rows.GetString(5), rows.GetString(6), []);
            }
            if (rows.IsDBNull(7) || resources.Count >= 4096) throw new IOException("recovery_registry_resource_limit");
            resources.Add(rows.GetString(7));
        }
        Finish(); return result;

        void Finish()
        {
            if (current is null) return;
            try { Validate(current with { ResourceKeys = resources }); }
            catch (ArgumentException error) { throw new IOException("recovery_registry_journal_invalid", error); }
            result.Add(current.JournalId);
        }
    }

    internal static RegisteredJournal ReadJournalInside(SqliteConnection connection, SqliteTransaction transaction, Guid journalId)
    {
        using var command = connection.CreateCommand(); command.Transaction = transaction;
        command.CommandText = "SELECT operation_id,operation_kind,phase,revision,plan_path,plan_digest FROM operation_journals WHERE journal_id=$id;";
        command.Parameters.AddWithValue("$id", journalId.ToString("D"));
        RegisteredJournal result;
        using (var row = command.ExecuteReader())
        {
            if (!row.Read()) throw new IOException("journal_not_registered");
            if (!Enum.TryParse<JournalKind>(row.GetString(1), true, out var kind) ||
                !Enum.TryParse<JournalPhase>(row.GetString(2), true, out var phase)) throw new IOException("recovery_registry_journal_invalid");
            result = new(journalId, Guid.ParseExact(row.GetString(0), "D"), kind, phase,
                row.GetInt64(3), row.GetString(4), row.GetString(5), []);
        }
        command.CommandText = "SELECT resource_key FROM journal_resources WHERE journal_id=$id ORDER BY resource_key LIMIT 4097;";
        var resources = new List<string>();
        using (var rows = command.ExecuteReader()) while (rows.Read()) resources.Add(rows.GetString(0));
        if (resources.Count is 0 or > 4096) throw new IOException("recovery_registry_resource_limit");
        result = result with { ResourceKeys = resources.AsReadOnly() };
        try { Validate(result); }
        catch (ArgumentException error) { throw new IOException("recovery_registry_journal_invalid", error); }
        return result;
    }

    internal IEnumerable<RegisteredJournal> ReadHistory(bool completedFamiliesOnly = false)
    {
        using var connection = DatabaseConnections.Open(database.DatabasePath, readOnly: true);
        using var transaction = connection.BeginTransaction(deferred: true);
        foreach (var journal in ReadHistoryInside(connection, transaction, completedFamiliesOnly)) yield return journal;
    }

    internal static IEnumerable<RegisteredJournal> ReadHistoryInside(SqliteConnection connection, SqliteTransaction transaction,
        bool completedFamiliesOnly = false)
    {
        string after = "";
        while (true)
        {
            using var command = connection.CreateCommand(); command.Transaction = transaction;
            command.CommandText = completedFamiliesOnly
                ? "SELECT j.journal_id FROM operation_journals j JOIN recovery_capacity_reservations r ON r.owner_journal_id=j.journal_id " +
                    "WHERE j.journal_id>$after AND NOT " + RecoveryCapacityStore.ActiveReservation + " ORDER BY j.journal_id LIMIT 128;"
                : "SELECT journal_id FROM operation_journals WHERE journal_id>$after ORDER BY journal_id LIMIT 128;";
            command.Parameters.AddWithValue("$after", after);
            var ids = new List<Guid>();
            using (var reader = command.ExecuteReader()) while (reader.Read()) ids.Add(Guid.ParseExact(reader.GetString(0), "D"));
            if (ids.Count == 0) yield break;
            foreach (var id in ids) yield return ReadJournalInside(connection, transaction, id);
            after = ids[^1].ToString("D");
        }
    }

    internal IEnumerable<RecoveryRegistrySnapshot> ReadCompleted()
    {
        using var connection = DatabaseConnections.Open(database.DatabasePath, readOnly: true);
        using var transaction = connection.BeginTransaction(deferred: true);
        RecoveryCapacityStore.VerifyGlobal(connection, transaction);
        var after = "";
        while (true)
        {
            using var command = connection.CreateCommand(); command.Transaction = transaction;
            command.CommandText = "SELECT r.owner_journal_id FROM recovery_capacity_reservations r WHERE r.owner_journal_id>$after AND NOT " +
                RecoveryCapacityStore.ActiveReservation + " ORDER BY r.owner_journal_id LIMIT 128;";
            command.Parameters.AddWithValue("$after", after);
            var owners = new List<Guid>();
            using (var rows = command.ExecuteReader()) while (rows.Read()) owners.Add(Guid.ParseExact(rows.GetString(0), "D"));
            foreach (var owner in owners) yield return ReadSnapshot(connection, transaction, owner);
            if (owners.Count < 128) break;
            after = owners[^1].ToString("D");
        }
        transaction.Commit();
    }

    private static void InsertPrepared(SqliteConnection connection, SqliteTransaction transaction, RegisteredJournal journal)
    {
        if (journal.Phase != JournalPhase.Prepared || journal.Revision != 1) throw new IOException("journal_initial_state_invalid");
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "INSERT INTO operation_journals VALUES($id,$operation,$kind,'prepared',1,$path,$digest,$created);";
        command.Parameters.AddWithValue("$id", journal.JournalId.ToString("D"));
        command.Parameters.AddWithValue("$operation", journal.OperationId.ToString("D"));
        command.Parameters.AddWithValue("$kind", Wire(journal.Kind));
        command.Parameters.AddWithValue("$path", journal.PlanPath);
        command.Parameters.AddWithValue("$digest", journal.PlanDigest);
        command.Parameters.AddWithValue("$created", DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture));
        command.ExecuteNonQuery();
        if (journal.Kind is JournalKind.RunAttempt or JournalKind.UpdateApply or JournalKind.ExtensionApply or JournalKind.AssetPrepare or JournalKind.SecretPrepare)
        {
            command.CommandText = "INSERT INTO journal_effect_gates VALUES($id,'unentered',1);";
            command.ExecuteNonQuery();
        }
        command.CommandText = "UPDATE operations SET initial_journal_id=$id WHERE operation_id=$operation AND initial_journal_id IS NULL;";
        command.ExecuteNonQuery();
        command.CommandText = "INSERT INTO journal_resources VALUES($id,$resource); INSERT INTO resource_isolations VALUES($resource,$id,'verified','recovery_pending');";
        var resource = command.Parameters.Add("$resource", SqliteType.Text);
        foreach (var key in journal.ResourceKeys)
        {
            resource.Value = key;
            command.ExecuteNonQuery();
        }
    }

    public void Advance(RegisteredJournal journal, long expectedRevision, NoEffectEvidence? noEffect = null, bool hasVerifiedRecoveryEvidence = false)
    {
        database.Write((connection, transaction) =>
        {
            AdvanceInside(connection, transaction, journal, expectedRevision, noEffect, hasVerifiedRecoveryEvidence);
            return true;
        });
    }

    internal void AdvanceInside(SqliteConnection connection, SqliteTransaction transaction, RegisteredJournal journal,
        long expectedRevision, NoEffectEvidence? noEffect = null, bool hasVerifiedRecoveryEvidence = false)
    {
            Validate(journal);
            var current = ReadJournalInside(connection, transaction, journal.JournalId);
            if (current.Revision != expectedRevision || journal.Revision != checked(expectedRevision + 1))
                throw new IOException("journal_revision_conflict");
            if (current.OperationId != journal.OperationId || current.Kind != journal.Kind || current.PlanDigest != journal.PlanDigest ||
                current.PlanPath != journal.PlanPath || !current.ResourceKeys.SequenceEqual(journal.ResourceKeys.Order(StringComparer.Ordinal)))
                throw new IOException("journal_frozen_identity_changed");
            if (current.Phase != journal.Phase && !JournalTransitionPolicy.CanMove(journal.Kind, current.Phase, journal.Phase, noEffect, hasVerifiedRecoveryEvidence) ||
                current.Phase == JournalPhase.Committed)
                throw new IOException("journal_transition_rejected");
            if (current.Phase == JournalPhase.Prepared && journal.Phase is JournalPhase.Applying or JournalPhase.Committed)
                JournalEffectGate.Require(connection, transaction, journal.JournalId,
                    journal.Phase == JournalPhase.Applying ? EffectGateState.Entered : EffectGateState.Sealed);
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "UPDATE operation_journals SET phase=$phase,revision=$revision WHERE journal_id=$id AND revision=$expected;";
            command.Parameters.AddWithValue("$phase", Wire(journal.Phase));
            command.Parameters.AddWithValue("$revision", journal.Revision);
            command.Parameters.AddWithValue("$id", journal.JournalId.ToString("D"));
            command.Parameters.AddWithValue("$expected", expectedRevision);
            if (command.ExecuteNonQuery() != 1) throw new IOException("journal_revision_conflict");
            if (journal.Phase == JournalPhase.Committed)
            {
                command.CommandText = "SELECT count(*) FROM file_effects WHERE journal_id=$id AND ($cleanup=1 OR action<>'removeOwned') AND state NOT IN ('completed','notExecuted') " +
                    "AND NOT ($extension=1 AND state='reverted' AND action IN ('preserveOriginal','installCandidate'));";
                command.Parameters.AddWithValue("$cleanup", journal.Kind == JournalKind.FileCleanup ? 1 : 0);
                command.Parameters.AddWithValue("$extension", journal.Kind is JournalKind.ExtensionApply or JournalKind.UpdateApply ? 1 : 0);
                if (Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture) != 0)
                    throw new IOException("file_effects_not_closed");
                command.CommandText = "DELETE FROM resource_isolations WHERE journal_id=$id;";
                command.ExecuteNonQuery();
            }
            else
            {
                command.CommandText = "UPDATE resource_isolations SET reason_code=$reason WHERE journal_id=$id;";
                command.Parameters.AddWithValue("$reason", journal.Phase == JournalPhase.Quarantined ? "recovery_quarantined" : "recovery_pending");
                command.ExecuteNonQuery();
            }
            IncrementIndex(connection, transaction);
    }

    public RecoveryRegistrySnapshot ReadSnapshot()
    {
        using var connection = DatabaseConnections.Open(database.DatabasePath, readOnly: true);
        using var transaction = connection.BeginTransaction(deferred: true);
        var result = ReadSnapshot(connection, transaction, includeHistory: true);
        transaction.Commit(); return result;
    }

    RecoveryRegistrySnapshot IRecoveryRegistry.ReadSnapshot() => ReadActiveSnapshot();
    internal RecoveryRegistrySnapshot ReadActiveSnapshot() => ReadSnapshot(null);

    internal RecoveryRegistrySnapshot ReadSnapshot(Guid? requiredJournalId) =>
        HistoryReadScope.ReadDatabase(database, (connection, transaction) => ReadSnapshot(connection, transaction, requiredJournalId));

    internal static RecoveryRegistrySnapshot ReadInspectedSnapshot(DatabaseInspection inspection)
    {
        var path = Path.Combine(inspection.DiagnosticCopyRoot, "nexus.db");
        using var connection = DatabaseConnections.Open(path);
        using var transaction = connection.BeginTransaction(deferred: true);
        var result = ReadSnapshot(connection, transaction);
        transaction.Commit();
        return result;
    }

    public RecoveryRegistrySnapshot PublishVerified(long expectedRevision, long minimumRevision = 0)
    {
        return database.Write((connection, transaction) =>
        {
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "UPDATE recovery_index_state SET ready=1,revision=max(revision+1,$minimum) WHERE singleton=1 AND revision=$expected;";
            command.Parameters.AddWithValue("$expected", expectedRevision);
            command.Parameters.AddWithValue("$minimum", minimumRevision);
            if (command.ExecuteNonQuery() != 1) throw new IOException("recovery_index_revision_conflict");
            return ReadSnapshot(connection, transaction);
        });
    }

    private static RecoveryRegistrySnapshot ReadSnapshot(SqliteConnection connection, SqliteTransaction transaction, Guid? requiredJournalId = null,
        bool includeHistory = false)
    {
        using var timing = StartupProfile.Measure("history.sql-snapshot");
        if (requiredJournalId is null) RecoveryCapacityStore.VerifyGlobal(connection, transaction);
        var journals = ReadJournals(connection, transaction, requiredJournalId, includeHistory);
        var effects = new List<FileEffect>();
        foreach (var journal in journals)
        {
            effects.AddRange(FileEffectRows.Read(connection, journal.JournalId, transaction));
            if (effects.Count > RecoveryCapacityBudget.MaximumFileEffects) throw new IOException("file_effect_limit");
        }
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT i.resource_key,i.journal_id,i.reason_code,i.scope_quality,COALESCE(c.mode,1) FROM " +
            (requiredJournalId is null ? "resource_isolations i " :
                "recovery_capacity_members m JOIN resource_isolations i ON i.journal_id=m.journal_id ") +
            "LEFT JOIN journal_resource_claims c ON c.journal_id=i.journal_id AND c.resource_key=i.resource_key " +
            (requiredJournalId is null ? "" : "WHERE m.owner_journal_id=(SELECT owner_journal_id FROM recovery_capacity_members WHERE journal_id=$required) ") +
            "ORDER BY i.resource_key,i.journal_id LIMIT 65537;";
        command.Parameters.AddWithValue("$required", requiredJournalId is { } required ? required.ToString("D") : DBNull.Value);
        var isolations = new List<ResourceIsolation>();
        using (var reader = command.ExecuteReader())
        {
            while (reader.Read())
            {
                if (reader.GetString(3) != "verified" || isolations.Count >= 65536) throw new IOException("recovery_registry_scope_unverified_or_limit");
                var mode = (ResourceClaimMode)reader.GetInt32(4);
                if (!Enum.IsDefined(mode)) throw new IOException("journal_resource_claim_mode_invalid");
                isolations.Add(new(reader.GetString(0), Guid.Parse(reader.GetString(1)), reader.GetString(2)) { Mode = mode });
            }
        }
        command.CommandText = "SELECT ready,revision FROM recovery_index_state WHERE singleton=1;";
        using var state = command.ExecuteReader();
        if (!state.Read()) throw new IOException("recovery_registry_state_missing");
        var result = new RecoveryRegistrySnapshot(state.GetInt64(1), state.GetInt64(0) == 1, journals, isolations.AsReadOnly(), effects);
        RecoveryRegistryPolicy.Verify(result);
        return result;
    }

    private static IReadOnlyList<RegisteredJournal> ReadJournals(SqliteConnection connection, SqliteTransaction transaction, Guid? requiredJournalId, bool includeHistory)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        const string columns = "SELECT j.journal_id,j.operation_id,j.operation_kind,j.phase,j.revision,j.plan_path,j.plan_digest ";
        command.CommandText = requiredJournalId is not null ? FamilyQuery : includeHistory
            ? columns + "FROM operation_journals j ORDER BY j.journal_id LIMIT 10001;"
            : columns + "FROM operation_journals j LEFT JOIN recovery_capacity_members m ON m.journal_id=j.journal_id " +
                "LEFT JOIN recovery_capacity_reservations r USING(owner_journal_id) WHERE r.owner_journal_id IS NULL OR j.phase<>'committed' OR " +
                RecoveryCapacityStore.ActiveReservation + " ORDER BY j.journal_id LIMIT 10001;";
        command.Parameters.AddWithValue("$required", requiredJournalId is { } required ? required.ToString("D") : DBNull.Value);
        var result = new List<RegisteredJournal>();
        using (var reader = command.ExecuteReader())
        {
            while (reader.Read())
            {
                if (result.Count >= 10000) throw new IOException("recovery_registry_journal_limit");
                result.Add(new(Guid.Parse(reader.GetString(0)), Guid.Parse(reader.GetString(1)), Enum.Parse<JournalKind>(reader.GetString(2), true),
                    Enum.Parse<JournalPhase>(reader.GetString(3), true), reader.GetInt64(4), reader.GetString(5), reader.GetString(6), []));
            }
        }
        if (requiredJournalId is { } familyJournal && !result.Any(item => item.JournalId == familyJournal))
            throw new IOException("recovery_family_unbound");
        command.CommandText = "SELECT resource_key FROM journal_resources WHERE journal_id=$id ORDER BY resource_key LIMIT 4097;";
        var id = command.Parameters.Add("$id", SqliteType.Text);
        for (var index = 0; index < result.Count; index++)
        {
            id.Value = result[index].JournalId.ToString("D");
            using var reader = command.ExecuteReader();
            var resources = new List<string>();
            while (reader.Read()) resources.Add(reader.GetString(0));
            if (resources.Count is 0 or > 4096) throw new IOException("recovery_registry_resource_limit");
            result[index] = result[index] with { ResourceKeys = resources.AsReadOnly() };
        }
        return result.AsReadOnly();
    }

    internal const string FamilyQuery = "SELECT j.journal_id,j.operation_id,j.operation_kind,j.phase,j.revision,j.plan_path,j.plan_digest " +
        "FROM recovery_capacity_members m JOIN operation_journals j ON j.journal_id=m.journal_id " +
        "WHERE m.owner_journal_id=(SELECT owner_journal_id FROM recovery_capacity_members WHERE journal_id=$required) " +
        "ORDER BY j.journal_id LIMIT 10001;";

    private static void IncrementIndex(SqliteConnection connection, SqliteTransaction transaction)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "UPDATE recovery_index_state SET ready=0,revision=revision+1 WHERE singleton=1;";
        if (command.ExecuteNonQuery() != 1) throw new IOException("recovery_registry_state_missing");
    }

    private static void Validate(RegisteredJournal journal)
    {
        if (journal.JournalId == Guid.Empty || journal.OperationId == Guid.Empty || !Enum.IsDefined(journal.Kind) || !Enum.IsDefined(journal.Phase) ||
            journal.Revision < 1 || journal.PlanDigest.Length != 64 || journal.PlanDigest.Any(character => character is not (>= '0' and <= '9' or >= 'a' and <= 'f')) ||
            journal.PlanPath != $"blobs/plans/{journal.PlanDigest}.json" || journal.ResourceKeys.Count is 0 or > 4096 ||
            journal.ResourceKeys.Any(key => string.IsNullOrWhiteSpace(key) || key.Length > 512) ||
            journal.ResourceKeys.Distinct(StringComparer.Ordinal).Count() != journal.ResourceKeys.Count)
            throw new ArgumentException("Invalid registered journal");
    }

    private static string Wire<T>(T value) where T : Enum
    {
        var text = value.ToString();
        return char.ToLowerInvariant(text[0]) + text[1..];
    }
}
