using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Nodes;
using NexusPipeline.Core.Configuration;
using NexusPipeline.Core.Execution;
using NexusPipeline.Extensibility.Protocol;
using NexusPipeline.Platform.Serialization;
using NexusPipeline.Platform.Storage.Configuration;
using NexusPipeline.Platform.Storage.Journals;

namespace NexusPipeline.Platform.Storage.Execution;

internal sealed record ExecutionJournalCapacity(long Revisions, long MaximumDocumentBytes, long HistoryBytes, int Participants, int Receipts, int Jobs)
{
    internal bool IsCompleteMeasurement { get; init; } = true;
    internal bool Fits => IsCompleteMeasurement && Revisions <= JournalCapacityLimits.MaximumRevisions && MaximumDocumentBytes <= StrictJson.MaximumBytes &&
        HistoryBytes <= JournalCapacityLimits.MaximumHistoryBytes && Participants <= JournalCapacityLimits.MaximumParticipants &&
        Receipts <= JournalCapacityLimits.MaximumReceipts && Jobs <= JournalCapacityLimits.MaximumOwnedJobs;
}

internal static class JournalCapacityPolicy
{
    internal const string Reason = "execution_journal_capacity_exceeded";
    private static readonly Guid Placeholder = Guid.Parse("ffffffff-ffff-ffff-ffff-ffffffffffff");
    private static readonly string Digest = new('f', 64);
    private const string Timestamp = "9999-12-31T23:59:59.9999999Z";

    internal static void Require(ExecutionPlan plan)
    {
        if (!Measure(plan).Fits) throw new ExecutionSelectionException(Reason);
    }

    internal static ExecutionJournalCapacity Measure(ExecutionPlan plan)
    {
        if (plan.Steps.Count == 0) throw new ArgumentException("execution_steps_invalid");
        var androidJobs = plan.Steps.Where(step => step.Target?.Kind == "android")
            .Sum(step => AndroidControlCapacity.Derive(step).MaximumCommands * checked((int)(step.MaximumAttempts ?? 1)));
        // A rejected job bound must not allocate one journal event for every second of a multi-day wait.
        if (androidJobs > JournalCapacityLimits.MaximumOwnedJobs)
            return new(0, 0, 0, 0, 0, JournalCapacityLimits.MaximumOwnedJobs + 1) { IsCompleteMeasurement = false };
        var events = new List<(long Bytes, int Participants, int Receipts)>();
        foreach (var step in plan.Steps)
        {
            if (step.TotalTimeoutSeconds is > 0)
                Add(events, BindingTimeoutCodec.Participant(new(BindingTimeoutCodec.Format, 1, step.StepId, 99,
                    JournalCapacityLimits.MaximumRevisions, 9007199254740991), step.ScriptId));
            for (var invocation = 1; invocation <= (step.MaximumAttempts ?? 1); invocation++)
            {
                if (invocation > 1)
                    Add(events, ExecutionInvocationCodec.Participant(new(ExecutionInvocationCodec.Format, 1, step.StepId, invocation,
                        JournalCapacityLimits.MaximumRevisions, "Local\\NexusWorker-" + Placeholder.ToString("N"), DateTimeOffset.MaxValue,
                        plan.ConfigurationSlots.Where(slot => slot.StepId == step.StepId).Select(_ =>
                            new BlobReference($"blobs/plans/{Digest}.json", StrictJson.MaximumBytes, Digest)).ToImmutableArray())
                            { TaskRetry = RetryCapacity(step) }, step.ScriptId));
                AddStep(events, step);
                if (plan.CaptureRuntime is not null && ExecutionCaptureInputs.IsSupported(step))
                { AddCapture(events, step); AddPreviewDomain(events, step); }
            }
        }
        AddFinalization(events, plan);
        var groups = plan.ConfigurationSlots.GroupBy(slot => slot.StepId, StringComparer.Ordinal);
        foreach (var group in groups)
        {
            var slots = group.Select(slot => ConfigurationRevisionService.ResourceKey(PlanConfigurationSlots.Address(slot))).Order(StringComparer.Ordinal).ToImmutableArray();
            var intent = new SnapshotCompletionIntent(SnapshotCompletionIntent.FormatId, 1, Placeholder,
                Placeholder, Digest, Placeholder, Placeholder, new string('\uffff', 256), "interrupted", 9007199254740990, Digest, slots);
            for (var invocation = 1; invocation <= (plan.Steps.Single(step => step.StepId == group.Key).MaximumAttempts ?? 1); invocation++)
                Add(events, SnapshotIntentCodec.Participant(intent), Receipt("prepare-snapshot-completion", "completed", "configuration_snapshot_completion_pending"));
        }
        var journal = new RegisteredJournal(Placeholder, Placeholder, JournalKind.RunAttempt, JournalPhase.Prepared, 1,
            $"blobs/plans/{Digest}.json", Digest, plan.ResourceKeys);
        var initial = JsonSerializer.SerializeToNode(ExecutionInitialJournal.Create(Guid.Parse(plan.InstanceId), journal, plan.RunId, Placeholder.ToString("D")))!.AsObject();
        initial["phase"] = "quarantined"; initial["revision"] = JournalCapacityLimits.MaximumRevisions; initial["updatedAt"] = Timestamp;
        long size = JsonSerializer.SerializeToUtf8Bytes(initial).LongLength, history = size;
        // Larger increments first bound every valid ordering of append-only complete journal revisions.
        foreach (var item in events.OrderByDescending(item => item.Bytes)) { size = checked(size + item.Bytes); history = checked(history + size); }
        const int phaseOnlyRevisions = 4;
        history = checked(history + size * phaseOnlyRevisions);
        return new(1L + events.Count + phaseOnlyRevisions, size, history,
            events.Sum(item => item.Participants), events.Sum(item => item.Receipts), plan.Steps.Sum(step => checked((int)(step.MaximumAttempts ?? 1)) *
                (1 + (step.JudgementWorker is null ? 0 : 1) + (step.Hooks?.Count ?? 0) +
                (plan.CaptureRuntime is not null && ExecutionCaptureInputs.IsSupported(step) ? (MuMuCaptureSequence.IsPaired(step) ? MuMuCaptureSequence.MaximumJobs : 1) + 1 : 0) +
                (step.Target is { Kind: "pc", LaunchByHost: true } ? 1 : 0) + (step.Provider?.Launcher is null ? 0 : 1))) + checked((int)androidJobs));
    }

    private static ExecutionTaskRetry? RetryCapacity(ExecutionPlanStep step)
    {
        if (step.Adapter is null || step.Tasks.Count == 0) return null;
        var binding = new StepEvidenceBinding(Placeholder, Placeholder, Placeholder, Placeholder, Placeholder, step.StepId,
            "Local\\NexusWorker-" + Placeholder.ToString("N"), Placeholder, int.MaxValue, Digest);
        var reference = new BlobReference($"blobs/plans/{Digest}.json", StrictJson.MaximumBytes, Digest);
        var ids = step.Tasks.Select(task => task.TaskId).ToImmutableArray();
        return new(binding, reference, reference, new(ids, ids));
    }

    private static void AddStep(List<(long Bytes, int Participants, int Receipts)> events, ExecutionPlanStep step)
    {
        Add(events, BindingSkipCodec.Participant(new(BindingSkipCodec.Format, 1, step.StepId,
            "execution_queue_dependency_not_successful", DateTimeOffset.MaxValue, int.MaxValue), step.ScriptId));
        AddTarget(events, step);
        if (step.Provider?.Launcher is not null)
        {
            var launcher = new ProviderLauncherBinding(Placeholder, Placeholder, Placeholder, Placeholder, step.StepId,
                Placeholder, "Local\\NexusWorker-" + Placeholder.ToString("N"), Placeholder, int.MaxValue, Digest, Placeholder,
                step.Provider.ExtensionId, step.Provider.EntryId);
            foreach (var stage in new[] { "registered", "root", "started" }
                .Concat(Enumerable.Repeat("retentionPrepared", ProviderLauncherCodec.MaximumRetentionIntents)).Append("retained"))
                Add(events, ProviderLauncherCodec.Participant(new(ProviderLauncherCodec.Format, 1, launcher, stage,
                    stage == "registered" ? null : new($"blobs/plans/{Digest}.json", StrictJson.MaximumBytes, Digest)), step.ScriptId));
        }
        var job = "Local\\NexusWorker-" + Placeholder.ToString("N");
        foreach (var stage in new[] { "registered", "closed" })
            Add(events, WriterIntentCodec.Participant(new(WriterIntentCodec.Format, 1, step.StepId, stage, job,
                Placeholder, int.MaxValue, stage == "closed" ? 922337203685477 : null)
                { FileLogBaseline = step.LogTargets.Count == 0 ? null : new($"blobs/plans/{Digest}.json", StrictJson.MaximumBytes, Digest) }, step.ScriptId));
        if (step.JudgementWorker is not null)
            foreach (var stage in new[] { "registered", "closed" })
                Add(events, WriterIntentCodec.Participant(new(WriterIntentCodec.Format, 1, step.StepId, stage, job,
                    Placeholder, int.MaxValue, stage == "closed" ? 922337203685477 : null)
                    { Role = "judgement", JudgementOwnerId = Placeholder }, step.ScriptId));
        foreach (var _ in step.Hooks ?? [])
            for (var count = 0; count < ExecutionHookInputs.MaximumJournalEvents; count++)
                events.Add((ExecutionHookInputs.MaximumJournalEventBytes, 1, 0));
        var binding = new StepEvidenceBinding(Placeholder, Placeholder, Placeholder, Placeholder, Placeholder, step.StepId, job,
            Placeholder, int.MaxValue, Digest);
        if (step.Provider is not null)
            Add(events, ProviderTerminalCodec.Participant(new(ProviderTerminalCodec.Format, 1, binding,
                Placeholder, step.Provider.EntryId, JournalCapacityLimits.MaximumRevisions,
                new($"blobs/plans/{Digest}.json", StrictJson.MaximumBytes, Digest),
                new(EngineOutcome.Failed, new string('z', 80), ProviderProtocolState.MaximumEvents)), step.ScriptId));
        foreach (var stage in new[] { "observed", "observed", "collected" })
            Add(events, StepResultCodec.Participant(new(StepResultCodec.Format, 1, binding, stage,
                new($"blobs/plans/{Digest}.json", StrictJson.MaximumBytes, Digest), EngineOutcome.NotStarted, BusinessOutcome.NotApplicable,
                9007199254740991, new string('\uffff', 120)), step.ScriptId));
    }

    private static void AddCapture(List<(long Bytes, int Participants, int Receipts)> events, ExecutionPlanStep step)
    {
        var primary = new StepEvidenceBinding(Placeholder, Placeholder, Placeholder, Placeholder, Placeholder, step.StepId,
            "Local\\NexusWorker-" + Placeholder.ToString("N"), Placeholder, int.MaxValue, Digest);
        var capture = new ExecutionCaptureBinding(primary, Placeholder, Digest, "Local\\NexusWorker-" + Placeholder.ToString("N"));
        var stages = new List<string> { "source", "root" };
        if (MuMuCaptureSequence.IsPaired(step))
            foreach (var sequence in Enumerable.Range(1, MuMuCaptureSequence.MaximumCommands))
            {
                stages.Add(MuMuCaptureSequence.Stage(sequence, observed: false));
                stages.Add(MuMuCaptureSequence.Stage(sequence, observed: true));
            }
        stages.Add("collected");
        foreach (var stage in stages)
            Add(events, ExecutionCaptureCodec.Participant(new(ExecutionCaptureCodec.Format, 1, capture, stage,
                JournalCapacityLimits.MaximumRevisions, new($"blobs/plans/{Digest}.json", StrictJson.MaximumBytes, Digest)), step.ScriptId));
    }

    private static void AddPreviewDomain(List<(long Bytes, int Participants, int Receipts)> events, ExecutionPlanStep step)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        var producer = NexusPipeline.Platform.Storage.Sqlite.JournalWriterStore.CurrentWriter.Process;
        var primary = new StepEvidenceBinding(Placeholder, Placeholder, Placeholder, Placeholder, Placeholder, step.StepId,
            "Local\\NexusWorker-" + Placeholder.ToString("N"), Placeholder, int.MaxValue, Digest);
        var root = producer with { ProcessId = int.MaxValue, CreatedAtUtc = DateTime.SpecifyKind(DateTime.MaxValue, DateTimeKind.Utc),
            ImagePath = step.ExecutablePath, ImageSha256 = step.ImageSha256 };
        var source = new PreviewDomainSource(primary, Digest, PreviewDomainCodec.Name(primary, Digest),
            producer, root, 0x2208, 64, 268435456);
        foreach (var stage in new[] { "open", "closed" })
            Add(events, PreviewDomainCodec.Participant(new(PreviewDomainCodec.Format, 1, source,
                stage, JournalCapacityLimits.MaximumRevisions, stage == "closed" ? uint.MaxValue : null), step.ScriptId));
    }

    private static void AddTarget(List<(long Bytes, int Participants, int Receipts)> events, ExecutionPlanStep step)
    {
        if (step.Target?.Kind == "android") { AddAndroidTarget(events, step); return; }
        if (step.Target is not { Kind: "pc" }) return;
        Add(events, PcStageCodec.Participant(new(PcStageCodec.Format, 1,
            new(Placeholder, Placeholder, Placeholder, Placeholder, step.StepId, Digest), TargetExecutionOutcome.StoppedByRequest,
            DispatchIntentCodec.TargetReason, new($"blobs/plans/{Digest}.json", StrictJson.MaximumBytes, Digest)), step.ScriptId));
        if (!step.Target.LaunchByHost) return;
        var binding = new ExecutionTargetBinding(Placeholder, Placeholder, Placeholder, Placeholder, step.StepId,
            Placeholder, "Local\\NexusWorker-" + Placeholder.ToString("N"), Placeholder, int.MaxValue, Digest);
        var stages = new[] { "registered", "root", "ready" }
            .Concat(Enumerable.Repeat("retentionPrepared", TargetJobCodec.MaximumRetentionIntents)).Append("recoveredRetained");
        foreach (var stage in stages)
            Add(events, TargetJobCodec.Participant(new(TargetJobCodec.Format, 1, binding, stage,
                stage == "registered" ? null : new($"blobs/plans/{Digest}.json", StrictJson.MaximumBytes, Digest)), step.ScriptId));
    }

    private static void AddAndroidTarget(List<(long Bytes, int Participants, int Receipts)> events, ExecutionPlanStep step)
    {
        var limits = AndroidControlCapacity.Derive(step);
        var binding = new ExecutionTargetBinding(Placeholder, Placeholder, Placeholder, Placeholder, step.StepId,
            Placeholder, "Local\\NexusWorker-" + Placeholder.ToString("N"), Placeholder, int.MaxValue, Digest);
        var reference = new BlobReference($"blobs/plans/{Digest}.json", StrictJson.MaximumBytes, Digest);
        for (var sequence = 0; sequence < limits.MaximumCommands; sequence++)
            Add(events, AndroidControlCodec.Participant(new(AndroidControlCodec.RegistrationFormat, 1, binding,
                1023, AndroidCommandPurpose.CleanupApplication, ExecutionAndroidAction.StartApplication,
                reference, Digest, reference, reference), step.ScriptId));
        var control = new AndroidControlBinding(Placeholder, Placeholder, Placeholder, Placeholder, step.StepId, Digest);
        var commands = Enumerable.Range(0, checked((int)limits.MaximumCommands)).Select(sequence =>
            new TargetCommandFact(Placeholder, sequence, EngineOutcome.Succeeded, new string('f', 80))
            { Kind = TargetCommandKind.ReadOnlyProbe, Observation = TargetCommandObservation.Contradictory }).ToImmutableArray();
        Add(events, AndroidStartupCodec.Participant(new(AndroidStartupCodec.Format, 1, control,
            TargetExecutionOutcome.ReadinessTimedOut, new string('f', 80), commands.Take(checked((int)limits.StartupCommands)).ToImmutableArray(), reference), step.ScriptId));
        Add(events, AndroidCollectedCodec.Participant(new(AndroidCollectedCodec.Format, 1, control, reference,
            commands, new(TargetCleanupOutcome.Completed, new string('f', 80))
            { Verification = TargetCleanupVerification.DeviceOffline, ConfirmationCommands = [Placeholder, Placeholder] }), step.ScriptId));
        if (step.Target?.Android?.Emulator is null) return;
        foreach (var stage in new[] { AndroidRetentionCodec.Prepared, AndroidRetentionCodec.Retained })
            Add(events, AndroidRetentionCodec.Participant(new(AndroidRetentionCodec.Format, 1, binding, stage, reference), step.ScriptId));
    }

    private static void AddFinalization(List<(long Bytes, int Participants, int Receipts)> events, ExecutionPlan plan)
    {
        var close = new DispatchCloseIntent(DispatchIntentCodec.Format, 1, Placeholder, Placeholder, Digest,
            Placeholder, Placeholder, Placeholder, new string('\uffff', 256), DispatchIntentCodec.BudgetReason);
        Add(events, DispatchIntentCodec.Participant(close, plan.Steps[0].ScriptId));
        var suffix = new ExecutionUndispatchedSteps(StepResultCodec.UndispatchedFormat, 1, Placeholder, Placeholder, Digest, Placeholder,
            DispatchIntentCodec.BudgetReason, plan.Steps.Select(step => step.StepId).ToImmutableArray());
        Add(events, StepResultCodec.UndispatchedParticipant(suffix, plan.Steps[0].ScriptId));
        var facts = new AttemptFacts(Placeholder, Placeholder, int.MaxValue, EngineOutcome.NotStarted, BusinessOutcome.NotApplicable,
            LifecycleOutcome.Interrupted, RecoveryOutcome.NotRequired, new string('\uffff', 120));
        var terminal = new AttemptResultIntent(AttemptResultCodec.Format, 1, Placeholder, Placeholder, Placeholder, Digest, "noEffectCancellation", facts);
        Add(events, AttemptResultCodec.Participant(terminal, plan.ResourceKeys[0]), AttemptResultCodec.Receipt(terminal, Timestamp));
        Add(events, null, Receipt("isolate-attempt", "unknown", new string('\uffff', 120)));
        Add(events, null, Receipt(new string('g', 80), "unknown", new string('\uffff', 120)));
    }

    private static JsonObject Receipt(string step, string effect, string reason) => new()
    {
        ["receiptId"] = Placeholder.ToString("D"), ["recordedAt"] = Timestamp, ["stepId"] = step, ["effect"] = effect, ["detailCode"] = reason
    };

    private static void Add(List<(long Bytes, int Participants, int Receipts)> events, JsonObject? participant, JsonObject? receipt = null)
    {
        var bytes = (participant is null ? 0 : JsonSerializer.SerializeToUtf8Bytes(participant).LongLength + 1) +
            (receipt is null ? 0 : JsonSerializer.SerializeToUtf8Bytes(receipt).LongLength + 1);
        events.Add((bytes, participant is null ? 0 : 1, receipt is null ? 0 : 1));
    }
}
