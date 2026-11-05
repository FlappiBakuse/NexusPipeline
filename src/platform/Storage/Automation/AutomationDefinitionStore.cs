using System.Globalization;
using System.Collections.Immutable;
using System.Security.Cryptography;
using Microsoft.Data.Sqlite;
using NexusPipeline.Core.Automation;
using NexusPipeline.Core.Operations;
using NexusPipeline.Platform.Serialization;
using NexusPipeline.Platform.Storage.Sqlite;

namespace NexusPipeline.Platform.Storage.Automation;

public sealed class AutomationDefinitionStore(StateDatabase database) : IAutomationDefinitionStore
{
    public static OperationIdentity Identity(Guid operationId, string subjectId, AutomationChanges changes) =>
        new(operationId, subjectId, Digest(changes), "automation.change");

    public AutomationCatalog Read()
    {
        using var connection = DatabaseConnections.Open(database.DatabasePath);
        using var transaction = connection.BeginTransaction(deferred: true);
        var result = AutomationRows.Read(connection, transaction);
        VerifyRoots(result.Roots);
        transaction.Commit();
        return result;
    }

    // The source lease re-checks its definitions on every read, so it needs the revision without materializing the catalog.
    internal long ReadRevision()
    {
        using var connection = DatabaseConnections.Open(database.DatabasePath, readOnly: true);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT revision FROM automation_catalog_state WHERE singleton=1;";
        return Convert.ToInt64(command.ExecuteScalar() ?? throw new IOException("automation_catalog_revision_missing"), CultureInfo.InvariantCulture);
    }

    public AutomationChangeResult Commit(OperationIdentity operation, AutomationChanges changes, DateTimeOffset acceptedAt)
    {
        if (operation.Kind != "automation.change" || operation.RequestDigest != Digest(changes)) throw new OperationConflictException();
        return CommitChanges(operation, changes, acceptedAt);
    }

    internal AutomationChangeResult? Lookup(OperationIdentity operation, AutomationChanges changes)
    {
        if (operation.Kind != "automation.change" || operation.RequestDigest != Digest(changes)) throw new OperationConflictException();
        using var connection = DatabaseConnections.Open(database.DatabasePath, readOnly: true);
        using var transaction = connection.BeginTransaction(deferred: true);
        return OperationRows.ReadExisting(connection, transaction, operation) is null ? null : ReadResult(connection, transaction, operation.OperationId);
    }

    internal AutomationChangeResult CommitImport(OperationIdentity operation, AutomationChanges changes, DateTimeOffset acceptedAt)
    {
        if (operation.Kind != "automation.import" || !changes.Removed.IsEmpty) throw new OperationConflictException();
        return CommitChanges(operation, changes, acceptedAt);
    }

    private AutomationChangeResult CommitChanges(OperationIdentity operation, AutomationChanges changes, DateTimeOffset acceptedAt)
    {
        VerifySavedRoots(changes);
        using (var connection = DatabaseConnections.Open(database.DatabasePath, readOnly: true))
        using (var transaction = connection.BeginTransaction(deferred: true))
            if (OperationRows.ReadExisting(connection, transaction, operation) is not null) return ReadResult(connection, transaction, operation.OperationId);
        var current = Read();
        _ = AutomationEdits.Apply(current, changes);
        foreach (var schedule in changes.Schedules)
        {
            try { _ = TimeZoneInfo.FindSystemTimeZoneById(schedule.TimeZoneId); }
            catch (TimeZoneNotFoundException) { throw new AutomationDefinitionException("automation_time_zone_unavailable"); }
            catch (InvalidTimeZoneException) { throw new AutomationDefinitionException("automation_time_zone_unavailable"); }
        }
        using var notificationSelections = Notifications.PreparedNotificationSelections.Capture(database);
        notificationSelections.RequireChanges(current, changes);
        using var retirement = AutomationAssetRetirement.Prepare(database, operation, changes);
        var result = database.Write((connection, transaction) =>
        {
            var accepted = OperationRows.Accept(connection, transaction, operation, acceptedAt);
            if (!accepted.IsNew) return ReadResult(connection, transaction, operation.OperationId);
            var before = AutomationRows.Read(connection, transaction);
            VerifyRoots(before.Roots);
            var next = AutomationEdits.Apply(before, changes);
            notificationSelections.RequireChangesInside(connection, transaction, before, changes);
            _ = BoundedJson.Serialize(next);
            retirement?.RetireOwners(connection, transaction);
            AutomationRowWriter.Write(connection, transaction, before, next);
            _ = AutomationRows.Read(connection, transaction);
            AutomationRowWriter.Row(connection, transaction, "INSERT INTO automation_mutations VALUES($0,$1);", operation.OperationId, next.Revision);
            AutomationRowWriter.Row(connection, transaction, "UPDATE operations SET status='completed' WHERE operation_id=$0;", operation.OperationId);
            return ReadResult(connection, transaction, operation.OperationId) with { IsNew = true };
        });
        if (result.IsNew) retirement?.MaterializeInitials();
        return result;
    }

    internal static AutomationChangeResult ReadResult(SqliteConnection connection, SqliteTransaction transaction, Guid operationId)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT catalog_revision FROM automation_mutations WHERE operation_id=$id;";
        command.Parameters.AddWithValue("$id", operationId.ToString("D"));
        var revision = command.ExecuteScalar();
        if (revision is null) throw new IOException("automation_operation_requires_reconciliation");
        command.CommandText = "SELECT journal_id FROM operation_journals WHERE operation_id=$id AND operation_kind='fileCleanup' ORDER BY journal_id LIMIT 257;";
        using var reader = command.ExecuteReader();
        var journals = ImmutableArray.CreateBuilder<Guid>();
        while (reader.Read())
        {
            if (journals.Count == 256) throw new IOException("automation_cleanup_journal_limit");
            journals.Add(Guid.Parse(reader.GetString(0)));
        }
        return new(operationId, Convert.ToInt64(revision, CultureInfo.InvariantCulture), false) { CleanupJournalIds = journals.ToImmutable() };
    }

    private void VerifySavedRoots(AutomationChanges changes)
    {
        if (changes.Roots.IsDefault) throw new AutomationDefinitionException("automation_changes_limit");
        VerifyRoots(changes.Roots);
    }

    private void VerifyRoots(IEnumerable<AutomationRoot> roots)
    {
        foreach (var root in roots)
        {
            if (root is null || PhysicalPathPolicy.NormalizeSyntax(root.Path) != root.Path ||
                Within(root.Path, database.RuntimeRoot) || Within(database.RuntimeRoot, root.Path))
                throw new AutomationDefinitionException("automation_root_overlaps_runtime");
        }
    }

    private static bool Within(string parent, string child)
    {
        var relative = Path.GetRelativePath(parent, child);
        return relative != ".." && !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal) && !Path.IsPathRooted(relative);
    }

    private static string Digest(AutomationChanges changes) => Convert.ToHexStringLower(SHA256.HashData(BoundedJson.Serialize(changes)));
}

