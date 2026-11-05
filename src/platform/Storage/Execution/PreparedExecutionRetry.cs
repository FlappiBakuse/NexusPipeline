using System.Runtime.Versioning;
using Microsoft.Data.Sqlite;
using NexusPipeline.Core.Execution;
using NexusPipeline.Core.Maintenance;
using NexusPipeline.Core.Operations;
using NexusPipeline.Core.Settings;
using NexusPipeline.Extensibility.Protocol;
using NexusPipeline.Platform.Serialization;
using NexusPipeline.Platform.Storage.Journals;
using NexusPipeline.Platform.Storage.Configuration;
using NexusPipeline.Platform.Storage.Sqlite;

namespace NexusPipeline.Platform.Storage.Execution;

[SupportedOSPlatform("windows")]
internal sealed class PreparedExecutionRetry : IPreparedExecutionRetry
{
    private readonly RuntimeInstance _instance;
    private readonly StateDatabase _database;
    private readonly InstanceLease _lease;
    private readonly ContractReader _contracts;
    private bool _disposed;
    internal PreparedRetryOrigin? ConfigurationOrigin { get; private set; }
    internal ExecutionRetryIntent Intent { get; }
    internal FrozenStartSource Previous { get; }
    internal ExecutionPlanExplanation Explanation { get; }
    internal FrozenExecutionPlan Plan { get; }
    private readonly byte[] _definitions;
    internal ReadOnlyMemory<byte> Definitions => _definitions.ToArray();
    internal HostSettingsSnapshot Settings { get; }
    private readonly ExecutionTimeBudget? _timeBudget;
    internal ExecutionTimeBudget TimeBudget => _timeBudget ?? throw new IOException("execution_retry_budget_continuity_unknown");
    public PendingExecutionRetry Frozen => Intent.Frozen;
    internal ExecutionSelectionRequest Request => Previous.Request;
    internal AcceptedExecutionRun AcceptedRun => _lease.RunBudgets.Continuation(_database,
        new AttemptReceiptStore(_database).Read(Frozen.PreviousAttemptId) ?? throw Invalid());
    internal DateTimeOffset FirstPreparedAtUtc => _lease.RunBudgets.PreparedAt(_database, Frozen.RunId) ?? throw Invalid();

    private PreparedExecutionRetry(RuntimeInstance instance, StateDatabase database, InstanceLease lease, ContractReader contracts,
        ExecutionRetryIntent intent, FrozenStartSource previous, ExecutionPlanExplanation explanation, FrozenExecutionPlan plan,
        byte[] definitions, HostSettingsSnapshot settings, ExecutionTimeBudget? budget)
    {
        _instance = instance; _database = database; _lease = lease; _contracts = contracts; Intent = intent; Previous = previous;
        Explanation = explanation; Plan = plan; _definitions = definitions.ToArray(); Settings = settings; _timeBudget = budget;
    }

    internal static PreparedExecutionRetry Capture(RuntimeInstance instance, StateDatabase database, ContractReader contracts,
        InstanceLease lease, Guid retryOperationId)
    {
        lease.VerifyHeld(instance);
        ExecutionRetryIntent intent; FrozenStartSource previous; HostSettingsSnapshot settings; bool replay;
        using (var connection = DatabaseConnections.Open(database.DatabasePath, readOnly: true))
        using (var transaction = connection.BeginTransaction(deferred: true))
        {
            intent = ExecutionRetryRows.Read(connection, transaction, retryOperationId);
            previous = StartSourceRows.Read(connection, transaction, intent.PreviousOperationId);
            settings = HostSettingsRows.Read(connection, transaction, previous.Request.ExpectedSettingsRevision);
            using var command = connection.CreateCommand(); command.Transaction = transaction;
            command.CommandText = "SELECT EXISTS(SELECT 1 FROM execution_start_sources WHERE operation_id=$next);";
            command.Parameters.AddWithValue("$next", intent.Frozen.NextOperationId.ToString("D"));
            replay = (long)command.ExecuteScalar()! == 1;
            if (!replay) ExecutionRetryOutcomes.RequireWaiting(connection, transaction, intent);
        }
        if (intent.InstanceId != instance.InstanceId || previous.JournalId != intent.PreviousJournalId ||
            previous.RunId != intent.Frozen.RunId || previous.Identity.SubjectId != intent.SubjectId) throw Invalid();
        var blobs = new ImmutableBlobStore(instance, contracts);
        var attempt = new AttemptPlanCodec(instance, contracts, blobs).Read(intent.PreviousPlan).ReadWire();
        StartSourceRows.RequireAttempt(previous, attempt);
        var plan = new ExecutionPlanCodec(instance, contracts, blobs).Read(intent.TotalPlan);
        if (attempt.JournalId != previous.JournalId.ToString("D") || attempt.AttemptId != intent.Frozen.PreviousAttemptId.ToString("D") ||
            attempt.AttemptOrdinal + 1 != intent.Frozen.NextOrdinal || AttemptPlanCodec.Reference(attempt.ExecutionPlan) != intent.TotalPlan)
            throw Invalid();
        using var input = blobs.OpenVerified(new(plan.ReadWire().DefinitionBundle.RelativePath,
            plan.ReadWire().DefinitionBundle.SizeBytes, plan.ReadWire().DefinitionBundle.Sha256));
        var definitions = new byte[checked((int)input.Length)]; input.ReadExactly(definitions);
        var explanation = ExecutionRetrySelection.Read(contracts, plan.ReadWire(), definitions, previous.Request, settings);
        var receipt = new AttemptReceiptStore(database).Read(intent.Frozen.PreviousAttemptId) ?? throw Invalid();
        var budget = replay ? null : lease.RunBudgets.CounterForRetry(database, receipt);
        var result = new PreparedExecutionRetry(instance, database, lease, contracts, intent, previous, explanation, plan, definitions, settings, budget);
        if (!replay && plan.ReadWire().ConfigurationSlots.Count != 0)
            result.ConfigurationOrigin = PreparedRetryOrigin.Capture(instance, database, contracts, lease, result);
        return result;
    }

    internal void RequireRequest(Guid operationId, string subject, ExecutionSelectionRequest request, bool preparing)
    {
        _lease.VerifyHeld(_instance);
        if (preparing) ObjectDisposedException.ThrowIf(_disposed, this);
        if (operationId != Frozen.NextOperationId || subject != Intent.SubjectId ||
            ExecutionSelectionPolicy.FreezeRequest(request) != Request) throw Invalid();
    }

    internal void RequireConfigurations(ExecutionConfigurationCursors cursors)
    {
        if (Plan.ReadWire().ConfigurationSlots.Count == 0) return;
        cursors.RequireHistorical(ConfigurationOrigin ?? throw new IOException("configuration_retry_source_unbound"));
    }

    internal void RequireReceipt(AttemptAcceptanceResult receipt)
    {
        var plan = new AttemptPlanCodec(_instance, _contracts, new(_instance, _contracts)).Read(receipt.Plan).ReadWire();
        if (receipt.JournalId != Frozen.NextJournalId || plan.AttemptId != Frozen.NextAttemptId.ToString("D") ||
            plan.AttemptOrdinal != Frozen.NextOrdinal || plan.RunId != Frozen.RunId.ToString("D") ||
            AttemptPlanCodec.Reference(plan.ExecutionPlan) != Intent.TotalPlan) throw Invalid();
    }

    internal void RequireInside(SqliteConnection connection, SqliteTransaction transaction, OperationIdentity operation,
        ExecutionSelectionRequest request, RunAttemptPlan attempt, ExecutionPlan total)
    {
        RequireRequest(operation.OperationId, operation.SubjectId, request, true);
        if (!StringComparer.OrdinalIgnoreCase.Equals(connection.DataSource, PhysicalPathPolicy.NativePath(_database.DatabasePath)) ||
            ExecutionRetryRows.Read(connection, transaction, RetryIntentCodec.Identity(Intent).OperationId) != Intent ||
            attempt.JournalId != Frozen.NextJournalId.ToString("D") || attempt.AttemptId != Frozen.NextAttemptId.ToString("D") ||
            attempt.AttemptOrdinal != Frozen.NextOrdinal || attempt.RunId != Frozen.RunId.ToString("D") ||
            AttemptPlanCodec.Reference(attempt.ExecutionPlan) != Intent.TotalPlan || total.RunId != Frozen.RunId.ToString("D"))
            throw Invalid();
        if (TimeProvider.System.GetUtcNow() < Frozen.NotBefore || TimeBudget.Read().IsExhausted) throw new IOException("retry_backoff_or_budget");
        ExecutionRetryOutcomes.RequireWaiting(connection, transaction, Intent);
        ConfigurationOrigin?.RequireCurrentInside(connection, transaction);
        ExecutionRetryAuthority.RequireFrozenSettings(connection, transaction, total);
    }

    public void Dispose() { _disposed = true; }
    private static IOException Invalid() => new("execution_retry_origin_unbound");
}
