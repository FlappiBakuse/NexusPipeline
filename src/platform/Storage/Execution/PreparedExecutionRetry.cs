using Microsoft.Data.Sqlite;
using NexusPipeline.Core.Automation;
using NexusPipeline.Core.Execution;
using NexusPipeline.Core.Maintenance;
using NexusPipeline.Core.Operations;
using NexusPipeline.Core.Settings;
using NexusPipeline.Extensibility.Protocol;
using NexusPipeline.Platform.Serialization;
using NexusPipeline.Platform.Storage.Automation;
using NexusPipeline.Platform.Storage.Configuration;
using NexusPipeline.Platform.Storage.Journals;
using NexusPipeline.Platform.Storage.Sqlite;
using System.Collections.Immutable;
using System.Runtime.Versioning;

namespace NexusPipeline.Platform.Storage.Execution;

internal static class ExecutionRetrySelection
{
    internal static ExecutionPlanExplanation Read(ContractReader contracts, ExecutionPlan total, ReadOnlyMemory<byte> definitions,
        ExecutionSelectionRequest request, HostSettingsSnapshot settings)
    {
        var roots = total.RootBindings.ToDictionary(root => Guid.Parse(root.RootId), root => root.Path);
        var imported = new AutomationBundleCodec(contracts).Decode(definitions, roots, request.ExpectedCatalogRevision).Definitions;
        var revisions = total.SourceRevisions.ToDictionary(item => (item.EntityKind, Guid.Parse(item.EntityId)), item => item.Revision);
        long Rev(string kind, Guid id) => revisions.TryGetValue((kind, id), out var value) ? value : throw Invalid();
        var catalog = new AutomationCatalog(request.ExpectedCatalogRevision,
            imported.Roots.Select(item => item with { Revision = Rev("root", item.Id) }).ToImmutableArray(),
            imported.Scripts.Select(item => item with { Revision = Rev("script", item.Id) }).ToImmutableArray(),
            imported.Users.Select(item => item with { Revision = Rev("user", item.Id) }).ToImmutableArray(),
            imported.Bindings.Select(item => item with { Revision = Rev("binding", item.Id) }).ToImmutableArray(),
            imported.Queues.Select(item => item with { Revision = Rev("queue", item.Id) }).ToImmutableArray(), []);
        var selection = ExecutionSelectionPolicy.Select(catalog, settings, request);
        if (!selection.Items.Select(item => item.Binding.Id.ToString("D")).SequenceEqual(total.Selection.Select(item => item.BindingId)))
            throw Invalid();
        var saved = total.Selection.ToDictionary(item => item.StepId, StringComparer.Ordinal);
        var readiness = selection.Items.Where(item => item.Disposition == ExecutionSelectionDisposition.Candidate).Select(item =>
        {
            var prior = saved[item.StepId];
            if (prior.Disposition != "runnable" || prior.ReasonCode != ExecutionSelectionPolicy.InputsVerified) throw Invalid();
            return new ExecutionItemReadiness(item, true, ExecutionSelectionPolicy.InputsVerified);
        }).ToImmutableArray();
        var explanation = ExecutionSelectionPolicy.Explain(selection, readiness);
        if (explanation.HasBlockedItems || !explanation.HasRunnable ||
            !explanation.Items.Select(item => item.Disposition.ToString().ToLowerInvariant()).SequenceEqual(total.Selection.Select(item => item.Disposition)))
            throw Invalid();
        return explanation;
    }

    private static IOException Invalid() => new("execution_retry_selection_unbound");
}

[SupportedOSPlatform("windows")]
internal sealed class PreparedExecutionRetry : IPreparedExecutionRetry
{
    private readonly RuntimeInstance _instance;
    private readonly StateDatabase _database;
    private readonly InstanceLease _lease;
    private readonly ContractReader _contracts;
    private readonly bool _hasConfigurations;
    private bool _disposed;
    internal PreparedRetryOrigin? ConfigurationOrigin { get; }
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
        byte[] definitions, HostSettingsSnapshot settings, ExecutionTimeBudget? budget,
        bool hasConfigurations, PreparedRetryOrigin? configurationOrigin)
    {
        _instance = instance; _database = database; _lease = lease; _contracts = contracts; Intent = intent; Previous = previous;
        Explanation = explanation; Plan = plan; _definitions = definitions.ToArray(); Settings = settings; _timeBudget = budget;
        _hasConfigurations = hasConfigurations; ConfigurationOrigin = configurationOrigin;
    }

    internal static PreparedExecutionRetry Capture(RuntimeInstance instance, StateDatabase database, ContractReader contracts,
        InstanceLease lease, Guid retryOperationId)
    {
        lease.VerifyHeld(instance);
        ExecutionRetryIntent intent; FrozenStartSource previous; HostSettingsSnapshot settings; bool replay;
        using (var connection = DatabaseConnections.Open(database.DatabasePath, readOnly: true,
            domains: DatabaseDomain.Runtime | DatabaseDomain.Records | DatabaseDomain.System))
        using (var transaction = DatabaseConnections.BeginRead(connection))
        {
            intent = ExecutionRetryRows.Read(connection, transaction, retryOperationId);
            previous = StartSourceRows.Read(connection, transaction, intent.PreviousOperationId);
            settings = HostSettingsRows.Read(connection, transaction, previous.Request.ExpectedSettingsRevision);
            using var command = connection.CreateCommand(); command.Transaction = transaction;
            command.CommandText = "SELECT EXISTS(SELECT 1 FROM main.execution_start_sources WHERE operation_id=$next);";
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
        var wire = plan.ReadWire();
        var content = wire.DefinitionBundle;
        using var input = blobs.OpenVerified(new(content.RelativePath, content.SizeBytes, content.Sha256));
        var definitions = new byte[checked((int)input.Length)]; input.ReadExactly(definitions);
        var explanation = ExecutionRetrySelection.Read(contracts, wire, definitions, previous.Request, settings);
        var receipt = new AttemptReceiptStore(database).Read(intent.Frozen.PreviousAttemptId) ?? throw Invalid();
        var budget = replay ? null : lease.RunBudgets.CounterForRetry(database, receipt);
        var hasConfigurations = wire.ConfigurationSlots.Count != 0;
        var origin = !replay && hasConfigurations ? PreparedRetryOrigin.Capture(instance, database, contracts, lease, intent, wire) : null;
        return new(instance, database, lease, contracts, intent, previous, explanation, plan, definitions, settings, budget, hasConfigurations, origin);
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
        if (!_hasConfigurations) return;
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
        if (!DatabaseConnections.IsBound(connection, _database.DatabasePath) ||
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
