using NexusPipeline.Core.Execution;
using NexusPipeline.Extensibility.Protocol;
using NexusPipeline.Platform.Serialization;
using NexusPipeline.Platform.Storage.Sqlite;
using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace NexusPipeline.Platform.Storage.Execution;

internal sealed class AndroidResultEvidence(RuntimeInstance instance, StateDatabase database, ContractReader contracts,
    AndroidControlEvidence? reader = null)
{
    private readonly AndroidControlEvidence _reader = reader ?? new(instance, database, contracts);

    internal ImmutableArray<TargetCommandFact> CommandFacts(AttemptWriterState state,
        IReadOnlyList<AndroidStartupCommand> commands) => CommandFacts(state.Total, commands);

    private ImmutableArray<TargetCommandFact> CommandFacts(ExecutionPlan total,
        IReadOnlyList<AndroidStartupCommand> commands) => commands.Select(command =>
    {
        var native = _reader.Observation(total, command.Registration, command.Observation, verifyContent: true);
        var meaning = ExecutionAndroidObservations.ReadSaved(_reader, _reader.Request(total, command.Registration), native, command.Observation);
        return Fact(command.Registration, native, meaning);
    }).ToImmutableArray();

    internal IReadOnlyList<BlobReference> RequireStartup(AttemptWriterState state,
        AndroidStartupIntent intent, bool verifyContent) =>
        RequireStartup(state.Latest.Root, state.Total, state.Attempt.ExecutionPlan.Sha256, intent, verifyContent);

    internal IReadOnlyList<BlobReference> RequireStartup(AttemptJournalView journal, ExecutionPlan total, string totalSha256,
        AndroidStartupIntent intent, bool verifyContent)
    {
        var value = _reader.Read<AndroidStartupDocument>(intent.Artifact);
        var registrations = AndroidControlCodec.Read(journal, total, totalSha256)
            .Where(item => item.Binding.StepId == intent.Binding.StepId && !AndroidControlAdmission.IsCleanup(item.Purpose)).ToArray();
        if (value.Format != AndroidStartupCodec.Format || value.Version != 1 || value.Binding != intent.Binding ||
            value.Outcome != intent.Outcome || value.ReasonCode != intent.ReasonCode || value.Commands.IsDefault ||
            value.Commands.Length != registrations.Length ||
            !value.Commands.Select(item => item.Registration).SequenceEqual(registrations)) throw Invalid();
        var references = new List<BlobReference> { intent.Artifact };
        var meanings = new List<(AndroidCommandRegistration Request, ExecutionAndroidObservation Meaning)>();
        for (var index = 0; index < registrations.Length; index++)
        {
            var command = value.Commands[index];
            if (index > 0 && command.Registration.PreviousObservation != value.Commands[index - 1].Observation) throw Invalid();
            var native = _reader.Observation(total, command.Registration, command.Observation, verifyContent);
            RequireFact(command.Registration, native, intent.Commands[index]);
            references.Add(command.Observation); references.AddRange(native.Parts.Select(part => part.Content));
            if (!verifyContent) continue;
            var request = _reader.Request(total, command.Registration);
            var expected = AndroidControlSequence.Next(request.Driver, request.Package, meanings, total.Steps.Single(step => step.StepId == request.Binding.StepId).Target!.Android!.Emulator is not null);
            if (expected is null || expected.Value.Purpose != request.Purpose || expected.Value.Action != request.Action) throw Invalid();
            var meaning = ExecutionAndroidObservations.ReadSaved(_reader, request, native, command.Observation);
            if (Fact(command.Registration, native, meaning) != intent.Commands[index]) throw Invalid();
            meanings.Add((command.Registration, meaning));
        }
        if (verifyContent) RequireStartupOutcome(total, intent, meanings);
        return references.Distinct().ToArray();
    }

    internal IReadOnlyList<BlobReference> RequireCollected(AttemptWriterState state,
        AndroidCollectedIntent intent, bool verifyContent) =>
        RequireCollected(state.Latest.Root, state.Total, state.Attempt.ExecutionPlan.Sha256, intent, verifyContent);

    internal IReadOnlyList<BlobReference> RequireCollected(AttemptJournalView journal, ExecutionPlan total, string totalSha256,
        AndroidCollectedIntent intent, bool verifyContent)
    {
        var references = AndroidCollectedCodec.References(journal, total, totalSha256, _reader, intent, verifyContent).ToList();
        var value = _reader.Read<AndroidCollectedDocument>(intent.Artifact);
        var registrations = AndroidControlCodec.Read(journal, total, totalSha256)
            .Where(item => item.Binding.StepId == intent.Binding.StepId).ToArray();
        var commands = new List<AndroidStartupCommand>();
        var meanings = new List<(AndroidCommandRegistration Request, ExecutionAndroidObservation Meaning)>();
        for (var index = 0; index < registrations.Length; index++)
        {
            var registration = registrations[index];
            var collection = _reader.Collection(total, registration, value.Commands[index].Collection, verifyContent: false);
            var native = _reader.Observation(total, registration, collection.Observation, verifyContent: false);
            RequireFact(registration, native, intent.Commands[index]);
            commands.Add(new(registration, collection.Observation));
            if (verifyContent) meanings.Add((registration, ExecutionAndroidObservations.ReadSaved(_reader,
                _reader.Request(total, registration), native, collection.Observation)));
        }
        if (verifyContent)
        {
            if (!CommandFacts(total, commands).SequenceEqual(intent.Commands)) throw Invalid();
            RequireCleanup(journal, total, totalSha256, intent, meanings);
        }
        return references.Distinct().ToArray();
    }

    private static void RequireStartupOutcome(ExecutionPlan total, AndroidStartupIntent intent,
        IReadOnlyList<(AndroidCommandRegistration Request, ExecutionAndroidObservation Meaning)> observed)
    {
        var target = total.Steps.Single(step => step.StepId == intent.Binding.StepId).Target!;
        var last = observed.LastOrDefault().Meaning;
        var ready = last is { State: ExecutionAndroidState.Foreground, Interpretation: TargetCommandObservation.Complete } &&
            last.ForegroundPackage == target.Android!.Package;
        var failed = observed.Any(item => item.Meaning.State == ExecutionAndroidState.Failed);
        var unknown = observed.Any(item => item.Meaning.State == ExecutionAndroidState.Unknown);
        var valid = intent.Outcome switch
        {
            TargetExecutionOutcome.Skipped => !target.LaunchByHost && observed.Count == 0,
            TargetExecutionOutcome.ControlledReady => ready && !failed && !unknown,
            TargetExecutionOutcome.LaunchFailed => failed,
            TargetExecutionOutcome.ReadinessUnknown => unknown && !ready,
            TargetExecutionOutcome.ReadinessTimedOut => !ready && !failed,
            TargetExecutionOutcome.Blocked => observed.Count == 0,
            TargetExecutionOutcome.StoppedByRequest => true,
            _ => false
        };
        if (!valid) throw Invalid();
    }

    private static void RequireCleanup(AttemptJournalView journal, ExecutionPlan total, string totalSha256,
        AndroidCollectedIntent intent,
        IReadOnlyList<(AndroidCommandRegistration Request, ExecutionAndroidObservation Meaning)> observed)
    {
        var expected = TargetCleanupFacts.ReadAtAndroidCollection(total, journal, totalSha256, intent.Binding.StepId);
        var cleanup = intent.Cleanup;
        var actual = observed.Where(item => AndroidControlAdmission.IsCleanup(item.Request.Purpose)).ToArray();
        var application = expected.Application == TargetCleanupAction.StopAndroidApplication;
        var device = expected.Device == TargetCleanupAction.ShutdownAndroid;
        var driver = total.Steps.Single(step => step.StepId == intent.Binding.StepId).Target!.Android!.Driver;
        var prefix = new List<(AndroidCommandRegistration Request, ExecutionAndroidObservation Meaning)>();
        foreach (var item in observed)
        {
            if (!AndroidControlAdmission.IsCleanup(item.Request.Purpose))
            {
                if (prefix.Count != 0) throw Invalid();
                continue;
            }
            AndroidControlSequence.RequireCleanupNext(driver, item.Request.Purpose, expected, prefix);
            prefix.Add(item);
        }
        if (!application && !device)
        {
            if (actual.Length != 0 || cleanup.Outcome != TargetCleanupOutcome.NotRequired ||
                cleanup.Verification != TargetCleanupVerification.None) throw Invalid();
            return;
        }
        var verification = cleanup.Outcome == TargetCleanupOutcome.Completed
            ? device ? TargetCleanupVerification.DeviceOffline : TargetCleanupVerification.Application
            : TargetCleanupVerification.None;
        if (cleanup.Outcome == TargetCleanupOutcome.NotRequired || cleanup.Verification != verification) throw Invalid();
        var appDone = !application || actual.Any(item => item.Request.Purpose == AndroidCommandPurpose.CleanupApplication &&
            item.Meaning.State == ExecutionAndroidState.ApplicationStopRequested);
        var shutdown = actual.Any(item => item.Request.Purpose == AndroidCommandPurpose.ShutdownDevice &&
            item.Meaning.State == ExecutionAndroidState.ShutdownRequested);
        var last = actual.TakeLast(2).ToArray();
        var offline = last.Length == 2 && last.All(item => item.Request.Purpose == AndroidCommandPurpose.ConfirmOffline &&
            item.Meaning.State == ExecutionAndroidState.Offline && item.Meaning.Interpretation == TargetCommandObservation.ExpectedOffline);
        var failed = actual.Any(item => item.Meaning.State == ExecutionAndroidState.Failed);
        var completed = appDone && (!device || shutdown && offline) && !failed;
        if (cleanup.Outcome == TargetCleanupOutcome.Completed && !completed ||
            cleanup.Outcome == TargetCleanupOutcome.Failed && !failed &&
                (completed || actual.Length == 0 || actual.Any(item => item.Meaning.State == ExecutionAndroidState.Unknown)) ||
            cleanup.Outcome == TargetCleanupOutcome.Unverified && completed) throw Invalid();
    }

    private static TargetCommandFact Fact(AndroidCommandRegistration registration,
        AndroidCommandObservation native, ExecutionAndroidObservation meaning) =>
        new(registration.Binding.TargetId, registration.Sequence, native.Engine, meaning.State switch
        {
            ExecutionAndroidState.Unknown => "execution_android_command_unverified",
            ExecutionAndroidState.Failed => "execution_android_command_failed",
            _ => "execution_android_command_observed"
        }) { Kind = Kind(registration.Action), Observation = meaning.Interpretation };

    private static void RequireFact(AndroidCommandRegistration registration, AndroidCommandObservation native,
        TargetCommandFact fact)
    {
        if (fact.CommandId != registration.Binding.TargetId || fact.Sequence != registration.Sequence || fact.Engine != native.Engine ||
            fact.Kind != Kind(registration.Action) || native.Completion == AndroidResponseCompletion.OwnedLaunchHandoff && fact.Observation != TargetCommandObservation.OwnedHandoff ||
            native.Completion is not (AndroidResponseCompletion.NaturalRootExitAndEofBeforeStop or AndroidResponseCompletion.OwnedLaunchHandoff) &&
                fact.Observation is not (TargetCommandObservation.Unverified or TargetCommandObservation.Incomplete)) throw Invalid();
    }
    private static TargetCommandKind Kind(ExecutionAndroidAction action) => action is ExecutionAndroidAction.DeviceState or
        ExecutionAndroidAction.Foreground or ExecutionAndroidAction.OfflineProbe ? TargetCommandKind.ReadOnlyProbe : TargetCommandKind.Effect;
    private static IOException Invalid() => new("execution_android_result_evidence_unbound");
}

internal sealed record AndroidStartupCommand(AndroidCommandRegistration Registration, BlobReference Observation);
internal sealed record AndroidStartupDocument(string Format, int Version, AndroidControlBinding Binding,
    TargetExecutionOutcome Outcome, string ReasonCode, ImmutableArray<AndroidStartupCommand> Commands);
internal sealed record AndroidStartupIntent(string Format, int Version, AndroidControlBinding Binding,
    TargetExecutionOutcome Outcome, string ReasonCode, ImmutableArray<TargetCommandFact> Commands, BlobReference Artifact);

internal static class AndroidStartupCodec
{
    internal const string Format = "nexus.execution.android-startup";

    internal static bool IsParticipant(JsonElement participant) => participant.GetProperty("identity")
        .TryGetProperty("format", out var value) && value.GetString() == Format;

    internal static ImmutableArray<AndroidStartupIntent> Read(AttemptJournalView journal, ExecutionPlan total, string totalSha256)
    {
        journal = ExecutionInvocationCodec.Current(journal);
        var result = ImmutableArray.CreateBuilder<AndroidStartupIntent>();
        var registrations = AndroidControlCodec.Read(journal, total, totalSha256);
        foreach (var participant in journal.Participants.Where(IsParticipant))
        {
            var identity = participant.GetProperty("identity");
            var decoded = ExecutionParticipantText.Read<AndroidStartupIntent>(identity, StepResultCodec.Options, Invalid);
            var value = decoded.Value;
            var binding = value.Binding ?? throw Invalid();
            RequireBinding(journal, total, totalSha256, participant, binding);
            var input = total.Steps.Single(step => step.StepId == binding.StepId);
            var prefix = registrations.Where(item => item.Binding.StepId == binding.StepId &&
                !AndroidControlAdmission.IsCleanup(item.Purpose)).ToArray();
            if (!decoded.MatchesOriginal ||
                value.Format != Format || value.Version != 1 || result.Any(item => item.Binding.StepId == binding.StepId) ||
                !Allowed(value.Outcome) || value.Commands.IsDefault || value.Commands.Length != prefix.Length ||
                !value.Commands.Select(item => item.CommandId).SequenceEqual(prefix.Select(item => item.Binding.TargetId)) ||
                value.Outcome == TargetExecutionOutcome.Skipped && (input.Target!.LaunchByHost || prefix.Length != 0) ||
                value.Outcome == TargetExecutionOutcome.ControlledReady && prefix.Length == 0 ||
                (value.Outcome == TargetExecutionOutcome.StoppedByRequest
                    ? DispatchIntentCodec.Read(journal, total)?.ReasonCode != value.ReasonCode
                    : value.ReasonCode != Reason(value.Outcome))) throw Invalid();
            RequireCommandFacts(binding.StepId, value.Commands);
            StepEvidenceCodec.Plan(value.Artifact); result.Add(value);
        }
        return result.ToImmutable();
    }

    internal static void RequireBinding(AttemptJournalView journal, ExecutionPlan total, string totalSha256, JsonElement participant,
        AndroidControlBinding binding)
    {
        var input = total.Steps.SingleOrDefault(step => step.StepId == binding.StepId) ?? throw Invalid();
        if (input.Target?.Kind != "android" || binding.InstanceId.ToString("D") != total.InstanceId ||
            binding.ParentJournalId != journal.Root.GetProperty("journalId").GetGuid() ||
            binding.ParentOperationId != journal.Root.GetProperty("operationId").GetGuid() ||
            binding.AttemptId != journal.Root.GetProperty("attemptId").GetGuid() || binding.TotalPlanSha256 != totalSha256 ||
            participant.GetProperty("kind").GetString() != "process" ||
            participant.GetProperty("resourceKey").GetString() != "automation:" + input.ScriptId) throw Invalid();
    }

    internal static void RequireCommandFacts(string stepId, ImmutableArray<TargetCommandFact> commands,
        TargetCleanupFact? cleanup = null)
    {
        if (commands.IsDefault) throw Invalid();
        _ = TargetFactsPolicy.Compose(new(stepId, EngineOutcome.NotStarted, BusinessOutcome.Unverified,
            "execution_primary_not_dispatched"), new(TargetExecutionOutcome.Skipped, "execution_android_validation"), commands, cleanup);
    }

    internal static string Reason(TargetExecutionOutcome outcome) => outcome switch
    {
        TargetExecutionOutcome.Skipped => "execution_android_launch_disabled",
        TargetExecutionOutcome.ControlledReady => "execution_android_controlled_ready",
        TargetExecutionOutcome.LaunchFailed => "execution_android_launch_failed",
        TargetExecutionOutcome.ReadinessTimedOut => "execution_android_readiness_timeout",
        TargetExecutionOutcome.ReadinessUnknown => "execution_android_readiness_unknown",
        TargetExecutionOutcome.Blocked => "execution_android_launch_blocked",
        _ => throw Invalid()
    };

    internal static bool Allowed(TargetExecutionOutcome outcome) => outcome is TargetExecutionOutcome.Skipped or
        TargetExecutionOutcome.ControlledReady or TargetExecutionOutcome.LaunchFailed or
        TargetExecutionOutcome.ReadinessTimedOut or TargetExecutionOutcome.ReadinessUnknown or
        TargetExecutionOutcome.Blocked or TargetExecutionOutcome.StoppedByRequest;

    internal static JsonObject Participant(AndroidStartupIntent item, string scriptId) => new()
    {
        ["participantId"] = Guid.NewGuid().ToString("D"), ["kind"] = "process", ["resourceKey"] = "automation:" + scriptId,
        ["identity"] = JsonSerializer.SerializeToNode(item, StepResultCodec.Options)
    };
    private static IOException Invalid() => new("execution_android_startup_unrecognized");
}

internal sealed record AndroidControlBinding(Guid InstanceId, Guid ParentJournalId, Guid ParentOperationId,
    Guid AttemptId, string StepId, string TotalPlanSha256);
internal sealed record AndroidCollectedCommand(ExecutionTargetBinding Binding, BlobReference Collection);
internal sealed record AndroidCollectedDocument(string Format, int Version, AndroidControlBinding Binding,
    ImmutableArray<AndroidCollectedCommand> Commands, BlobReference Startup, TargetCleanupFact Cleanup);
internal sealed record AndroidCollectedIntent(string Format, int Version, AndroidControlBinding Binding,
    BlobReference Artifact, ImmutableArray<TargetCommandFact> Commands, TargetCleanupFact Cleanup);

internal static class AndroidCollectedCodec
{
    internal const string Format = "nexus.execution.android-collected";

    internal static bool IsParticipant(JsonElement participant) =>
        participant.GetProperty("identity").TryGetProperty("format", out var value) && value.GetString() == Format;

    internal static ImmutableArray<AndroidCollectedIntent> Read(AttemptJournalView journal, ExecutionPlan total, string totalSha256)
    {
        journal = ExecutionInvocationCodec.Current(journal);
        var result = ImmutableArray.CreateBuilder<AndroidCollectedIntent>();
        foreach (var participant in journal.Participants.Where(IsParticipant))
        {
            var identity = participant.GetProperty("identity");
            var decoded = ExecutionParticipantText.Read<AndroidCollectedIntent>(identity, StepResultCodec.Options, Invalid);
            var item = decoded.Value;
            var binding = item.Binding ?? throw Invalid();
            AndroidStartupCodec.RequireBinding(journal, total, totalSha256, participant, binding);
            if (!decoded.MatchesOriginal ||
                item.Format != Format || item.Version != 1 || participant.GetProperty("kind").GetString() != "process" ||
                binding.InstanceId.ToString("D") != total.InstanceId || binding.ParentJournalId != journal.Root.GetProperty("journalId").GetGuid() ||
                binding.ParentOperationId != journal.Root.GetProperty("operationId").GetGuid() || binding.AttemptId != journal.Root.GetProperty("attemptId").GetGuid() ||
                binding.TotalPlanSha256 != totalSha256 || result.Any(value => value.Binding.StepId == binding.StepId))
                throw Invalid();
            var step = total.Steps.SingleOrDefault(step => step.StepId == binding.StepId) ?? throw Invalid();
            if (step.Target?.Kind != "android" || participant.GetProperty("resourceKey").GetString() != "automation:" + step.ScriptId) throw Invalid();
            var registrations = AndroidControlCodec.Read(journal, total, totalSha256)
                .Where(value => value.Binding.StepId == binding.StepId).ToArray();
            var startup = AndroidStartupCodec.Read(journal, total, totalSha256)
                .SingleOrDefault(value => value.Binding == binding) ?? throw Invalid();
            if (item.Cleanup is null || item.Commands.IsDefault || item.Commands.Length != registrations.Length ||
                !item.Commands.Select(command => command.CommandId).SequenceEqual(registrations.Select(command => command.Binding.TargetId)) ||
                !item.Commands.Take(startup.Commands.Length).SequenceEqual(startup.Commands)) throw Invalid();
            AndroidStartupCodec.RequireCommandFacts(binding.StepId, item.Commands, item.Cleanup);
            StepEvidenceCodec.Plan(item.Artifact); result.Add(item);
        }
        return result.ToImmutable();
    }

    internal static IReadOnlyList<BlobReference> References(AttemptJournalView journal, ExecutionPlan total, string totalSha256,
        AndroidControlEvidence reader, AndroidCollectedIntent intent, bool verifyContent)
    {
        var document = reader.Read<AndroidCollectedDocument>(intent.Artifact);
        var registrations = AndroidControlCodec.Read(journal, total, totalSha256)
            .Where(item => item.Binding.StepId == intent.Binding.StepId).ToArray();
        var startup = AndroidStartupCodec.Read(journal, total, totalSha256).Single(item => item.Binding == intent.Binding);
        if (document.Format != Format || document.Version != 1 || document.Binding != intent.Binding || document.Commands.IsDefault ||
            document.Startup != startup.Artifact || document.Cleanup is null ||
            !JsonElement.DeepEquals(JsonSerializer.SerializeToElement(document.Cleanup, StepResultCodec.Options),
                JsonSerializer.SerializeToElement(intent.Cleanup, StepResultCodec.Options)) ||
            document.Commands.Length != registrations.Length || !document.Commands.Select(item => item.Binding).SequenceEqual(registrations.Select(item => item.Binding)))
            throw Invalid();
        var references = new List<BlobReference> { intent.Artifact, document.Startup }; long bytes = 0;
        var identities = new HashSet<(int ProcessId, DateTime Created)>();
        for (var index = 0; index < registrations.Length; index++)
        {
            var registration = registrations[index];
            var collection = reader.Collection(total, registration, document.Commands[index].Collection, verifyContent);
            var observation = reader.Observation(total, registration, collection.Observation, verifyContent: false);
            references.Add(document.Commands[index].Collection); references.Add(collection.Observation);
            references.AddRange(observation.Parts.Select(part => part.Content));
            references.AddRange(AndroidRetentionEvidence.Require(reader, total, registration, collection));
            if (collection.Retention is { } retainedReference)
            {
                var retained = reader.Read<AndroidLaunchRetained>(retainedReference);
                if (AndroidRetentionEvidence.Last(journal, registration.Binding, "retained") != retainedReference ||
                    AndroidRetentionEvidence.Last(journal, registration.Binding, "prepared") != retained.Intent) throw Invalid();
            }
            bytes = checked(bytes + observation.Parts.Sum(part => part.Content.SizeBytes));
            foreach (var member in collection.Inventory.AllFamily) identities.Add((member.ProcessId, member.CreatedAtUtc));
            if (index + 1 < registrations.Length && registrations[index + 1].PreviousObservation != collection.Observation) throw Invalid();
        }
        if (bytes > AndroidControlCapacity.MaximumBindingRawBytes || identities.Count > AndroidControlCapacity.MaximumObservedProcesses)
            throw Invalid();
        return references.Distinct().ToArray();
    }

    internal static JsonObject Participant(AndroidCollectedIntent item, string scriptId) => new()
    {
        ["participantId"] = Guid.NewGuid().ToString("D"), ["kind"] = "process", ["resourceKey"] = "automation:" + scriptId,
        ["identity"] = JsonSerializer.SerializeToNode(item, StepResultCodec.Options)
    };

    internal static void RequireResolved(AttemptJournalView journal, ExecutionPlan total, string totalSha256, string? stepId = null)
    {
        var registrations = AndroidControlCodec.Read(journal, total, totalSha256);
        var startups = AndroidStartupCodec.Read(journal, total, totalSha256);
        var collected = Read(journal, total, totalSha256);
        var required = registrations.Select(item => item.Binding.StepId).Concat(startups.Select(item => item.Binding.StepId))
            .Where(value => stepId is null || value == stepId).Distinct(StringComparer.Ordinal);
        if (required.Any(value => !collected.Any(item => item.Binding.StepId == value)) ||
            collected.Where(item => stepId is null || item.Binding.StepId == stepId).Any(item => item.Commands.Any(command =>
                command.Kind == TargetCommandKind.Effect && command.Engine != EngineOutcome.NotStarted &&
                (command.Engine == EngineOutcome.Unknown && command.Observation != TargetCommandObservation.OwnedHandoff || command.Observation is TargetCommandObservation.Unverified or
                    TargetCommandObservation.Incomplete or TargetCommandObservation.Contradictory))))
            throw new IOException("execution_target_collection_pending");
    }

    private static JsonException Invalid() => new("execution_android_collection_unrecognized");
}
