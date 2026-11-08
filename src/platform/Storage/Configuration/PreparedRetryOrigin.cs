using System.Collections.Immutable;
using System.Runtime.Versioning;
using Microsoft.Data.Sqlite;
using NexusPipeline.Core.Configuration;
using NexusPipeline.Extensibility.Protocol;
using NexusPipeline.Platform.Serialization;
using NexusPipeline.Platform.Storage.Execution;
using NexusPipeline.Platform.Storage.Journals;
using NexusPipeline.Platform.Storage.Sqlite;

namespace NexusPipeline.Platform.Storage.Configuration;

internal sealed record ConfigurationRetrySource(FrozenRunState State, StoredConfigurationRevision Publication,
    Guid PreviousSessionId, BlobReference PreviousChild, RetryConfigurationInput? Input);

internal sealed class PreparedRetryOrigin
{
    private readonly RuntimeInstance _instance;
    private readonly Guid _nextOperation;
    private readonly Guid _previousJournal;
    private readonly string _subject;
    private readonly string _totalDigest;
    private readonly ImmutableDictionary<string, ImmutableDictionary<ConfigurationAddress, ConfigurationRetrySource>> _sources;
    internal ExecutionTaskRetry? TaskRetry { get; private init; }

    [SupportedOSPlatform("windows")]
    private PreparedRetryOrigin(RuntimeInstance instance, PreparedExecutionRetry retry,
        ImmutableDictionary<string, ImmutableDictionary<ConfigurationAddress, ConfigurationRetrySource>> sources)
    {
        _instance = instance; _nextOperation = retry.Frozen.NextOperationId; _previousJournal = retry.Intent.PreviousJournalId;
        _subject = retry.Intent.SubjectId; _totalDigest = retry.Intent.TotalPlan.Sha256; _sources = sources;
    }

    private PreparedRetryOrigin(RuntimeInstance instance, Guid operation, Guid previousJournal, string subject,
        string totalDigest, ImmutableDictionary<string, ImmutableDictionary<ConfigurationAddress, ConfigurationRetrySource>> sources)
    { _instance = instance; _nextOperation = operation; _previousJournal = previousJournal; _subject = subject; _totalDigest = totalDigest; _sources = sources; }

    internal static PreparedRetryOrigin CaptureInvocation(RuntimeInstance instance, StateDatabase database,
        ContractReader contracts, AttemptWriterState state, string subject, string stepId, ExecutionTaskRetry? taskRetry = null) =>
        new(instance, state.Journal.OperationId, state.Journal.JournalId, subject, state.Attempt.ExecutionPlan.Sha256,
            ReadSources(instance, database, contracts, state.Journal.JournalId, state.Journal.OperationId, subject,
                state.Attempt.AttemptId, AttemptPlanCodec.Reference(state.Attempt.ExecutionPlan), state.Total, stepId,
                ExecutionInvocationCodec.Ordinal(state.Latest.Root, stepId), state.Attempt)) { TaskRetry = taskRetry };

    [SupportedOSPlatform("windows")]
    internal static PreparedRetryOrigin Capture(RuntimeInstance instance, StateDatabase database,
        ContractReader contracts, InstanceLease lease, PreparedExecutionRetry retry)
    {
        lease.VerifyHeld(instance);
        return new(instance, retry, ReadSources(instance, database, contracts, retry.Intent, retry.Plan.ReadWire()));
    }

    private static ImmutableDictionary<string, ImmutableDictionary<ConfigurationAddress, ConfigurationRetrySource>> ReadSources(
        RuntimeInstance instance, StateDatabase database, ContractReader contracts, ExecutionRetryIntent intent, ExecutionPlan total, string? onlyStep = null)
        => ReadSources(instance, database, contracts, intent.PreviousJournalId, intent.PreviousOperationId, intent.SubjectId,
            intent.Frozen.PreviousAttemptId.ToString("D"), intent.TotalPlan, total, onlyStep);

    private static ImmutableDictionary<string, ImmutableDictionary<ConfigurationAddress, ConfigurationRetrySource>> ReadSources(
        RuntimeInstance instance, StateDatabase database, ContractReader contracts, Guid previousJournal, Guid previousOperation,
        string subject, string previousAttempt, BlobReference totalReference, ExecutionPlan total, string? onlyStep = null, int? invocation = null, RunAttemptPlan? verifiedParent = null)
    {
        var snapshots = new ConfigurationSnapshotCodec(new(instance, contracts));
        var sources = ImmutableDictionary.CreateBuilder<string, ImmutableDictionary<ConfigurationAddress, ConfigurationRetrySource>>(StringComparer.Ordinal);
        using var connection = DatabaseConnections.Open(database.DatabasePath, readOnly: true);
        using var transaction = connection.BeginTransaction(deferred: true);
        foreach (var step in total.Steps.Where(step => step.ConfigurationTargets.Count != 0 && (onlyStep is null || step.StepId == onlyStep)))
        {
            using var command = connection.CreateCommand(); command.Transaction = transaction;
            command.CommandText = "SELECT session_id FROM configuration_run_sessions WHERE parent_journal_id=$parent AND step_id=$step AND ($ordinal IS NULL OR invocation_ordinal=$ordinal) ORDER BY invocation_ordinal DESC LIMIT 1;";
            command.Parameters.AddWithValue("$parent", previousJournal.ToString("D"));
            command.Parameters.AddWithValue("$step", step.StepId); command.Parameters.AddWithValue("$ordinal", (object?)invocation ?? DBNull.Value);
            Guid sessionId;
            using (var reader = command.ExecuteReader())
            {
                if (!reader.Read()) throw Invalid();
                sessionId = Guid.ParseExact(reader.GetString(0), "D");
                if (reader.Read()) throw Invalid();
            }
            var session = ConfigurationRunRows.Read(connection, transaction, sessionId, subject) ?? throw Invalid();
            RequireCompleted(session, previousJournal, step.StepId, previousOperation);
            var bound = ConfigurationRunBinding.Read(instance, database, contracts, session, verifiedParent, verifiedParent is null ? null : total);
            ConfigurationRunHistory.Verify(connection, transaction, session, subject,
                bound.Resources.ToDictionary(item => item.State.Source.Address, item => item.State), snapshots);
            if (bound.Parent.AttemptId != previousAttempt ||
                AttemptPlanCodec.Reference(bound.Parent.ExecutionPlan) != totalReference) throw Invalid();
            var items = ImmutableDictionary.CreateBuilder<ConfigurationAddress, ConfigurationRetrySource>();
            foreach (var resource in session.Resources)
            {
                var original = bound.Resources.Single(item => item.State.Source.Address == resource.Address);
                var publication = ConfigurationRevisionRows.Read(connection, transaction, resource.Address, resource.CurrentRevision, snapshots);
                if (publication.Revision.UserId != original.State.Source.UserId || publication.Revision.ScriptId != original.State.Source.ScriptId ||
                    publication.Revision.Contract != original.State.Source.Contract || publication.Revision.Tree.Shape != original.State.Source.Tree.Shape)
                    throw Invalid();
                var child = new BlobReference(resource.PlanPath, new FileInfo(Path.Combine(instance.RuntimeRoot, resource.PlanPath)).Length, resource.PlanDigest);
                var input = invocation is null ? null : RetryConfigurationInput.Read(instance, database, contracts, resource, original.State);
                items.Add(resource.Address, new(original.State, publication, sessionId, child, input));
            }
            if (items.Count != total.ConfigurationSlots.Count(slot => slot.StepId == step.StepId)) throw Invalid();
            sources.Add(step.StepId, items.ToImmutable());
        }
        return sources.ToImmutable();
    }

    internal IReadOnlyDictionary<ConfigurationAddress, ConfigurationRetrySource> ForStep(RuntimeInstance instance,
        Guid nextOperation, FrozenExecutionPlan execution, string stepId, ConfigurationRunRequest request)
    {
        var totalBytes = execution.Export();
        if (instance != _instance || nextOperation != _nextOperation ||
            totalBytes.LongLength < 1 || Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(totalBytes)) != _totalDigest ||
            !_sources.TryGetValue(stepId, out var sources) || !sources.Keys.ToHashSet().SetEquals(request.Resources.Select(item => item.Target.Address)))
            throw Invalid();
        foreach (var resource in request.Resources)
        {
            var original = sources[resource.Target.Address].State;
            if (resource.Target.ExpectedRevision != original.Source.Revision || resource.Target.Contract != original.Source.Contract ||
                request.ExpectedBindingRevision != original.BindingRevision || request.SyncInitially ||
                request.AutoSyncEnabled != original.AutoSyncEnabled || request.SyncAtFinish != original.SyncAtFinish)
                throw Invalid();
        }
        return sources;
    }

    internal IReadOnlyDictionary<ConfigurationAddress, ConfigurationRetrySource> Sources(string stepId) =>
        _sources.TryGetValue(stepId, out var sources) ? sources : throw Invalid();

    internal void RequirePrepared(string stepId, IReadOnlyDictionary<ConfigurationAddress, FrozenRunState> states)
    {
        var expected = Sources(stepId);
        if (!expected.Keys.ToHashSet().SetEquals(states.Keys)) throw Invalid();
        foreach (var pair in states)
        {
            var previous = expected[pair.Key];
            if (pair.Value.OwnerOperationId != _nextOperation || pair.Value.InitialPublicationRevision != previous.Publication.Revision.Revision)
                throw Invalid();
            RunStateCodec.RequireRetrySource(pair.Value, previous.State, TaskRetry, previous.Publication, previous.Input);
        }
    }

    internal void RequireCurrentInside(SqliteConnection connection, SqliteTransaction transaction)
    {
        foreach (var step in _sources)
        foreach (var item in step.Value)
        {
            var source = item.Value;
            var session = ConfigurationRunRows.Read(connection, transaction, source.PreviousSessionId, _subject) ?? throw Invalid();
            RequireCompleted(session, _previousJournal, step.Key, source.State.OwnerOperationId);
            var resource = session.Resources.SingleOrDefault(resource => resource.Address == item.Key) ?? throw Invalid();
            if (resource.CurrentRevision != source.Publication.Revision.Revision || resource.PlanPath != source.PreviousChild.RelativePath ||
                resource.PlanDigest != source.PreviousChild.Sha256) throw Invalid();
            ConfigurationRevisionRows.RequireHistoricalFrozen(connection, transaction, source.State.Source, source.State.BindingRevision,
                source.State.SnapshotReference ?? throw Invalid(), source.Publication);
        }
    }

    internal static void RequireSaved(RuntimeInstance instance, StateDatabase database, ContractReader contracts,
        RunAttemptPlan parent, ExecutionPlan total, string stepId, IReadOnlyDictionary<ConfigurationAddress, FrozenRunState> states)
    {
        var latest = new JournalStore(instance, contracts).Read(Guid.Parse(parent.JournalId));
        var childCodec = new ConfigurationPlanCodec(instance, contracts, new(instance, contracts));
        var operationIds = states.Values.Select(state => state.ConfigurationOperationId).ToHashSet();
        var invocation = ExecutionInvocationCodec.Read(latest.Root, total).SingleOrDefault(item => item.StepId == stepId &&
            item.ConfigurationPlans.Select(plan => childCodec.ReadStructure(plan).OperationId).ToHashSet().SetEquals(operationIds));
        if (invocation is not null)
        {
            RequireInvocationSource(instance, database, contracts, parent, total, invocation, states);
            return;
        }
        if (parent.AttemptOrdinal == 1)
        {
            if (states.Values.Any(state => state.InitialPublicationRevision != state.Source.Revision)) throw Invalid();
            return;
        }
        using var connection = DatabaseConnections.Open(database.DatabasePath, readOnly: true);
        using var transaction = connection.BeginTransaction(deferred: true);
        var id = ExecutionRetryRows.ForNext(connection, transaction, Guid.Parse(parent.OperationId)) ?? throw Invalid();
        var intent = ExecutionRetryRows.Read(connection, transaction, id);
        var source = StartSourceRows.Read(connection, transaction, Guid.Parse(parent.OperationId));
        if (source.RetrySourceOperationId != intent.PreviousOperationId || intent.InstanceId != instance.InstanceId ||
            parent.RunId != intent.Frozen.RunId.ToString("D") || parent.AttemptId != intent.Frozen.NextAttemptId.ToString("D") ||
            parent.JournalId != intent.Frozen.NextJournalId.ToString("D") || parent.AttemptOrdinal != intent.Frozen.NextOrdinal ||
            AttemptPlanCodec.Reference(parent.ExecutionPlan) != intent.TotalPlan) throw Invalid();
        var original = ReadSources(instance, database, contracts, intent, total, stepId);
        if (!original.TryGetValue(stepId, out var expected) || !expected.Keys.ToHashSet().SetEquals(states.Keys)) throw Invalid();
        foreach (var pair in states)
        {
            var previous = expected[pair.Key];
            if (pair.Value.InitialPublicationRevision != previous.Publication.Revision.Revision) throw Invalid();
            RunStateCodec.RequireRetrySource(pair.Value, previous.State);
        }
    }

    internal static void RequireInvocationSource(RuntimeInstance instance, StateDatabase database, ContractReader contracts,
        RunAttemptPlan parent, ExecutionPlan total, ExecutionInvocation invocation, IReadOnlyDictionary<ConfigurationAddress, FrozenRunState> states)
    {
        using var connection = DatabaseConnections.Open(database.DatabasePath, readOnly: true);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT subject_id FROM operations WHERE operation_id=$operation;";
        command.Parameters.AddWithValue("$operation", parent.OperationId);
        var subject = (string?)command.ExecuteScalar() ?? throw Invalid();
        var previous = ReadSources(instance, database, contracts, Guid.Parse(parent.JournalId), Guid.Parse(parent.OperationId), subject,
            parent.AttemptId, AttemptPlanCodec.Reference(parent.ExecutionPlan), total, invocation.StepId, invocation.Ordinal - 1, parent)[invocation.StepId];
        if (!previous.Keys.ToHashSet().SetEquals(states.Keys)) throw Invalid();
        foreach (var pair in states)
        {
            if (pair.Value.InitialPublicationRevision != previous[pair.Key].Publication.Revision.Revision) throw Invalid();
            RunStateCodec.RequireRetrySource(pair.Value, previous[pair.Key].State, invocation.TaskRetry, previous[pair.Key].Publication, previous[pair.Key].Input);
        }
    }

    private static void RequireCompleted(ConfigurationRunStatus session, Guid previousJournal, string stepId, Guid previousOperation)
    {
        if (session.ParentJournalId != previousJournal || session.StepId != stepId || session.OwnerOperationId != previousOperation ||
            session.Phase is not (ConfigurationRunPhase.Completed or ConfigurationRunPhase.Interrupted) ||
            !session.IsWriterAdmissionClosed || session.CompletionOperationId is null) throw Invalid();
    }

    internal static IOException Invalid() => new("configuration_retry_source_unbound");
}
