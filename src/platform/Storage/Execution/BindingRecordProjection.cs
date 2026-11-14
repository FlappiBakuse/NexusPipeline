using Microsoft.Data.Sqlite;
using NexusPipeline.Platform.Serialization;
using NexusPipeline.Platform.Storage.Sqlite;

namespace NexusPipeline.Platform.Storage.Execution;

internal static class BindingRecordProjection
{
    internal static IReadOnlyList<ExecutionStepView> Capture(RuntimeInstance instance, StateDatabase database,
        ContractReader contracts, Guid runId, Guid attemptId)
    {
        using var connection = DatabaseConnections.Open(database.DatabasePath, readOnly: true, domains: DatabaseDomain.Runtime | DatabaseDomain.Configuration | DatabaseDomain.Records | DatabaseDomain.Extensions | DatabaseDomain.System | DatabaseDomain.Content);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT EXISTS(SELECT 1 FROM records.run_records WHERE run_id=$run);";
        command.Parameters.AddWithValue("$run", runId.ToString("D"));
        if ((long)command.ExecuteScalar()! != 0) return [];
        var reports = new ExecutionLogReader(instance, database, contracts);
        var final = reports.ReadReport(runId, attemptId).Steps;
        var selected = final.Where(step => step.BindingId is not null).ToDictionary(step => step.StepId, StringComparer.Ordinal);
        if (selected.Values.All(step => step.Phase == "collected")) return selected.Values.ToArray();
        command.CommandText = "SELECT attempt_id FROM records.attempt_receipts WHERE run_id=$run AND ordinal < " +
            "(SELECT ordinal FROM records.attempt_receipts WHERE attempt_id=$attempt) ORDER BY ordinal DESC LIMIT 99;";
        command.Parameters.AddWithValue("$attempt", attemptId.ToString("D"));
        var earlier = new List<Guid>();
        using (var rows = command.ExecuteReader()) while (rows.Read()) earlier.Add(Guid.Parse(rows.GetString(0)));
        foreach (var previous in earlier)
        {
            foreach (var step in reports.ReadReport(runId, previous).Steps.Where(step => step.BindingId is not null && step.Phase == "collected"))
                if (selected.TryGetValue(step.StepId, out var current) && current.Phase != "collected" &&
                    step.UserId == current.UserId && step.ScriptId == current.ScriptId) selected[step.StepId] = step;
            if (selected.Values.All(step => step.Phase == "collected")) break;
        }
        return selected.Values.ToArray();
    }

    internal static void Publish(SqliteConnection connection, SqliteTransaction transaction, Guid runId,
        IReadOnlyList<ExecutionStepView> steps)
    {
        foreach (var group in steps.Where(step => step.BindingId is not null).GroupBy(step => step.BindingId))
        {
            var step = group.OrderBy(item => Rank(Tone(item))).ThenBy(item => item.StepId, StringComparer.Ordinal).First();
            using var command = connection.CreateCommand(); command.Transaction = transaction;
            command.CommandText = "INSERT INTO records.binding_task_records(run_id,binding_id,user_id,script_id,tone,reason) " +
                "VALUES($run,$binding,$user,$script,$tone,$reason);";
            command.Parameters.AddWithValue("$run", runId.ToString("D"));
            command.Parameters.AddWithValue("$binding", Guid.Parse(step.BindingId!).ToString("D"));
            command.Parameters.AddWithValue("$user", Guid.Parse(step.UserId!).ToString("D"));
            command.Parameters.AddWithValue("$script", Guid.Parse(step.ScriptId).ToString("D"));
            command.Parameters.AddWithValue("$tone", Tone(step));
            command.Parameters.AddWithValue("$reason", step.Phase == "collected" && step.Tasks.Count > 0 ? "latest" : "no_task_report");
            command.ExecuteNonQuery();
        }
    }

    private static string Tone(ExecutionStepView step) => step.Business switch
    {
        "Failed" => "bad", "Partial" => "warn", "Succeeded" => "ok", _ => "muted"
    };
    private static int Rank(string tone) => tone switch { "bad" => 0, "warn" => 1, "muted" => 2, _ => 3 };
}
