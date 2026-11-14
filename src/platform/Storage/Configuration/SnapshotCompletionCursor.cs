using Microsoft.Data.Sqlite;
using NexusPipeline.Core.Configuration;
using NexusPipeline.Core.Operations;

namespace NexusPipeline.Platform.Storage.Configuration;

internal static class SnapshotCompletionCursor
{
    internal static void Require(SqliteConnection connection, SqliteTransaction transaction, OperationIdentity operation,
        ConfigurationRunStatus session, ConfigurationRunPhase? terminal, SnapshotCompletionProjection completion)
    {
        if (completion.CompletionOperationId != operation.OperationId || completion.SubjectId != operation.SubjectId ||
            completion.SessionId != session.SessionId || completion.ParentOperationId != session.OwnerOperationId ||
            completion.ParentJournalId != session.ParentJournalId || completion.GroupRevision != session.Revision ||
            completion.Terminal != terminal || !completion.SlotKeys.SequenceEqual(session.Resources.Select(item =>
                ConfigurationRevisionService.ResourceKey(item.Address)).Order(StringComparer.Ordinal)))
            throw new IOException("configuration_snapshot_completion_evidence_unbound");
        using var command = connection.CreateCommand(); command.Transaction = transaction;
        command.CommandText = "SELECT EXISTS(SELECT 1 FROM main.operation_journals j JOIN main.journal_effect_gates g USING(journal_id) " +
            "JOIN main.operations o ON o.operation_id=j.operation_id WHERE j.journal_id=$parent AND j.operation_id=$owner " +
            "AND j.operation_kind='runAttempt' AND j.revision=$revision AND j.plan_digest=$digest " +
            "AND ((j.phase='prepared' AND g.state='sealed') OR (j.phase='applied' AND g.state='entered')) " +
            "AND g.revision=2 AND o.subject_id=$subject AND o.status='accepted');";
        command.Parameters.AddWithValue("$parent", completion.ParentJournalId.ToString("D"));
        command.Parameters.AddWithValue("$owner", completion.ParentOperationId.ToString("D"));
        command.Parameters.AddWithValue("$revision", completion.ParentRevision); command.Parameters.AddWithValue("$digest", completion.ParentPlanDigest);
        command.Parameters.AddWithValue("$subject", completion.SubjectId);
        if ((long)command.ExecuteScalar()! != 1) throw new IOException("configuration_snapshot_completion_parent_changed");
        foreach (var resource in session.Resources)
        {
            command.CommandText = "SELECT EXISTS(SELECT 1 FROM main.operation_journals WHERE journal_id=$child AND operation_id=$operation " +
                "AND operation_kind='configExchange' AND phase='committed' AND plan_path=$path AND plan_digest=$plan);";
            command.Parameters.AddWithValue("$child", resource.JournalId.ToString("D")); command.Parameters.AddWithValue("$operation", resource.OperationId.ToString("D"));
            command.Parameters.AddWithValue("$path", resource.PlanPath); command.Parameters.AddWithValue("$plan", resource.PlanDigest);
            if ((long)command.ExecuteScalar()! != 1) throw new IOException("configuration_snapshot_completion_child_changed");
            foreach (var key in new[] { "$child", "$operation", "$path", "$plan" }) command.Parameters.RemoveAt(key);
        }
    }
}
