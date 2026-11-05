using NexusPipeline.Core.Configuration;
using NexusPipeline.Core.Maintenance;
using NexusPipeline.Platform.Storage.Sqlite;

namespace NexusPipeline.Platform.Storage.Execution;

internal static class LiveHookEvidence
{
    internal static void Require(StateDatabase database, ResourceLeaseManager resources, RecoveryIndex index,
        AcceptedExecutionAdmission accepted, AttemptWriterState state)
    {
        if (!resources.UsesRecoveryIndex(index) || state.Journal.JournalId != accepted.Receipt.JournalId ||
            state.Journal.Phase != JournalPhase.Applying || state.Latest.Revision != state.Journal.Revision)
            throw Invalid();
        var current = index.Current;
        if (!current.IsReady) throw Invalid();
        var claims = ExecutionPlanResources.Claims(state.Total);
        var journals = new HashSet<Guid> { state.Journal.JournalId };
        var operations = new HashSet<Guid> { state.Journal.OperationId };
        foreach (var configuration in accepted.Prepared.Configurations.Values)
            foreach (var resource in configuration.Request.Resources)
            { journals.Add(resource.JournalId); operations.Add(resource.OperationId); }
        using (var connection = DatabaseConnections.Open(database.DatabasePath, readOnly: true))
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT r.journal_id,r.operation_id FROM configuration_run_resources r JOIN configuration_run_sessions s USING(session_id) WHERE s.parent_journal_id=$parent;";
            command.Parameters.AddWithValue("$parent", state.Journal.JournalId.ToString("D"));
            using var rows = command.ExecuteReader();
            while (rows.Read()) { journals.Add(Guid.Parse(rows.GetString(0))); operations.Add(Guid.Parse(rows.GetString(1))); }
        }
        var registered = new RecoveryJournalRegistry(database).ReadActiveSnapshot();
        foreach (var journal in registered.Journals.Where(item => journals.Contains(item.JournalId)))
            if (journal.Phase == JournalPhase.Quarantined) throw Invalid();
        if (current.Isolations.Concat(registered.Isolations).Any(item => Conflicts(item.ResourceKey, item.Mode) &&
                (!journals.Contains(item.JournalId) || item.ReasonCode != "recovery_pending")) ||
            current.ResourceHolds.Any(item => Conflicts(item.ResourceKey, item.Mode) &&
                (item.Owner != RecoveryHoldOwner.ConfigurationOperations || !operations.Contains(item.OperationId))) ||
            !ReferenceEquals(current, index.Current))
            throw Invalid();

        bool Conflicts(string key, ResourceClaimMode mode) => claims.Any(claim => claim.ResourceKey == key &&
            (claim.Mode == ResourceClaimMode.Mutation || mode == ResourceClaimMode.Mutation));
    }

    private static IOException Invalid() => new("execution_retry_hook_live_evidence_unverified");
}
