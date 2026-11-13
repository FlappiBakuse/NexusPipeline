using System.Collections.Immutable;
using NexusPipeline.Core.Configuration;
using NexusPipeline.Extensibility.Protocol;
using NexusPipeline.Platform.Processes;
using NexusPipeline.Platform.Serialization;
using NexusPipeline.Platform.Storage.Configuration;
using NexusPipeline.Platform.Storage.Journals;
using NexusPipeline.Platform.Storage.Sqlite;

namespace NexusPipeline.Platform.Storage.Execution;

internal sealed class RunBindingEvidence
{
    private readonly RuntimeInstance _instance;
    private readonly StateDatabase _database;
    private readonly ContractReader _contracts;
    private readonly ImmutableBlobStore _blobs;
    private readonly RunAttemptPlan _attempt;
    private readonly ExecutionPlan _total;
    private readonly ExecutionPlanStep _step;
    private readonly int _ordinal;
    private readonly ExecutionTaskRetry? _taskRetry;

    internal string AttemptId => _attempt.AttemptId;
    internal BlobReference TotalPlan => AttemptPlanCodec.Reference(_attempt.ExecutionPlan);
    internal string? UserId => _step.UserId;
    internal bool HasHooks => _step.Hooks is { Count: > 0 };
    internal ImmutableArray<BlobReference> Plans { get; }

    private RunBindingEvidence(RuntimeInstance instance, StateDatabase database, ContractReader contracts,
        ConfigurationRunStatus session, RunAttemptPlan attempt, ExecutionPlan total)
    {
        if (attempt.JournalId != session.ParentJournalId.ToString("D") ||
            attempt.OperationId != session.OwnerOperationId.ToString("D"))
            throw new IOException("configuration_run_parent_unbound");
        _instance = instance; _database = database; _contracts = contracts; _blobs = new(instance, contracts);
        _attempt = attempt; _total = total; _ordinal = session.InvocationOrdinal;
        _step = total.Steps.SingleOrDefault(item => item.StepId == session.StepId) ?? throw new IOException("configuration_run_step_unknown");
        using var history = JournalReadScope.HoldAttempt(instance, contracts, database, session.ParentJournalId);
        var latest = new JournalStore(instance, contracts).Read(session.ParentJournalId);
        _taskRetry = ExecutionInvocationCodec.Read(latest.Root, total).SingleOrDefault(item =>
            item.StepId == session.StepId && item.Ordinal == _ordinal)?.TaskRetry;
        Plans = ExecutionInvocationCodec.PlansForOrdinal(latest.Root, total, attempt, session.StepId, _ordinal);
    }

    internal static RunBindingEvidence Read(RuntimeInstance instance, StateDatabase database, ContractReader contracts,
        ConfigurationRunStatus session)
    {
        var bound = AttemptPreparedBinding.Read(instance, contracts, database, session.ParentJournalId);
        return new(instance, database, contracts, session, bound.Plan, bound.Total);
    }

    // Invocation history can be checked while the same attempt is already being verified.
    internal static RunBindingEvidence Read(RuntimeInstance instance, StateDatabase database, ContractReader contracts,
        ConfigurationRunStatus session, RunAttemptPlan verifiedAttempt, ExecutionPlan verifiedTotal) =>
        new(instance, database, contracts, session, verifiedAttempt, verifiedTotal);

    internal void RequireInvocation(RuntimeInstance instance, StateDatabase database, ConfigurationRunStatus session)
    {
        if (instance != _instance || database != _database ||
            session.ParentJournalId.ToString("D") != _attempt.JournalId || session.OwnerOperationId.ToString("D") != _attempt.OperationId ||
            session.StepId != _step.StepId || session.InvocationOrdinal != _ordinal)
            throw new IOException("configuration_run_parent_unbound");
    }

    internal void RequireResource(FrozenRunState state, string targetPath) =>
        RunConfigurationBinding.Require(state, _step.StepId, targetPath, _attempt, _total, _blobs, _contracts,
            laterInvocation: _ordinal > 1, taskRetry: _taskRetry);

    internal void RequireSources(IReadOnlyDictionary<ConfigurationAddress, FrozenRunState> states) =>
        PreparedRetryOrigin.RequireSaved(_instance, _database, _contracts, _attempt, _total, _step.StepId, states);

    internal (ConfigurationEditorCommand Command, CommandLineSyntax Syntax) WriterCommand(string role)
    {
        if (HasHooks && role != "process")
        {
            var hook = ExecutionBindingRoles.Hook(_step, role);
            var command = EditorCommandPolicy.Freeze(new(hook.Command.Path, hook.Command.Sha256,
                hook.Arguments.ToImmutableArray(), hook.WorkingDirectory));
            return (command, hook.LaunchKind == "command" ? CommandLineSyntax.CommandInterpreter : CommandLineSyntax.ArgumentVector);
        }
        return (EditorCommandPolicy.Freeze(new(_step.ExecutablePath, _step.ImageSha256, _step.Arguments.ToImmutableArray(),
            _step.WorkingDirectory) { ExclusiveProgram = _step.ExclusiveProgram == true }), CommandLineSyntax.ArgumentVector);
    }

    internal static RegisteredJournal ReadAdmission(RuntimeInstance instance, StateDatabase database, ContractReader contracts,
        Guid journalId, Guid owner, string stepId, IEnumerable<BlobReference> references)
    {
        using var history = JournalReadScope.HoldAttempt(instance, contracts, database, journalId);
        var registry = new RecoveryJournalRegistry(database).ReadSnapshot(journalId).Journals;
        var journal = registry.SingleOrDefault(item => item.JournalId == journalId && item.Kind == JournalKind.RunAttempt && item.OperationId == owner)
            ?? throw new IOException("configuration_run_parent_unbound");
        var bound = AttemptPreparedBinding.Read(instance, contracts, database, journalId);
        if (journal.Phase == JournalPhase.Applying)
        {
            if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
            var current = AttemptWriterState.Read(instance, database, contracts, journalId);
            var ordinal = ExecutionStepAdmission.RequireNext(new JournalStore(instance, contracts).Read(journalId), bound.Total, stepId,
                bound.Total.Steps.Single(step => step.StepId == stepId).Hooks?.Any(hook => hook.Role == "preHook") == true ? "preHook" : "process");
            using var predecessors = ExecutionStepAdmission.HoldPredecessors(instance, database, contracts, current, ordinal);
        }
        var expected = ExecutionInvocationCodec.CurrentPlans(new JournalStore(instance, contracts).Read(journalId).Root, bound.Plan, stepId).ToHashSet();
        if (!expected.SetEquals(references) || journal.Phase is not (JournalPhase.Prepared or JournalPhase.Applying))
            throw new IOException("configuration_run_parent_coverage_invalid");
        return journal;
    }

    internal static void RequireAdmissionSource(RuntimeInstance instance, StateDatabase database, ContractReader contracts,
        RegisteredJournal journal, string stepId, IReadOnlyDictionary<ConfigurationAddress, FrozenRunState> states)
    {
        var graph = AttemptPreparedBinding.ReadPlanGraph(instance, contracts, journal, database);
        PreparedRetryOrigin.RequireSaved(instance, database, contracts, graph.Plan, graph.Total, stepId, states);
    }

    internal static int ReadOrdinal(RuntimeInstance instance, StateDatabase database, ContractReader contracts, Guid journalId, string stepId)
    {
        using var history = JournalReadScope.HoldAttempt(instance, contracts, database, journalId);
        return ExecutionInvocationCodec.Ordinal(new JournalStore(instance, contracts).Read(journalId).Root, stepId);
    }

    internal static IDisposable HoldRestored(RuntimeInstance instance, StateDatabase database, ContractReader contracts,
        AttemptWriterState state, IEnumerable<string> selectedSteps)
    {
        var selected = selectedSteps.ToHashSet(StringComparer.Ordinal);
        var references = ExecutionInvocationCodec.Plans(state.Latest.Root, state.Total, state.Attempt)
            .Where(item => selected.Contains(item.StepId)).Select(item => (item.StepId, Reference: AttemptPlanCodec.Reference(item.Plan)));
        return new ConfigurationSceneEvidence(instance, database, contracts).HoldRestored(state.Journal.JournalId, references,
            (stepId, reference) => ExecutionInvocationCodec.PlanOrdinal(state.Latest.Root, state.Total, state.Attempt, stepId, reference));
    }
}
