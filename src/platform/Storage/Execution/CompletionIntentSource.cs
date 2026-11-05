using NexusPipeline.Core.Automation;
using NexusPipeline.Core.Execution;
using NexusPipeline.Platform.Serialization;
using NexusPipeline.Platform.Storage.Journals;
using NexusPipeline.Platform.Storage.Sqlite;

namespace NexusPipeline.Platform.Storage.Execution;

internal sealed record BoundCompletionIntent(FrozenCompletionIntent Intent, BlobReference TotalPlan, string SubjectId);

internal sealed class CompletionIntentSource(RuntimeInstance instance, StateDatabase database, ContractReader contracts,
    InstanceLease lease, ExecutionRunFinality finality)
{
    internal BoundCompletionIntent? Capture(AttemptReceipt receipt)
    {
        lease.VerifyHeld(instance);
        return ReadSaved(instance, database, contracts, finality, receipt);
    }

    internal static BoundCompletionIntent? ReadSaved(RuntimeInstance instance, StateDatabase database, ContractReader contracts,
        ExecutionRunFinality finality, AttemptReceipt receipt)
    {
        if (new AttemptReceiptStore(database).Read(receipt.Facts.AttemptId) != receipt) throw CompletionActionCodec.Invalid();
        var final = finality.ReadDecision(receipt);
        if (!final.IsFinal || final.DeclineReasonCode is not (null or "retry_budget_exhausted")) return null;
        var bound = AttemptPreparedBinding.Read(instance, contracts, database, receipt.JournalId);
        var journal = new JournalStore(instance, contracts).Read(receipt.JournalId);
        if (AttemptResultCodec.Receipt(bound.Plan, journal, receipt.Facts) != receipt) throw CompletionActionCodec.Invalid();
        var reference = AttemptPlanCodec.Reference(bound.Plan.ExecutionPlan);
        var plan = new ExecutionPlanCodec(instance, contracts, new(instance, contracts)).Read(reference).ReadWire();
        if (plan.RunId != receipt.Facts.RunId.ToString("D")) throw CompletionActionCodec.Invalid();
        if (plan.CompletionAction == "none") return null;
        if (plan.QueueId is not { } queueId) throw CompletionActionCodec.Invalid();
        var queue = plan.SourceRevisions.SingleOrDefault(value => value.EntityKind == "queue" && value.EntityId == queueId)
            ?? throw CompletionActionCodec.Invalid();
        var action = plan.CompletionAction switch
        {
            "shutdown" => AutomationCompletionAction.Shutdown,
            "hibernate" => AutomationCompletionAction.Hibernate,
            "restart" => AutomationCompletionAction.Restart,
            "exitHost" => AutomationCompletionAction.Exit,
            _ => throw CompletionActionCodec.Invalid()
        };
        if (plan.Selection.Any(item => item.Disposition == "skipped" && item.ReasonCode == "execution_queue_dependency_not_successful") ||
            BindingSkipCodec.Read(journal.Root, plan).Any(item => item.ReasonCode == "execution_queue_dependency_not_successful")) return null;
        var intent = new FrozenCompletionIntent(receipt.Facts.RunId, receipt.Facts.AttemptId, Guid.Parse(queueId), queue.Revision,
            action, receipt.PlanDigest, receipt.SourceReceiptDigest);
        if (!CompletionActionPolicy.Evaluate(intent, receipt).IsEligible) return null;
        using var connection = DatabaseConnections.Open(database.DatabasePath, readOnly: true);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT subject_id FROM operations WHERE operation_id=$operation AND operation_kind='execution.start' AND status='completed' AND initial_journal_id=$journal;";
        command.Parameters.AddWithValue("$operation", receipt.OperationId.ToString("D"));
        command.Parameters.AddWithValue("$journal", receipt.JournalId.ToString("D"));
        var subject = command.ExecuteScalar() as string ?? throw CompletionActionCodec.Invalid();
        return new(intent, reference, subject);
    }

    internal void Require(BoundCompletionIntent expected)
    {
        var receipt = new AttemptReceiptStore(database).Read(expected.Intent.AttemptId) ?? throw CompletionActionCodec.Invalid();
        var actual = Capture(receipt);
        if (actual != expected) throw CompletionActionCodec.Invalid();
    }
}
