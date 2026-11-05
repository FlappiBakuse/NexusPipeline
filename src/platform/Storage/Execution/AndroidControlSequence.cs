using NexusPipeline.Core.Execution;
using NexusPipeline.Platform.Serialization;
using NexusPipeline.Platform.Storage.Sqlite;

namespace NexusPipeline.Platform.Storage.Execution;

internal static class AndroidControlSequence
{
    internal static void RequireNext(RuntimeInstance instance, StateDatabase database, ContractReader contracts,
        AttemptWriterState state, AndroidCommandRequest request,
        IReadOnlyList<AndroidCommandRegistration> prefix, BlobReference? previousObservation)
    {
        if (prefix.Count != request.Sequence || prefix.Any(item => item.Binding.StepId != request.Binding.StepId))
            throw Invalid();
        if (AndroidControlAdmission.IsCleanup(request.Purpose))
        { RequireCleanup(instance, database, contracts, state, request, prefix, previousObservation); return; }
        if (prefix.Any(item => AndroidControlAdmission.IsCleanup(item.Purpose))) throw Invalid();
        var target = state.Total.Steps.Single(step => step.StepId == request.Binding.StepId).Target!;
        if (!target.LaunchByHost) throw Invalid();
        var reader = new AndroidControlEvidence(instance, database, contracts);
        var observations = new List<(AndroidCommandRegistration Request, ExecutionAndroidObservation Meaning)>();
        for (var index = 0; index < prefix.Count; index++)
        {
            var prior = prefix[index];
            var reference = index + 1 < prefix.Count ? prefix[index + 1].PreviousObservation : previousObservation;
            if (reference is null) throw Invalid();
            var native = reader.Observation(state, prior, reference, verifyContent: true);
            var meaning = ExecutionAndroidObservations.ReadSaved(reader, reader.Request(state, prior), native, reference);
            if (meaning.State is ExecutionAndroidState.Unknown or ExecutionAndroidState.Failed ||
                meaning.Interpretation is TargetCommandObservation.Incomplete or TargetCommandObservation.Contradictory or
                    TargetCommandObservation.Unverified) throw Invalid();
            observations.Add((prior, meaning));
        }
        var next = Next(request.Driver, request.Package, observations, target.Android!.Emulator is not null);
        if (next is null || next.Value.Purpose != request.Purpose || next.Value.Action != request.Action) throw Invalid();
        if (request.Action is ExecutionAndroidAction.StartApplication or ExecutionAndroidAction.LaunchDevice)
        {
            var action = TargetLifecyclePolicy.Launch(new(ExecutionTargetKind.Android, true, target.ForceCloseAtEnd),
                ExecutionTargetReadiness.NotReady, ExecutionTargetAuthority.ExplicitAndroidControl, null);
            if (action != TargetLaunchAction.Start) throw Invalid();
        }
    }

    private static void RequireCleanup(RuntimeInstance instance, StateDatabase database, ContractReader contracts,
        AttemptWriterState state, AndroidCommandRequest request,
        IReadOnlyList<AndroidCommandRegistration> prefix, BlobReference? previousObservation)
    {
        AndroidControlAdmission.RequireCollectedMain(state, request.Binding.StepId);
        var decision = TargetCleanupFacts.Read(state, request.Binding.StepId);
        var reader = new AndroidControlEvidence(instance, database, contracts);
        var meanings = new List<(AndroidCommandRegistration Request, ExecutionAndroidObservation Meaning)>();
        for (var index = 0; index < prefix.Count; index++)
        {
            if (!AndroidControlAdmission.IsCleanup(prefix[index].Purpose)) continue;
            var reference = index + 1 < prefix.Count ? prefix[index + 1].PreviousObservation : previousObservation;
            if (reference is null) throw Invalid();
            var native = reader.Observation(state, prefix[index], reference, verifyContent: true);
            var meaning = ExecutionAndroidObservations.ReadSaved(reader, reader.Request(state, prefix[index]), native, reference);
            meanings.Add((prefix[index], meaning));
        }
        RequireCleanupNext(request.Driver, request.Purpose, decision, meanings);
    }

    internal static void RequireCleanupNext(string driver, AndroidCommandPurpose purpose, ExecutionTargetCleanup decision,
        IReadOnlyList<(AndroidCommandRegistration Request, ExecutionAndroidObservation Meaning)> meanings)
    {
        if (purpose == AndroidCommandPurpose.CleanupReadiness)
        {
            if (driver != "mumu" || meanings.Count != 0 ||
                decision.Application == TargetCleanupAction.None && decision.Device == TargetCleanupAction.None) throw Invalid();
            return;
        }
        if (driver == "mumu" && !meanings.Any(item => item.Request.Purpose == AndroidCommandPurpose.CleanupReadiness &&
                IsCurrentMapping(item.Meaning))) throw new IOException("execution_mumu_current_mapping_unproven");
        if (meanings.Any(item => item.Meaning.State is ExecutionAndroidState.Unknown or ExecutionAndroidState.Failed ||
                item.Meaning.Interpretation is TargetCommandObservation.Unverified or TargetCommandObservation.Incomplete or
                    TargetCommandObservation.Contradictory)) throw Invalid();
        if (purpose == AndroidCommandPurpose.CleanupApplication)
        {
            if (decision.Application != TargetCleanupAction.StopAndroidApplication ||
                meanings.Any(item => item.Request.Purpose is AndroidCommandPurpose.CleanupApplication or AndroidCommandPurpose.ShutdownDevice or
                    AndroidCommandPurpose.ConfirmOffline)) throw Invalid();
            return;
        }
        if (decision.Device != TargetCleanupAction.ShutdownAndroid) throw Invalid();
        if (purpose == AndroidCommandPurpose.ShutdownDevice)
        {
            if (meanings.Any(item => item.Request.Purpose is AndroidCommandPurpose.ShutdownDevice or AndroidCommandPurpose.ConfirmOffline))
                throw Invalid();
            if (decision.Application == TargetCleanupAction.StopAndroidApplication &&
                !meanings.Any(item => item.Request.Purpose == AndroidCommandPurpose.CleanupApplication &&
                    item.Meaning.State == ExecutionAndroidState.ApplicationStopRequested)) throw Invalid();
            return;
        }
        var shutdown = meanings.ToList().FindIndex(item => item.Request.Purpose == AndroidCommandPurpose.ShutdownDevice);
        if (purpose != AndroidCommandPurpose.ConfirmOffline || shutdown < 0 ||
            meanings.Skip(shutdown + 1).Any(item => item.Request.Purpose != AndroidCommandPurpose.ConfirmOffline) ||
            meanings.Count >= 2 && meanings.TakeLast(2).All(item => item.Request.Purpose == AndroidCommandPurpose.ConfirmOffline &&
                item.Meaning.State == ExecutionAndroidState.Offline && item.Meaning.Interpretation == TargetCommandObservation.ExpectedOffline) ||
            !meanings.Any(item => item.Request.Purpose == AndroidCommandPurpose.ShutdownDevice &&
                item.Meaning.State == ExecutionAndroidState.ShutdownRequested)) throw Invalid();
    }

    internal static bool CleanupMappingProven(RuntimeInstance instance, StateDatabase database, ContractReader contracts,
        AttemptWriterState state, IReadOnlyList<AndroidCommandRegistration> prefix, BlobReference? previousObservation)
    {
        var index = prefix.ToList().FindLastIndex(item => item.Purpose == AndroidCommandPurpose.CleanupReadiness &&
            item.Action == ExecutionAndroidAction.DeviceState);
        if (index < 0) return false;
        var reference = index + 1 < prefix.Count ? prefix[index + 1].PreviousObservation : previousObservation;
        if (reference is null) return false;
        var reader = new AndroidControlEvidence(instance, database, contracts);
        var request = reader.Request(state, prefix[index]);
        if (request.Driver != "mumu") return false;
        var native = reader.Observation(state, prefix[index], reference, verifyContent: true);
        var meaning = ExecutionAndroidObservations.ReadSaved(reader, request, native, reference);
        return IsCurrentMapping(meaning);
    }

    private static bool IsCurrentMapping(ExecutionAndroidObservation meaning) =>
        meaning.Interpretation == TargetCommandObservation.Complete &&
        meaning.State is ExecutionAndroidState.Ready or ExecutionAndroidState.NotReady or ExecutionAndroidState.Booting;

    internal static (AndroidCommandPurpose Purpose, ExecutionAndroidAction Action)? Next(string driver, string package,
        IReadOnlyList<(AndroidCommandRegistration Request, ExecutionAndroidObservation Meaning)> observed, bool emulator = false)
    {
        if (observed.Count == 0 && emulator) return (AndroidCommandPurpose.InitialReadiness, ExecutionAndroidAction.DeviceState);
        if (observed.Count == 0) return driver == "mumu"
            ? (AndroidCommandPurpose.InitialReadiness, ExecutionAndroidAction.DeviceState)
            : (AndroidCommandPurpose.Startup, ExecutionAndroidAction.Connect);
        var (previous, result) = observed[^1];
        return previous.Action switch
        {
            ExecutionAndroidAction.DeviceState when (driver == "mumu" || emulator) && result.State == ExecutionAndroidState.NotReady =>
                (AndroidCommandPurpose.Startup, ExecutionAndroidAction.LaunchDevice),
            ExecutionAndroidAction.DeviceState when (driver == "mumu" || emulator) && result.State is ExecutionAndroidState.Ready or ExecutionAndroidState.Booting =>
                (AndroidCommandPurpose.Startup, ExecutionAndroidAction.Connect),
            ExecutionAndroidAction.LaunchDevice when result.State == ExecutionAndroidState.LaunchRequested =>
                (AndroidCommandPurpose.Startup, ExecutionAndroidAction.Connect),
            ExecutionAndroidAction.Connect when result.State == ExecutionAndroidState.Connected =>
                (AndroidCommandPurpose.InitialReadiness, ExecutionAndroidAction.Foreground),
            ExecutionAndroidAction.Foreground when result.State == ExecutionAndroidState.Foreground && result.ForegroundPackage == package => null,
            ExecutionAndroidAction.Foreground when result.State == ExecutionAndroidState.Foreground &&
                previous.Purpose == AndroidCommandPurpose.InitialReadiness =>
                (AndroidCommandPurpose.Startup, ExecutionAndroidAction.StartApplication),
            ExecutionAndroidAction.StartApplication when result.State == ExecutionAndroidState.ApplicationStartRequested =>
                (AndroidCommandPurpose.AwaitReadiness, ExecutionAndroidAction.Foreground),
            ExecutionAndroidAction.Foreground when result.State == ExecutionAndroidState.Foreground &&
                previous.Purpose == AndroidCommandPurpose.AwaitReadiness =>
                (AndroidCommandPurpose.AwaitReadiness, ExecutionAndroidAction.Foreground),
            _ => throw Invalid()
        };
    }

    private static IOException Invalid() => new("execution_android_predecessor_not_authorized");
}
