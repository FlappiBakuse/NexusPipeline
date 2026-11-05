using System.Runtime.Versioning;
using NexusPipeline.Core.Execution;
using NexusPipeline.Platform.Processes;
using NexusPipeline.Platform.Storage.Sqlite;

namespace NexusPipeline.Platform.Storage.Execution;

internal sealed record AndroidLaunchRetention(string Format, int Version, ExecutionTargetBinding Binding,
    string RequestDigest, ProcessIdentity Root, OwnedTargetInventory Inventory, BlobReference Primary);
internal sealed record AndroidLaunchRetained(string Format, int Version, ExecutionTargetBinding Binding,
    BlobReference Intent, OwnedTargetHandoff Native);

[SupportedOSPlatform("windows")]
internal sealed class PreparedAndroidRetention(AndroidControlStore owner, PreparedAndroidAuthority authority,
    OwnedWorkerJob job, BlobReference reference)
{
    internal BlobReference Reference => reference;
    internal T Use<T>(OwnedWorkerJob actual, OwnedTargetInventory inventory, Func<T> effect)
    {
        if (!ReferenceEquals(actual, job)) throw new IOException("execution_emulator_retention_owner_unbound");
        return authority.Use(actual, request => owner.UseRetention(request, reference, inventory, effect));
    }
}

[SupportedOSPlatform("windows")]
internal sealed partial class AndroidControlStore
{
    internal bool IsEmulatorLaunch(AndroidCommandRequest request) => request.Action == ExecutionAndroidAction.LaunchDevice &&
        _prepared.Total.Plan.ReadWire().Steps.Single(step => step.StepId == request.Binding.StepId).Target?.Android?.Emulator is not null;

    internal PreparedAndroidRetention PrepareRetention(PreparedAndroidAuthority authority, OwnedWorkerJob job) => authority.Use(job, request =>
        _root.Use(() => _lease.PublishExecution(() =>
        {
            var state = ReadSynchronized(); var inventory = job.CaptureTargetInventory();
            RequireRetention(state, request, inventory);
            var root = ReadRegistrationRoot(state, request) ?? throw new IOException("execution_emulator_retention_root_missing");
            var primary = StepResultCodec.Read(state.Latest.Root, state.Total).Single(value => value.Binding.StepId == request.Binding.StepId).Latest.Artifact;
            var document = new AndroidLaunchRetention("nexus.execution.android-launch-retention", 1, request.Binding,
                AndroidCommandIdentity.Digest(request), root, inventory, primary);
            var reference = PublishRetention(state, request, AndroidRetentionCodec.Prepared, document);
            return new PreparedAndroidRetention(this, authority, job, reference);
        })));

    internal T UseRetention<T>(AndroidCommandRequest request, BlobReference reference, OwnedTargetInventory inventory, Func<T> effect) =>
        _root.Use(() => _lease.PublishExecution(() =>
        {
            var state = ReadSynchronized(); RequireRetention(state, request, inventory);
            var document = Reader().Read<AndroidLaunchRetention>(reference);
            if (document.Binding != request.Binding || document.RequestDigest != AndroidCommandIdentity.Digest(request) ||
                document.Root != ReadRegistrationRoot(state, request) || !AndroidRetentionEvidence.Same(document.Inventory, inventory) ||
                AndroidRetentionEvidence.Last(state.Latest.Root, request.Binding, AndroidRetentionCodec.Prepared) != reference) throw new IOException("execution_emulator_retention_changed");
            return effect();
        }));

    internal BlobReference RecordRetained(PreparedAndroidAuthority authority, OwnedWorkerJob job, PreparedAndroidRetention intent,
        OwnedTargetHandoff native) => authority.Use(job, request => job.UseTargetHandoff(native, () =>
        _root.Use(() => _lease.PublishExecution(() =>
        {
            var state = ReadSynchronized(); var prepared = Reader().Read<AndroidLaunchRetention>(intent.Reference);
            if (prepared.Binding != request.Binding || AndroidRetentionEvidence.Last(state.Latest.Root, request.Binding, AndroidRetentionCodec.Prepared) != intent.Reference ||
                !AndroidRetentionEvidence.Same(prepared.Inventory, native.Inventory) || native.PreviousLimitFlags != 0x00002000 || native.EffectiveLimitFlags != 0)
                throw new IOException("execution_emulator_handoff_unconfirmed");
            return PublishRetention(state, request, AndroidRetentionCodec.Retained, new AndroidLaunchRetained("nexus.execution.android-launch-retained", 1,
                request.Binding, intent.Reference, native));
        }))));

    private void RequireRetention(AttemptWriterState state, AndroidCommandRequest request, OwnedTargetInventory inventory)
    {
        _ = Required(state, request.Binding); AndroidControlAdmission.RequireCollectedMain(state, request.Binding.StepId);
        if (!IsEmulatorLaunch(request) || TargetCleanupFacts.Read(state, request.Binding.StepId).Device != TargetCleanupAction.None ||
            inventory.LimitFlags != 0x00002000 || inventory.ActiveProcesses == 0 || inventory.LivingFamily.Length != inventory.ActiveProcesses ||
            inventory.TotalProcesses != inventory.RetainedKernelObjects || inventory.AllFamily.All(value => value != ReadRegistrationRoot(state, request)))
            throw new IOException("execution_emulator_retention_not_authorized");
        TargetJobEvidence.RequireInventory(inventory);
    }

    // ReadRoot publishes on its own, and a publication callback cannot enter the publication queue again.
    private ProcessIdentity? ReadRegistrationRoot(AttemptWriterState state, AndroidCommandRequest request) =>
        TargetRootFacts.ReadCommand(_database, Required(state, request.Binding));

    private BlobReference PublishRetention<T>(AttemptWriterState state, AndroidCommandRequest request, string stage, T document) =>
        TargetJournalPublisher.PublishParticipant(_instance, _database, _contracts, state, document, reference =>
            AndroidRetentionCodec.Participant(new(AndroidRetentionCodec.Format, 1, request.Binding, stage, reference),
                state.Total.Steps.Single(step => step.StepId == request.Binding.StepId).ScriptId));

}
