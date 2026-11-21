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
            for (var attempt = 0; attempt < 8; attempt++)
            {
                var projection = index.Current;
                if (index.TryUpdateHolds(RecoveryHoldOwner.ConfigurationOperations, projection.Revision,
                        Read(database, payloadRoots, projection))) return;
            }
            throw new IOException("configuration_holds_publication_conflict");
        }
        catch { index.BlockUnknown("configuration_holds_reconciliation_required"); throw; }
    }

    private static IReadOnlyList<RecoveryResourceHold> Read(StateDatabase database, PayloadApplyRoots? payloadRoots,
        RecoveryProjection projection)
    {
        using var connection = DatabaseConnections.Open(database.DatabasePath, readOnly: true, domains: DatabaseDomain.Runtime | DatabaseDomain.Configuration | DatabaseDomain.Extensions | DatabaseDomain.System);
        using var transaction = DatabaseConnections.BeginRead(connection);
        var groups = new List<(Guid Session, Guid Owner, Guid? Parent)>();
        using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = "SELECT session_id,session_id,NULL FROM configuration.configuration_edit_sessions WHERE phase IN (0,1) " +
                "UNION ALL SELECT session_id,owner_operation_id,parent_journal_id FROM configuration.configuration_run_sessions WHERE phase IN (0,1) LIMIT 4097;";
            using var rows = command.ExecuteReader();
            while (rows.Read()) groups.Add((rows.GetGuid(0), rows.GetGuid(1), rows.IsDBNull(2) ? null : rows.GetGuid(2)));
        }
        if (groups.Count > 4096) throw new IOException("configuration_hold_inventory_limit");
        var holds = new Dictionary<(Guid Owner, string Key), RecoveryResourceHold>();
        var registered = new List<RegisteredJournal>();
        var claimsByJournal = new Dictionary<Guid, Dictionary<string, ResourceClaimMode>>();
        var scoped = projection.Isolations.Where(item => item.CanOrdinaryExit)
            .ToLookup(item => item.JournalId, item => item.ResourceKey);
        foreach (var group in groups)
        {
            var run = group.Parent is not null;
            var journals = Members(connection, transaction, group.Session, run);
            var narrowed = group.Parent is { } parent && scoped.Contains(parent);
            var keys = narrowed ? scoped[group.Parent!.Value].ToHashSet(StringComparer.Ordinal) : null;
            foreach (var journalId in journals)
            {
                var (journal, claims) = RecoveryJournalRegistry.ReadWithClaimsInside(connection, transaction, journalId);
                registered.Add(journal);
                claimsByJournal[journalId] = claims;
                if ((run || claims.Count > 0) && claims.Count != journal.ResourceKeys.Count)
                    throw new IOException("configuration_hold_claims_unbound");
                foreach (var resource in journal.ResourceKeys)
                {
                    if (keys is not null && !keys.Contains(resource)) continue;
                    var mode = claims.GetValueOrDefault(resource, ResourceClaimMode.Mutation);
                    var key = (group.Owner, resource);
                    if (holds.TryGetValue(key, out var prior) && prior.Mode >= mode) continue;
                    holds[key] = new(group.Owner, resource, "configuration_operation_pending")
                        { Mode = mode, CanOrdinaryExit = narrowed };
                }
            }
        }
        if (registered.Count > 0)
        {
            var instance = new InstanceRoot(new()).OpenExisting(database.RuntimeRoot);
            JournalResourceClaims.VerifyScope(instance, database, new(), registered.DistinctBy(item => item.JournalId), payloadRoots, claimsByJournal);
        }
        return holds.Values.ToArray();
    }

    private static IReadOnlyList<Guid> Members(SqliteConnection connection, SqliteTransaction transaction, Guid session, bool run)
    {
        using var command = connection.CreateCommand(); command.Transaction = transaction;
        command.CommandText = run ? "SELECT r.journal_id,j.operation_id=r.operation_id FROM configuration.configuration_run_resources r LEFT JOIN main.operation_journals j " +
            "ON j.journal_id=r.journal_id WHERE r.session_id=$session LIMIT 33;" :
            "SELECT r.journal_id,j.operation_id=r.session_id FROM configuration.configuration_edit_resources r LEFT JOIN main.operation_journals j " +
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
