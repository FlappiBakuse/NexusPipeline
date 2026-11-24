using Microsoft.Data.Sqlite;
using NexusPipeline.Core.Configuration;
using NexusPipeline.Core.Execution;
using NexusPipeline.Extensibility.Protocol;
using NexusPipeline.Platform.Serialization;
using NexusPipeline.Platform.Storage.Configuration;
using NexusPipeline.Platform.Storage.Journals;
using NexusPipeline.Platform.Storage.Sqlite;
using System.Collections.Immutable;
using System.Runtime.Versioning;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace NexusPipeline.Platform.Storage.Execution;

internal sealed class ExecutionInvocationStore(RuntimeInstance instance, StateDatabase database, ContractReader contracts, InstanceLease lease)
{
    internal static bool HasOrdinaryFailure(AttemptWriterState state, string stepId)
    {
        var result = StepResultCodec.Read(state.Latest.Root, state.Total).SingleOrDefault(item => item.Binding.StepId == stepId);
        var coverage = new ExecutionStepCompletion(stepId, StepCompletionKind.Collected, result, null);
        var fact = ExecutionBindingRoles.Combine(state.Latest.Root, state.Total, coverage, state.Attempt.ExecutionPlan.Sha256);
        return fact.ReasonCode is not (BindingTimeoutCodec.Reason or "execution_step_total_timeout") &&
            (fact.Engine != EngineOutcome.Unknown || fact.ReasonCode is "execution_business_failed" or "execution_step_stall_timeout") &&
            RetryDecision.ClassifyCollectedPrimary(fact) == AttemptOutcome.Failed;
    }

    internal static bool HasRemainingInvocation(AttemptWriterState state, string stepId) =>
        ExecutionInvocationCodec.Ordinal(state.Latest.Root, stepId) + state.PreviousInvocationCount(stepId) <
            (state.Total.Steps.Single(step => step.StepId == stepId).MaximumAttempts ?? 1);

    internal static bool CanRetry(AttemptWriterState state, string stepId) =>
        state.IsExecutionStart && DispatchIntentCodec.Read(state.Latest.Root, state.Total) is null && state.Latest.Phase == JournalPhase.Applying &&
        state.Gate.State == EffectGateState.Entered && HasRemainingInvocation(state, stepId) &&
        ExecutionBindingRoles.IsComplete(state.Latest.Root, state.Total, stepId) && HasOrdinaryFailure(state, stepId);

    [SupportedOSPlatform("windows")]
    internal bool Append(Guid journalId, string stepId, PreparedConfigurationRun? prepared, ExecutionTimeBudget budget) => lease.PublishExecution(() =>
    {
        lease.VerifyHeld(instance);
        if (budget.Read().Remaining <= TimeSpan.Zero) return false;
        var state = AttemptWriterState.Read(instance, database, contracts, journalId);
        if (state.Latest.Revision != state.Journal.Revision || !CanRetry(state, stepId)) throw ExecutionInvocationCodec.Invalid();
        AttemptTerminalEvidence.RequireArtifacts(instance, database, contracts, state);
        StepWritersEvidence.RequireStopped(instance, database, contracts, journalId, state.AllJobs.Where(job => job.StepId == stepId));
        using var restored = RunBindingEvidence.HoldRestored(instance, database, contracts, state, [stepId]);
        using (var connection = DatabaseConnections.Open(database.DatabasePath, readOnly: true, domains: DatabaseDomain.Runtime | DatabaseDomain.Configuration | DatabaseDomain.Records | DatabaseDomain.Extensions | DatabaseDomain.System | DatabaseDomain.Content))
        using (var transaction = DatabaseConnections.BeginRead(connection))
        {
            AttemptTerminalEvidence.RequireRestoredInside(connection, transaction, state, stepId: stepId);
            transaction.Commit();
        }
        var plans = prepared?.Use(resources => resources.Select(item => item.Reference).ToImmutableArray()) ?? [];
        if (!TaskRetryEvidence.TryCapture(state, stepId, new(instance, contracts), out var taskRetry)) throw ExecutionInvocationCodec.Invalid();
        var item = new ExecutionInvocation(ExecutionInvocationCodec.Format, 1, stepId,
            ExecutionInvocationCodec.Ordinal(state.Latest.Root, stepId) + 1, state.Latest.Revision,
            state.Jobs.Last(job => job.StepId == stepId).JobName, DateTimeOffset.UtcNow, plans) { TaskRetry = taskRetry };
        var document = JsonNode.Parse(state.Latest.Export())!.AsObject();
        document["revision"] = state.Latest.Revision + 1;
        document["updatedAt"] = item.PreparedAtUtc.ToString("yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'");
        document["participants"]!.AsArray().Add(ExecutionInvocationCodec.Participant(item, state.Total.Steps.Single(step => step.StepId == stepId).ScriptId));
        var bytes = JsonSerializer.SerializeToUtf8Bytes(document);
        var next = JournalDocument.Read(bytes, contracts);
        RequireStructure(instance, database, contracts, state, state.Latest, next, item);
        if (budget.Read().Remaining <= TimeSpan.Zero) return false;
        new JournalStore(instance, contracts).Append(journalId, state.Latest.Revision, bytes);
        ExecutionAttemptProjection.Synchronize(instance, database, contracts,
            AttemptWriterState.Read(instance, database, contracts, journalId));
        return true;
    });

    internal static void RequireStructure(RuntimeInstance instance, StateDatabase database, ContractReader contracts, AttemptWriterState state,
        JournalDocument before, JournalDocument next, ExecutionInvocation item)
    {
        var prior = state with { Latest = before, Steps = WriterIntentCodec.Read(before.Root, state.Total) };
        if (!TaskRetryEvidence.TryCapture(prior, item.StepId, new(instance, contracts), out var taskRetry) ||
            !TaskRetryEvidence.Same(taskRetry, item.TaskRetry)) throw ExecutionInvocationCodec.Invalid();
        _ = ExecutionInvocationCodec.Read(next.Root, state.Total);
        var jobs = prior.Jobs;
        if (!CanRetry(prior, item.StepId) || before.Phase != next.Phase || next.Revision != before.Revision + 1 ||
            item.BeforeRevision != before.Revision || item.Ordinal != ExecutionInvocationCodec.Ordinal(before.Root, item.StepId) + 1 ||
            jobs.LastOrDefault()?.StepId != item.StepId || jobs.Last(job => job.StepId == item.StepId).JobName != item.PreviousJobName ||
            jobs.Where(job => job.StepId == item.StepId).Any(job => job.ClosedElapsedMilliseconds is null)) throw ExecutionInvocationCodec.Invalid();
        ProviderLauncherCodec.RequireResolved(before.Root, state.Total, state.Attempt.ExecutionPlan.Sha256, item.StepId);
        AndroidCollectedCodec.RequireResolved(before.Root, state.Total, state.Attempt.ExecutionPlan.Sha256, item.StepId);
        ExecutionCaptureCodec.RequireResolved(before.Root, state.Total, state.Attempt.ExecutionPlan.Sha256, item.StepId);
        PreviewDomainCodec.RequireResolved(before.Root, state.Total, state.Attempt.ExecutionPlan.Sha256, item.StepId);
        if (TargetJobCodec.Read(before.Root, state.Total, state.Attempt.ExecutionPlan.Sha256).Any(job => job.Binding.StepId == item.StepId &&
            job.Stage is not ("stopped" or "retained" or "recoveredStopped" or "recoveredRetained"))) throw ExecutionInvocationCodec.Invalid();
        var blobs = new ImmutableBlobStore(instance, contracts);
        var codec = new ConfigurationPlanCodec(instance, contracts, blobs);
        var states = new RunStateCodec(instance, contracts, blobs);
        var previous = ExecutionInvocationCodec.CurrentPlans(before.Root, state.Attempt, item.StepId)
            .Select(reference => codec.ReadStructure(reference)).ToDictionary(plan => plan.TargetPath, StringComparer.OrdinalIgnoreCase);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var inputs = new Dictionary<ConfigurationAddress, FrozenRunState>();
        foreach (var reference in item.ConfigurationPlans)
        {
            var child = codec.ReadStructure(reference);
            if (!seen.Add(child.TargetPath) || !previous.TryGetValue(child.TargetPath, out var old) || child.RunState is null || old.RunState is null ||
                child.JournalId == old.JournalId || child.OperationId == old.OperationId || child.Kind != JournalKind.ConfigExchange ||
                !FileTreeDelta.Compare(old.Before.Snapshot, child.Before.Snapshot).Changes.IsEmpty) throw ExecutionInvocationCodec.Invalid();
            var saved = states.ReadStructure(child.RunState);
            RunConfigurationBinding.Require(saved, item.StepId, child.TargetPath, state.Attempt, state.Total, blobs, contracts, laterInvocation: true, taskRetry: item.TaskRetry);
            if (!inputs.TryAdd(saved.Source.Address, saved)) throw ExecutionInvocationCodec.Invalid();
        }
        if (!seen.SetEquals(previous.Keys)) throw ExecutionInvocationCodec.Invalid();
        if (inputs.Count != 0) PreparedRetryOrigin.RequireInvocationSource(instance, database, contracts, state.Attempt, state.Total, item, inputs);
    }

    internal static void RequireNewInside(SqliteConnection connection, SqliteTransaction transaction,
        AttemptWriterState state, JournalDocument before, JournalDocument next)
    {
        var old = ExecutionInvocationCodec.Read(before.Root, state.Total);
        foreach (var item in ExecutionInvocationCodec.Read(next.Root, state.Total).Skip(old.Length))
        {
            ExecutionCancellationRows.RequireDispatchOpen(connection, transaction, state.Journal.JournalId);
            ExecutionStepAdmission.RequireJobsInside(connection, transaction, state.Journal.JournalId,
                WriterIntentCodec.ReadAllJobs(before.Root, state.Total));
            var prior = state with { Latest = before, Steps = WriterIntentCodec.Read(before.Root, state.Total) };
            AttemptTerminalEvidence.RequireRestoredInside(connection, transaction, prior, stepId: item.StepId);
            using var command = connection.CreateCommand(); command.Transaction = transaction;
            foreach (var job in prior.AllJobs.Where(job => job.StepId == item.StepId))
            {
                command.Parameters.Clear(); command.Parameters.AddWithValue("$parent", state.Journal.JournalId.ToString("D"));
                command.Parameters.AddWithValue("$job", job.JobName); command.Parameters.AddWithValue("$writer", job.WriterId.ToString("D"));
                command.CommandText = "SELECT EXISTS(SELECT 1 FROM main.journal_writer_job_closures WHERE journal_id=$parent AND job_name=$job AND writer_id=$writer);";
                if ((long)command.ExecuteScalar()! != 1) throw ExecutionInvocationCodec.Invalid();
            }
        }
    }
}

internal static class ExecutionInvocationHistory
{
    internal static ImmutableDictionary<string, int> Before(RuntimeInstance instance, StateDatabase database,
        ContractReader contracts, RunAttemptPlan attempt, ExecutionPlan total)
    {
        if (attempt.AttemptOrdinal == 1) return ImmutableDictionary<string, int>.Empty;
        var result = total.Steps.ToDictionary(step => step.StepId, _ => 0, StringComparer.Ordinal);
        using var connection = DatabaseConnections.Open(database.DatabasePath, readOnly: true, domains: DatabaseDomain.Runtime | DatabaseDomain.Records);
        using var transaction = DatabaseConnections.BeginRead(connection);
        var source = StartSourceRows.Read(connection, transaction, Guid.Parse(attempt.OperationId));
        StartSourceRows.RequireAttempt(source, attempt);
        for (var ordinal = attempt.AttemptOrdinal - 1; ordinal >= 1; ordinal--)
        {
            if (source.RetrySourceOperationId is not { } operation) throw ExecutionInvocationCodec.Invalid();
            var previous = StartSourceRows.Read(connection, transaction, operation);
            var registered = RecoveryJournalRegistry.ReadInside(connection, transaction, previous.JournalId);
            var plan = AttemptPreparedBinding.ReadPlan(instance, contracts, registered);
            StartSourceRows.RequireAttempt(previous, plan);
            var retry = ExecutionRetryRows.After(connection, transaction, Guid.Parse(plan.AttemptId)) ?? throw ExecutionInvocationCodec.Invalid();
            var journal = new JournalStore(instance, contracts).Read(registered.JournalId);
            var receipt = AttemptReceiptStore.ReadInside(connection, transaction, Guid.Parse(plan.AttemptId)) ?? throw ExecutionInvocationCodec.Invalid();
            if (previous.AttemptOrdinal != ordinal || previous.RunId != source.RunId || previous.Identity.SubjectId != source.Identity.SubjectId ||
                registered.Phase != JournalPhase.Committed || registered.Revision != journal.Revision ||
                receipt != AttemptPreparedBinding.ExpectedReceipt(plan, journal) || retry.Frozen.NextOperationId != source.Identity.OperationId ||
                retry.PreviousPlan != AttemptPreparedBinding.Reference(instance, registered) ||
                retry.TotalPlan != AttemptPlanCodec.Reference(attempt.ExecutionPlan) ||
                AttemptPlanCodec.Reference(plan.ExecutionPlan) != AttemptPlanCodec.Reference(attempt.ExecutionPlan)) throw ExecutionInvocationCodec.Invalid();
            foreach (var pair in Count(journal.Root, total)) result[pair.Key] = checked(result[pair.Key] + pair.Value);
            source = previous;
        }
        if (source.RetrySourceOperationId is not null || result.Any(pair => pair.Value > (total.Steps.Single(step => step.StepId == pair.Key).MaximumAttempts ?? 1)))
            throw ExecutionInvocationCodec.Invalid();
        return result.ToImmutableDictionary(StringComparer.Ordinal);
    }

    internal static ImmutableDictionary<string, int> Count(JsonElement journal, ExecutionPlan total)
    {
        var markers = ExecutionInvocationCodec.Read(journal, total);
        var jobs = WriterIntentCodec.ReadJobs(journal, total);
        return total.Steps.ToImmutableDictionary(step => step.StepId,
            step => markers.Count(item => item.StepId == step.StepId) + (jobs.Any(job => job.StepId == step.StepId) ? 1 : 0), StringComparer.Ordinal);
    }

    internal static bool CanResume(ExecutionPlan total, JsonElement journal, IReadOnlyDictionary<string, int> previous)
    {
        var current = Count(journal, total);
        return total.Steps.All(step => current[step.StepId] + (previous.TryGetValue(step.StepId, out var count) ? count : 0) < (step.MaximumAttempts ?? 1));
    }
}

internal sealed record ExecutionInvocation(string Format, int Version, string StepId, int Ordinal,
    long BeforeRevision, string PreviousJobName, DateTimeOffset PreparedAtUtc,
    ImmutableArray<BlobReference> ConfigurationPlans)
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ExecutionTaskRetry? TaskRetry { get; init; }
}

internal static class ExecutionInvocationCodec
{
    internal const string Format = "nexus.execution.invocation";
    private static readonly HashSet<string> ScopedFormats = new(StringComparer.Ordinal)
    {
        WriterIntentCodec.Format, StepResultCodec.Format, HookResultCodec.Format,
        RetryHookCodec.Format, BindingSuppressionCodec.Format, ProviderTerminalCodec.Format,
        TargetJobCodec.Format, PcStageCodec.Format, AndroidControlCodec.RegistrationFormat,
        AndroidStartupCodec.Format, AndroidCollectedCodec.Format, ProviderLauncherCodec.Format,
        ExecutionCaptureCodec.Format, PreviewDomainCodec.Format, BindingTimeoutCodec.Format
    };

    internal static bool IsParticipant(JsonElement participant)
    {
        if (!MatchesFormat(participant)) return false;
        _ = Decode(participant); return true;
    }

    private static bool MatchesFormat(JsonElement participant) =>
        participant.GetProperty("identity").TryGetProperty("format", out var format) && format.GetString() == Format;

    internal static Dictionary<string, int> ReadOrdinals(AttemptJournalView view)
    {
        var ordinals = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var participant in view.Participants)
        {
            if (!MatchesFormat(participant)) continue;
            var invocation = Decode(participant);
            ordinals[invocation.StepId] = Math.Max(ordinals.GetValueOrDefault(invocation.StepId, 1), invocation.Ordinal);
        }
        return ordinals;
    }

    internal static ExecutionInvocation Decode(JsonElement participant)
    {
        if (AttemptReadScope.TryInvocation(participant, out var saved)) return saved;
        var identity = participant.GetProperty("identity");
        var value = identity.Deserialize<ExecutionInvocation>(StepResultCodec.Options) ?? throw Invalid();
        if (!JsonElement.DeepEquals(identity, JsonSerializer.SerializeToElement(value, StepResultCodec.Options)) ||
            value.Format != Format || value.Version != 1 || string.IsNullOrWhiteSpace(value.StepId) || value.StepId.Length > 120 ||
            value.Ordinal is < 2 or > 99 || value.BeforeRevision is < 1 or > 4095 || value.PreparedAtUtc.Offset != TimeSpan.Zero ||
            value.PreviousJobName is not { Length: 50 } || !value.PreviousJobName.StartsWith("Local\\NexusWorker-", StringComparison.Ordinal) ||
            !Guid.TryParseExact(value.PreviousJobName[18..], "N", out var job) || job == Guid.Empty ||
            value.PreviousJobName[18..] != job.ToString("N") || value.ConfigurationPlans.IsDefault || value.ConfigurationPlans.Length > 32 ||
            value.ConfigurationPlans.Distinct().Count() != value.ConfigurationPlans.Length || participant.GetProperty("kind").GetString() != "database")
            throw Invalid();
        foreach (var plan in value.ConfigurationPlans) StepEvidenceCodec.Plan(plan);
        if (value.TaskRetry is { } retry)
        {
            StepEvidenceCodec.Plan(retry.PreviousRaw); StepEvidenceCodec.Plan(retry.PreviousObservation);
            if (retry.PreviousBinding is null || retry.PreviousBinding.StepId != value.StepId || retry.PreviousBinding.JobName != value.PreviousJobName ||
                retry.Selection is null || retry.Selection.Targets.IsDefaultOrEmpty || retry.Selection.Targets.Length > 4096 ||
                retry.Selection.SelectedIds.IsDefaultOrEmpty || retry.Selection.SelectedIds.Length > 4096)
                throw Invalid();
        }
        return AttemptReadScope.RememberInvocation(participant, value);
    }

    internal static ImmutableArray<ExecutionInvocation> Read(AttemptJournalView journal, ExecutionPlan total)
    {
        if (AttemptReadScope.TryRead<ExecutionInvocation>(journal, total, out var saved)) return saved;
        var result = ImmutableArray.CreateBuilder<ExecutionInvocation>();
        foreach (var participant in journal.Participants.Where(IsParticipant))
        {
            var value = Decode(participant);
            var step = total.Steps.SingleOrDefault(step => step.StepId == value.StepId) ?? throw Invalid();
            var ordinal = result.Where(prior => prior.StepId == value.StepId).Select(prior => prior.Ordinal).DefaultIfEmpty(1).Max() + 1;
            if (value.Ordinal != ordinal || ordinal > (step.MaximumAttempts ?? 1) ||
                value.BeforeRevision >= journal.Root.GetProperty("revision").GetInt64() ||
                value.ConfigurationPlans.Length != total.ConfigurationSlots.Count(slot => slot.StepId == value.StepId) ||
                participant.GetProperty("resourceKey").GetString() != "automation:" + step.ScriptId) throw Invalid();
            result.Add(value);
        }
        return AttemptReadScope.Remember(journal, total, result.ToImmutable());
    }

    internal static int OrdinalForJob(JsonElement journal, string stepId, string jobName)
    {
        var starts = journal.GetProperty("participants").EnumerateArray().Where(IsParticipant).Select(Decode)
            .Where(item => item.StepId == stepId).ToArray();
        return starts.SingleOrDefault(item => item.PreviousJobName == jobName)?.Ordinal - 1 ??
            starts.Select(item => item.Ordinal).DefaultIfEmpty(1).Max();
    }

    internal static int Ordinal(AttemptJournalView journal, string stepId) => journal.Participants
        .Where(IsParticipant).Select(Decode).Where(item => item.StepId == stepId).Select(item => item.Ordinal).DefaultIfEmpty(1).Max();

    // Only invocation-owned events are projected. Configuration completion and unknown formats remain visible to their owners.
    internal static AttemptJournalView Current(AttemptJournalView journal)
    {
        if (journal.IsProjected) return journal;
        if (AttemptReadScope.TryProjection(journal, out var saved)) return saved;
        var participants = journal.Participants.ToArray();
        var starts = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var index = 0; index < participants.Length; index++)
            if (IsParticipant(participants[index])) starts[Decode(participants[index]).StepId] = index;
        if (starts.Count == 0) return AttemptReadScope.RememberProjection(journal, journal);
        var selected = participants.Where((participant, index) =>
        {
            var step = ScopedStep(participant);
            return step is null || !starts.TryGetValue(step, out var start) || index > start;
        });
        var projected = selected.ToImmutableArray();
        return AttemptReadScope.RememberProjection(journal,
            projected.Length == participants.Length ? journal : new(journal.Root, Selection: projected));
    }

    internal static IReadOnlyList<AttemptJournalView> Views(AttemptJournalView journal)
    {
        if (AttemptReadScope.TryViews(journal, out var saved)) return saved;
        var result = new List<AttemptJournalView>();
        var index = 0;
        foreach (var participant in journal.Participants)
        {
            if (IsParticipant(participant)) result.Add(journal with { Limit = index });
            index++;
        }
        result.Add(journal); return AttemptReadScope.RememberViews(journal, result.AsReadOnly());
    }

    internal static AttemptJournalView ForJob(AttemptJournalView journal, ExecutionPlan total, string jobName) => Views(journal)
        .LastOrDefault(view => WriterIntentCodec.ReadJobs(view, total).Any(job => job.JobName == jobName)) is var view && view.Root.ValueKind != JsonValueKind.Undefined
            ? Current(view) : throw Invalid();

    internal static ImmutableArray<RunAttemptPlanConfigurationPlansItem> Plans(JsonElement journal, ExecutionPlan total, RunAttemptPlan attempt) =>
        attempt.ConfigurationPlans.Concat(Read(journal, total).SelectMany(item => item.ConfigurationPlans.Select(plan =>
            new RunAttemptPlanConfigurationPlansItem { StepId = item.StepId,
                Plan = new() { RelativePath = plan.RelativePath, Sha256 = plan.Sha256, SizeBytes = plan.SizeBytes } }))).ToImmutableArray();

    internal static ImmutableArray<BlobReference> CurrentPlans(JsonElement journal, RunAttemptPlan attempt, string stepId)
    {
        var invocation = journal.GetProperty("participants").EnumerateArray().Where(IsParticipant).Select(Decode)
            .LastOrDefault(item => item.StepId == stepId);
        return invocation?.ConfigurationPlans ?? attempt.ConfigurationPlans.Where(item => item.StepId == stepId)
            .Select(item => AttemptPlanCodec.Reference(item.Plan)).ToImmutableArray();
    }

    internal static ImmutableArray<BlobReference> PlansForOrdinal(JsonElement journal, ExecutionPlan total, RunAttemptPlan attempt, string stepId, int ordinal) =>
        ordinal == 1 ? attempt.ConfigurationPlans.Where(item => item.StepId == stepId).Select(item => AttemptPlanCodec.Reference(item.Plan)).ToImmutableArray() :
        Read(journal, total).Single(item => item.StepId == stepId && item.Ordinal == ordinal).ConfigurationPlans;

    internal static int PlanOrdinal(JsonElement journal, ExecutionPlan total, RunAttemptPlan attempt, string stepId, BlobReference reference) =>
        attempt.ConfigurationPlans.Any(item => item.StepId == stepId && AttemptPlanCodec.Reference(item.Plan) == reference) ? 1 :
        Read(journal, total).Single(item => item.StepId == stepId && item.ConfigurationPlans.Contains(reference)).Ordinal;

    internal static JsonObject Participant(ExecutionInvocation value, string scriptId) => new()
    {
        ["participantId"] = Guid.NewGuid().ToString("D"), ["kind"] = "database", ["resourceKey"] = "automation:" + scriptId,
        ["identity"] = JsonSerializer.SerializeToNode(value, StepResultCodec.Options)
    };

    private static string? ScopedStep(JsonElement participant)
    {
        var identity = participant.GetProperty("identity");
        if (!identity.TryGetProperty("format", out var format) || !ScopedFormats.Contains(format.GetString()!)) return null;
        if (identity.TryGetProperty("stepId", out var step)) return step.GetString();
        var owner = identity.TryGetProperty("binding", out var binding) ? binding : identity.GetProperty("source");
        if (owner.TryGetProperty("primary", out var primary)) owner = primary;
        return owner.GetProperty("stepId").GetString();
    }

    internal static IOException Invalid() => new("execution_invocation_unbound");
}
