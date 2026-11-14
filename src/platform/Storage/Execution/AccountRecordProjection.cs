using Microsoft.Data.Sqlite;
using NexusPipeline.Core.Execution;
using NexusPipeline.Core.Records;

namespace NexusPipeline.Platform.Storage.Execution;

internal static class AccountRecordProjection
{
    internal static void Publish(SqliteConnection connection, SqliteTransaction transaction,
        CompletedRunRecord run, IReadOnlyList<ExecutionStepView> steps)
    {
        foreach (var account in steps.Where(step => step.UserId is not null).GroupBy(step => step.UserId))
        {
            var facts = Facts(run.FinalAttempt, account);
            var engine = facts.Engine; var business = facts.Business; var lifecycle = facts.Lifecycle;
            (run with { UserId = Guid.Parse(account.Key!), FinalAttempt = facts }).Validate();
            using var command = connection.CreateCommand(); command.Transaction = transaction;
            command.CommandText = "INSERT INTO records.account_run_records(ended_at_utc_ticks,run_id,user_id,engine_outcome,business_outcome,lifecycle_outcome,reason_code) " +
                "VALUES($ended,$run,$user,$engine,$business,$lifecycle,$reason);";
            command.Parameters.AddWithValue("$ended", run.EndedAt.UtcTicks);
            command.Parameters.AddWithValue("$run", run.FinalAttempt.RunId.ToString("D"));
            command.Parameters.AddWithValue("$user", account.Key!);
            command.Parameters.AddWithValue("$engine", (int)engine);
            command.Parameters.AddWithValue("$business", (int)business);
            command.Parameters.AddWithValue("$lifecycle", (int)lifecycle);
            command.Parameters.AddWithValue("$reason", facts.ReasonCode);
            command.ExecuteNonQuery();
        }
    }

    internal static AttemptFacts Facts(AttemptFacts run, IEnumerable<ExecutionStepView> steps)
    {
        var selected = steps.OrderBy(step => Rank(step.Business)).ThenBy(step => step.StepId, StringComparer.Ordinal).First();
        var collected = selected.Phase == "collected";
        var engine = collected && Enum.TryParse<EngineOutcome>(selected.Engine, out var rawEngine) ? rawEngine : EngineOutcome.NotStarted;
        var business = collected && Enum.TryParse<BusinessOutcome>(selected.Business, out var rawBusiness) ? rawBusiness : BusinessOutcome.Unverified;
        var lifecycle = business == BusinessOutcome.Cancelled || business == BusinessOutcome.Unverified && run.Lifecycle == LifecycleOutcome.Cancelled
            ? LifecycleOutcome.Cancelled : business == BusinessOutcome.Unverified && run.Lifecycle == LifecycleOutcome.Interrupted ? LifecycleOutcome.Interrupted :
                collected ? LifecycleOutcome.Completed : LifecycleOutcome.Skipped;
        return run with { Engine = engine, Business = business, Lifecycle = lifecycle,
            ReasonCode = lifecycle is LifecycleOutcome.Cancelled or LifecycleOutcome.Interrupted ? run.ReasonCode : selected.ReasonCode ?? run.ReasonCode };
    }

    private static int Rank(string? business) => business switch
    { "Failed" => 0, "Partial" => 1, "Cancelled" => 2, null or "Unverified" => 3, "Succeeded" => 5, _ => 4 };
}
