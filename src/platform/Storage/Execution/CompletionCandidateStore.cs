using System.Security.Cryptography;
using System.Text;
using NexusPipeline.Core.Execution;
using NexusPipeline.Core.Operations;
using NexusPipeline.Platform.Storage.Sqlite;

namespace NexusPipeline.Platform.Storage.Execution;

internal sealed class CompletionCandidateStore(StateDatabase database, InstanceLease lease, CompletionIntentSource source,
    Guid generation, TimeProvider clock)
{
    internal BoundCompletionIntent? Accept(AttemptReceipt receipt)
    {
        var bound = source.Capture(receipt);
        if (bound is null) return null;
        var intentJson = CompletionActionCodec.Encode(bound.Intent);
        var identity = Identity(bound); var operationId = identity.OperationId;
        return lease.PublishExecution(() => database.Write((connection, transaction) =>
        {
            using var command = connection.CreateCommand(); command.Transaction = transaction;
            command.CommandText = "SELECT intent_json,total_plan_path,total_plan_size,total_plan_sha256,subject_id FROM completion_candidates WHERE run_id=$run;";
            command.Parameters.AddWithValue("$run", bound.Intent.RunId.ToString("D"));
            using (var prior = command.ExecuteReader())
            {
                if (prior.Read())
                {
                    if (prior.GetString(0) != intentJson || prior.GetString(1) != bound.TotalPlan.RelativePath ||
                        prior.GetInt64(2) != bound.TotalPlan.SizeBytes || prior.GetString(3) != bound.TotalPlan.Sha256 || prior.GetString(4) != bound.SubjectId)
                        throw CompletionActionCodec.Invalid();
                    return bound;
                }
            }
            command.CommandText = "SELECT COUNT(*) FROM completion_candidates c WHERE NOT EXISTS(SELECT 1 FROM completion_candidate_outcomes o WHERE o.run_id=c.run_id) AND NOT EXISTS(SELECT 1 FROM completion_action_members m WHERE m.run_id=c.run_id);";
            if ((long)command.ExecuteScalar()! >= 4096) throw new IOException("completion_candidate_capacity");
            if (!OperationRows.Accept(connection, transaction, identity, clock.GetUtcNow()).IsNew) throw CompletionActionCodec.Invalid();
            command.CommandText = "INSERT INTO completion_candidates VALUES($run,$operation,$attempt,$subject,$intent,$path,$size,$digest,$generation);";
            command.Parameters.AddWithValue("$operation", operationId.ToString("D"));
            command.Parameters.AddWithValue("$attempt", bound.Intent.AttemptId.ToString("D"));
            command.Parameters.AddWithValue("$subject", bound.SubjectId);
            command.Parameters.AddWithValue("$intent", intentJson);
            command.Parameters.AddWithValue("$path", bound.TotalPlan.RelativePath);
            command.Parameters.AddWithValue("$size", bound.TotalPlan.SizeBytes);
            command.Parameters.AddWithValue("$digest", bound.TotalPlan.Sha256);
            command.Parameters.AddWithValue("$generation", generation.ToString("D")); command.ExecuteNonQuery();
            command.CommandText = "UPDATE operations SET status='completed' WHERE operation_id=$operation AND status='accepted';";
            if (command.ExecuteNonQuery() != 1) throw CompletionActionCodec.Invalid();
            return bound;
        }));
    }

    internal static OperationIdentity Identity(BoundCompletionIntent bound)
    {
        var material = Encoding.UTF8.GetBytes("nexus.completion.candidate/1:" + bound.Intent.RunId.ToString("D"));
        return new(new Guid(SHA256.HashData(material).AsSpan(0, 16)), bound.SubjectId,
            CompletionActionCodec.Digest(CompletionActionCodec.Encode(bound)), "execution.completion.candidate");
    }

    internal IReadOnlyList<BoundCompletionIntent> ReadPending()
    {
        lease.VerifyHeld(lease.Instance);
        using var connection = DatabaseConnections.Open(database.DatabasePath, readOnly: true);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT c.intent_json,c.total_plan_path,c.total_plan_size,c.total_plan_sha256,c.subject_id FROM completion_candidates c WHERE c.owner_generation=$generation AND NOT EXISTS(SELECT 1 FROM completion_candidate_outcomes o WHERE o.run_id=c.run_id) AND NOT EXISTS(SELECT 1 FROM completion_action_members m WHERE m.run_id=c.run_id) ORDER BY c.run_id LIMIT 4097;";
        command.Parameters.AddWithValue("$generation", generation.ToString("D"));
        var values = new List<BoundCompletionIntent>();
        using (var reader = command.ExecuteReader())
        {
            while (reader.Read())
            {
                if (values.Count == 4096) throw CompletionActionCodec.Invalid();
                values.Add(new(CompletionActionCodec.Decode<FrozenCompletionIntent>(reader.GetString(0)),
                    new(reader.GetString(1), reader.GetInt64(2), reader.GetString(3)), reader.GetString(4)));
            }
        }
        foreach (var value in values) source.Require(value);
        return values.AsReadOnly();
    }

    internal bool HasPending()
    {
        lease.VerifyHeld(lease.Instance);
        using var connection = DatabaseConnections.Open(database.DatabasePath, readOnly: true);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT EXISTS(SELECT 1 FROM completion_candidates c WHERE c.owner_generation=$generation AND NOT EXISTS(SELECT 1 FROM completion_candidate_outcomes o WHERE o.run_id=c.run_id) AND NOT EXISTS(SELECT 1 FROM completion_action_members m WHERE m.run_id=c.run_id));";
        command.Parameters.AddWithValue("$generation", generation.ToString("D")); return (long)command.ExecuteScalar()! == 1;
    }

    internal void Reject(IReadOnlyList<BoundCompletionIntent> candidates, string reason)
    {
        if (reason is not ("completion_action_conflict" or "completion_cancelled" or "completion_local_authorization_required" or "completion_lifecycle_unavailable"))
            throw new ArgumentException("completion_rejection_invalid");
        if (candidates.Count is < 1 or > 4096) throw new ArgumentException("completion_candidate_limit");
        lease.PublishExecution(() => database.Write((connection, transaction) =>
        {
            using var command = connection.CreateCommand(); command.Transaction = transaction;
            command.CommandText = "INSERT INTO completion_candidate_outcomes SELECT run_id,$reason FROM completion_candidates WHERE run_id=$run AND owner_generation=$generation AND NOT EXISTS(SELECT 1 FROM completion_action_members m WHERE m.run_id=completion_candidates.run_id);";
            command.Parameters.AddWithValue("$reason", reason); command.Parameters.AddWithValue("$generation", generation.ToString("D"));
            foreach (var item in candidates)
            {
                command.Parameters.AddWithValue("$run", item.Intent.RunId.ToString("D"));
                if (command.ExecuteNonQuery() != 1) throw CompletionActionCodec.Invalid();
                command.Parameters.RemoveAt("$run");
            }
            return true;
        }));
    }

    internal void RetirePriorCandidates()
    {
        lease.PublishExecution(() => database.Write((connection, transaction) =>
        {
            using var command = connection.CreateCommand(); command.Transaction = transaction;
            command.CommandText = "INSERT INTO completion_candidate_outcomes SELECT run_id,'completion_owner_restarted' FROM completion_candidates c WHERE c.owner_generation!=$generation AND NOT EXISTS(SELECT 1 FROM completion_candidate_outcomes o WHERE o.run_id=c.run_id) AND NOT EXISTS(SELECT 1 FROM completion_action_members m WHERE m.run_id=c.run_id);";
            command.Parameters.AddWithValue("$generation", generation.ToString("D")); return command.ExecuteNonQuery();
        }));
    }
}
