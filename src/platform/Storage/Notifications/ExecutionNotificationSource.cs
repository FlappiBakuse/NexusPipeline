using System.Collections.Immutable;
using System.Text.Json;
using System.Runtime.Versioning;
using NexusPipeline.Core.Execution;
using NexusPipeline.Core.Notifications;
using NexusPipeline.Extensibility.Protocol;
using NexusPipeline.Platform.Serialization;
using NexusPipeline.Platform.Storage.Execution;
using NexusPipeline.Platform.Storage.Sqlite;

namespace NexusPipeline.Platform.Storage.Notifications;

internal sealed class ExecutionNotificationSource
{
    internal AttemptReceipt Receipt { get; }
    internal string SubjectId { get; }
    internal string? DeclineReasonCode { get; }
    internal BlobReference TotalPlan { get; }
    internal BlobReference DefinitionBundle { get; }
    internal ImmutableArray<NotificationRoute> Routes { get; }
    internal ImmutableArray<ExecutionStepFact> Facts { get; }

    private ExecutionNotificationSource(AttemptReceipt receipt, string subject, string? declineReason,
        BlobReference totalPlan, BlobReference bundle, ImmutableArray<NotificationRoute> routes,
        ImmutableArray<ExecutionStepFact> facts)
    {
        Receipt = receipt; SubjectId = subject; DeclineReasonCode = declineReason;
        TotalPlan = totalPlan; DefinitionBundle = bundle; Routes = routes; Facts = facts;
    }

    [SupportedOSPlatform("windows")]
    internal static ExecutionNotificationSource? ReadActual(RuntimeInstance instance, StateDatabase database,
        ContractReader contracts, ExecutionRunFinality finality, AttemptReceipt receipt)
    {
        if (instance.InstanceId != database.InstanceId || instance.RuntimeRoot != database.RuntimeRoot)
            throw Invalid();
        finality.RequireDatabase(database);
        var decision = finality.ReadDecision(receipt);
        if (!decision.IsFinal && !ExecutionRecordProjection.RequireRecoveredRecord(instance, database, contracts, receipt)) return null;
        var bound = AttemptPreparedBinding.Read(instance, contracts, database, receipt.JournalId);
        using var parsed = StrictJson.Parse(JsonSerializer.SerializeToUtf8Bytes(bound.Document));
        if (!bound.IsExecutionStart || bound.Plan.OperationId != receipt.OperationId.ToString("D") ||
            bound.Plan.AttemptId != receipt.Facts.AttemptId.ToString("D") ||
            bound.Plan.RunId != receipt.Facts.RunId.ToString("D") ||
            AttemptPreparedBinding.ExpectedReceipt(bound.Plan,
                new NexusPipeline.Platform.Storage.Journals.JournalStore(instance, contracts).Read(receipt.JournalId)) != receipt)
            throw Invalid();
        var total = bound.Total;
        if (ProjectInitializationPlan.IsInitialization(total)) return null;
        var definition = new BlobReference(total.DefinitionBundle.RelativePath, total.DefinitionBundle.SizeBytes, total.DefinitionBundle.Sha256);
        var bundle = ReadBundle(instance, contracts, definition);
        var routes = ReadRoutes(total, bundle);
        var facts = OrderedFacts(bound.Plan, total, parsed.RootElement, receipt);
        string subject;
        using (var connection = DatabaseConnections.Open(database.DatabasePath, readOnly: true))
        using (var transaction = connection.BeginTransaction(deferred: true))
        {
            RequirePublishedRecord(connection, transaction, receipt);
            using var command = connection.CreateCommand(); command.Transaction = transaction;
            command.CommandText = "SELECT subject_id FROM operations WHERE operation_id=$operation AND operation_kind='execution.start' AND status='completed';";
            command.Parameters.AddWithValue("$operation", receipt.OperationId.ToString("D"));
            subject = command.ExecuteScalar() as string ?? throw Invalid();
            transaction.Commit();
        }
        return new(receipt, subject, decision.DeclineReasonCode, AttemptPlanCodec.Reference(bound.Plan.ExecutionPlan),
            definition, routes, facts);
    }

    internal static ImmutableArray<ExecutionStepFact> OrderedFacts(RunAttemptPlan attempt, ExecutionPlan total,
        JsonElement journal, AttemptReceipt receipt)
    {
        var terminal = AttemptResultCodec.Read(journal, attempt);
        if (terminal?.Mode == "executed") return StepResultCodec.CompletedFacts(journal, total, attempt.ExecutionPlan.Sha256);
        if (receipt.Facts.Engine != EngineOutcome.NotStarted ||
            terminal is null && !NoEffectReceipt.Recorded(journal)) throw Invalid();
        return total.Steps.Select(step => new ExecutionStepFact(step.StepId, EngineOutcome.NotStarted,
            BusinessOutcome.Unverified, receipt.Facts.ReasonCode)).ToImmutableArray();
    }

    internal static void RequirePublishedRecord(Microsoft.Data.Sqlite.SqliteConnection connection,
        Microsoft.Data.Sqlite.SqliteTransaction transaction, AttemptReceipt receipt)
    {
        if (AttemptReceiptStore.ReadInside(connection, transaction, receipt.Facts.AttemptId) != receipt) throw Invalid();
        using var command = connection.CreateCommand(); command.Transaction = transaction;
        command.CommandText = "SELECT attempt_id,attempt_ordinal,engine_outcome,business_outcome,lifecycle_outcome,recovery_outcome,reason_code " +
            "FROM run_records WHERE run_id=$run;";
        command.Parameters.AddWithValue("$run", receipt.Facts.RunId.ToString("D"));
        using var row = command.ExecuteReader(); var facts = receipt.Facts;
        if (!row.Read() || row.GetString(0) != facts.AttemptId.ToString("D") || row.GetInt32(1) != facts.Ordinal ||
            row.GetInt32(2) != (int)facts.Engine || row.GetInt32(3) != (int)facts.Business ||
            row.GetInt32(4) != (int)facts.Lifecycle || row.GetInt32(5) != (int)facts.Recovery || row.GetString(6) != facts.ReasonCode)
            throw Invalid();
    }

    internal static AutomationBundle ReadBundle(RuntimeInstance instance, ContractReader contracts, BlobReference reference)
    {
        NotificationDeliveryCodec.Content(reference);
        if (reference.SizeBytes is < 1 or > StrictJson.MaximumBytes) throw Invalid();
        using var input = new ImmutableBlobStore(instance, contracts).OpenVerified(reference);
        var bytes = new byte[checked((int)input.Length)]; input.ReadExactly(bytes);
        return contracts.Read<AutomationBundle>(bytes, "automation-bundle.schema.json");
    }

    internal static ImmutableArray<NotificationRoute> ReadRoutes(ExecutionPlan total, AutomationBundle bundle)
    {
        var users = bundle.Users.ToDictionary(item => item.Id, StringComparer.Ordinal);
        var bindings = bundle.Bindings.Select(item => NexusPipeline.Platform.Storage.Automation.BindingPolicyCodec.Resolve(users[item.UserId], item))
            .ToDictionary(item => item.Id, StringComparer.Ordinal);
        var selected = total.Selection.DistinctBy(item => item.BindingId, StringComparer.Ordinal).Select(item =>
        {
            if (!bindings.TryGetValue(item.BindingId, out var binding)) throw Invalid();
            return new BindingRouteInput(Guid.ParseExact(binding.Id, "D"), binding.NotifyEnabled,
                binding.NotificationTaskIds.Select(id => Guid.ParseExact(id, "D")).ToImmutableArray());
        }).ToImmutableArray();
        QueueRouteInput? queue = null;
        if (total.QueueId is { } queueId)
        {
            var saved = bundle.Queues.SingleOrDefault(item => item.Id == queueId) ?? throw Invalid();
            queue = new(Guid.ParseExact(saved.Id, "D"), saved.NotifyEnabled,
                saved.NotificationTaskIds.Select(id => Guid.ParseExact(id, "D")).ToImmutableArray(),
                selected.Select(item => item.BindingId).ToImmutableArray());
        }
        return NotificationRoutingPolicy.SelectFrozen(queue, selected);
    }

    internal static IOException Invalid() => new("notification_execution_source_unbound");
}
