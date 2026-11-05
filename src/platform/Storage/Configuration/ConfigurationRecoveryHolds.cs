using Microsoft.Data.Sqlite;
using NexusPipeline.Platform.Storage.Journals;
using NexusPipeline.Core.Configuration;
using NexusPipeline.Core.Maintenance;
using NexusPipeline.Platform.Storage.Sqlite;

namespace NexusPipeline.Platform.Storage.Configuration;

public static class ConfigurationRecoveryHolds
{
    public static void Publish(StateDatabase database, RecoveryIndex index, PayloadApplyRoots? payloadRoots = null)
    {
        try
        {
            var instance = new InstanceRoot(new()).OpenExisting(database.RuntimeRoot);
            JournalResourceClaims.VerifyInventory(instance, database, new(), payloadRoots);
            for (var attempt = 0; attempt < 8; attempt++)
            {
                var revision = index.Current.Revision;
                if (index.TryUpdateHolds(RecoveryHoldOwner.ConfigurationOperations, revision, Read(database))) return;
            }
            throw new IOException("configuration_holds_publication_conflict");
        }
        catch { index.BlockUnknown("configuration_holds_reconciliation_required"); throw; }
    }

    private static IReadOnlyList<RecoveryResourceHold> Read(StateDatabase database)
    {
        using var connection = DatabaseConnections.Open(database.DatabasePath, readOnly: true);
        using var transaction = connection.BeginTransaction(deferred: true);
        var groups = new List<(Guid Session, Guid Owner, bool Run)>();
        using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = "SELECT session_id,session_id,0 FROM configuration_edit_sessions WHERE phase IN (0,1) " +
                "UNION ALL SELECT session_id,owner_operation_id,1 FROM configuration_run_sessions WHERE phase IN (0,1) LIMIT 4097;";
            using var rows = command.ExecuteReader();
            while (rows.Read()) groups.Add((Guid.Parse(rows.GetString(0)), Guid.Parse(rows.GetString(1)), rows.GetBoolean(2)));
        }
        if (groups.Count > 4096) throw new IOException("configuration_hold_inventory_limit");
        var holds = new Dictionary<(Guid Owner, string Key), RecoveryResourceHold>();
        foreach (var group in groups)
        {
            var journals = Members(connection, transaction, group.Session, group.Run);
            foreach (var journal in journals)
            {
                using var command = connection.CreateCommand(); command.Transaction = transaction;
                command.CommandText = "SELECT r.resource_key,c.mode FROM journal_resources r LEFT JOIN journal_resource_claims c " +
                    "ON c.journal_id=r.journal_id AND c.resource_key=r.resource_key WHERE r.journal_id=$journal ORDER BY r.resource_key LIMIT 4097;";
                command.Parameters.AddWithValue("$journal", journal.ToString("D"));
                var claims = new List<(string Key, ResourceClaimMode? Mode)>();
                using (var rows = command.ExecuteReader())
                    while (rows.Read()) claims.Add((rows.GetString(0), rows.IsDBNull(1) ? null : (ResourceClaimMode)rows.GetInt32(1)));
                if (claims.Count is < 1 or > 4096 || claims.Any(item => item.Mode.HasValue && !Enum.IsDefined(item.Mode.Value)) ||
                    (group.Run || claims.Any(item => item.Mode.HasValue)) && claims.Any(item => !item.Mode.HasValue))
                    throw new IOException("configuration_hold_claims_unbound");
                foreach (var claim in claims)
                {
                    var mode = claim.Mode ?? ResourceClaimMode.Mutation;
                    var key = (group.Owner, claim.Key);
                    if (holds.TryGetValue(key, out var prior) && prior.Mode >= mode) continue;
                    holds[key] = new(group.Owner, claim.Key, "configuration_operation_pending") { Mode = mode };
                }
            }
        }
        return holds.Values.ToArray();
    }

    private static IReadOnlyList<Guid> Members(SqliteConnection connection, SqliteTransaction transaction, Guid session, bool run)
    {
        using var command = connection.CreateCommand(); command.Transaction = transaction;
        command.CommandText = run ? "SELECT r.journal_id,j.operation_id=r.operation_id FROM configuration_run_resources r LEFT JOIN operation_journals j " +
            "ON j.journal_id=r.journal_id WHERE r.session_id=$session LIMIT 33;" :
            "SELECT r.journal_id,j.operation_id=r.session_id FROM configuration_edit_resources r LEFT JOIN operation_journals j " +
            "ON j.journal_id=r.journal_id WHERE r.session_id=$session LIMIT 33;";
        command.Parameters.AddWithValue("$session", session.ToString("D"));
        var result = new List<Guid>(); using var rows = command.ExecuteReader();
        while (rows.Read())
        {
            if (rows.IsDBNull(1) || !rows.GetBoolean(1)) throw new IOException("configuration_hold_journal_unbound");
            result.Add(Guid.Parse(rows.GetString(0)));
        }
        if (result.Count is < 1 or > 32) throw new IOException("configuration_hold_membership_invalid");
        return result;
    }
}
