using System.Collections.Immutable;
using System.Text.Json;
using NexusPipeline.Core.Execution;
using NexusPipeline.Extensibility.Protocol;
using NexusPipeline.Platform.Serialization;
using NexusPipeline.Platform.Storage.Sqlite;

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

    internal IReadOnlyList<BlobReference> RequireStartup(JsonElement journal, ExecutionPlan total, string totalSha256,
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

    internal IReadOnlyList<BlobReference> RequireCollected(JsonElement journal, ExecutionPlan total, string totalSha256,
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

    private static void RequireCleanup(JsonElement journal, ExecutionPlan total, string totalSha256,
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
