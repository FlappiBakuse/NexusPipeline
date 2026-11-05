using System.Collections.Immutable;
using System.Runtime.Versioning;
using System.Text.Json;
using NexusPipeline.Core.Configuration;
using NexusPipeline.Core.Execution;
using NexusPipeline.Core.Maintenance;
using NexusPipeline.Extensibility.Protocol;
using NexusPipeline.Platform.Extensions;
using NexusPipeline.Platform.Processes;
using NexusPipeline.Platform.Serialization;
using NexusPipeline.Platform.Storage.Sqlite;

namespace NexusPipeline.Platform.Storage.Execution;

[SupportedOSPlatform("windows")]
internal sealed class ProviderLauncherStore : IDisposable
{
    private readonly RuntimeInstance _instance;
    private readonly StateDatabase _database;
    private readonly ContractReader _contracts;
    private readonly InstanceLease _lease;
    private readonly ResourceLeaseManager _resources;
    private readonly ITargetPersistenceObserver? _observer;
    private readonly ResourceOperationBorrow _root;
    private readonly ExecutionProviderRegistry.PreparedExecutionProvider _provider;
    private readonly Guid _journalId;
    private readonly Guid _launcherId;
    private readonly string _subject, _stepId;
    private readonly ImmutableArray<ResourceClaim> _references;
    private RetainedResourceReferences? _passive;
    private IDisposable? _package;
    private bool _transferred, _disposed;

    internal ProviderLauncherStore(RuntimeInstance instance, StateDatabase database, ContractReader contracts,
        InstanceLease lease, ResourceLeaseManager resources, AcceptedExecutionAdmission accepted, string stepId, ITargetPersistenceObserver? observer = null)
    {
        _instance = instance; _database = database; _contracts = contracts; _lease = lease; _resources = resources;
        _observer = observer;
        _journalId = accepted.Receipt.JournalId; _subject = accepted.Identity.SubjectId; _stepId = stepId;
        accepted.Prepared.RequireLive();
        var total = accepted.Prepared.Total.Plan.ReadWire();
        var step = total.Steps.Single(step => step.StepId == stepId);
        _provider = accepted.Prepared.Providers[stepId]; _launcherId = _provider.RetainedReservationId;
        _root = accepted.Root.Borrow(resources, accepted.Identity.OperationId, ExecutionPlanResources.Claims(total));
        try
        {
            _references = ProviderLauncherReferences.ReadLive(step, _provider);
            ProviderLauncherReferences.RequireSubset(total, _references, _provider.ExtensionId);
            _package = _provider.Use(package => package.Hold());
            _passive = accepted.Root.RetainReferences(resources, accepted.Identity.OperationId, _references);
            _provider.ClaimRetainedReservation();
        }
        catch { _passive?.Dispose(); _package?.Dispose(); _root.Dispose(); throw; }
    }

    internal ProviderLauncherBinding Register(OwnedWorkerJob job) => Use(() => _provider.Use(_ => _lease.PublishExecution(() =>
    {
        var state = ReadFresh();
        var prior = Current(state);
        if (prior is not null)
        {
            if (prior.Stage != "registered" || prior.Binding.JobName != job.Name ||
                prior.Binding.OwnerWriterId != JournalWriterStore.CurrentWriter.WriterId) throw Invalid();
            RequireEmpty(job); return prior.Binding;
        }
        RequireEmpty(job);
        if (state.IsDispatchClosed || state.Gate.State != EffectGateState.Entered ||
            state.Journal.Phase != JournalPhase.Applying || state.Latest.Revision != state.Journal.Revision ||
            state.Jobs.Any(item => item.JobName == job.Name)) throw Invalid();
        var step = state.Total.Steps.Single(step => step.StepId == _stepId);
        if (step.Provider?.Launcher is null || step.Provider.ActivationId != _provider.ActivationId.ToString("D") ||
            step.Provider.ExtensionId != _provider.ExtensionId) throw Invalid();
        using var process = System.Diagnostics.Process.GetCurrentProcess();
        var binding = new ProviderLauncherBinding(_instance.InstanceId, _journalId, state.Journal.OperationId,
            Guid.Parse(state.Attempt.AttemptId), _stepId, _launcherId, job.Name, JournalWriterStore.CurrentWriter.WriterId,
            process.SessionId, state.Attempt.ExecutionPlan.Sha256, _provider.ActivationId, _provider.ExtensionId, step.Provider.EntryId);
        Publish(state, new(ProviderLauncherCodec.Format, 1, binding, "registered", null));
        return binding;
    })));

    internal T Start<T>(ProviderLauncherBinding binding, OwnedWorkerJob job, Func<ConfigurationEditorCommand, T> start) =>
        Use(() => _provider.Use(_ =>
        {
            var state = ReadFresh(); var current = Required(state, binding);
            if (current.Stage != "registered" || current.Binding.JobName != job.Name || state.IsDispatchClosed) throw Invalid();
            var step = state.Total.Steps.Single(step => step.StepId == binding.StepId);
            var launcher = step.Provider!.Launcher!;
            var command = EditorCommandPolicy.Freeze(new(launcher.Image.Path, launcher.Image.Sha256,
                launcher.Arguments.ToImmutableArray(), step.WorkingDirectory));
            return start(command);
        }));

    internal void RecordRoot(ProviderLauncherBinding binding, OwnedWorkerJob job, OwnedWorkerIdentity root) =>
        Use(() => _provider.Use(_ => _lease.PublishExecution(() =>
        {
            var state = ReadFresh(); var current = Required(state, binding);
            var identity = new ProcessIdentity(root.ProcessId, root.CreatedAtUtc, root.ImagePath, root.ImageSha256, root.UserSid, root.IsElevated);
            if (root.InstanceId != binding.InstanceId || root.AttemptId != binding.AttemptId || current.Binding.JobName != job.Name) throw Invalid();
            if (current.Stage == "root")
            { if (ProviderLauncherRows.Root(_database, binding) != identity) throw Invalid(); return true; }
            if (current.Stage != "registered") throw Invalid();
            using var process = System.Diagnostics.Process.GetProcessById(root.ProcessId);
            if (!job.ContainsProcessHandle(process.SafeHandle) || ProcessIdentityReader.Read(root.ProcessId) != identity ||
                job.GetAccounting() is not { ActiveProcesses: 1, TotalProcesses: 1 }) throw Invalid();
            _database.Write((connection, transaction) =>
            { ProviderLauncherRows.InsertRootInside(connection, transaction, state, binding, identity); return true; });
            PublishArtifact(state, binding, "root", new ProviderLauncherRoot("nexus.execution.provider-launcher-root", 1, binding, identity));
            return true;
        })));

    internal void RecordStarted(PreparedLauncherStart proof) =>
        proof.Use(proof.Binding, (actualRoot, error) => Use(() => _lease.PublishExecution(() =>
        {
            var state = ReadFresh(); var current = Required(state, proof.Binding);
            if (actualRoot is null || error is not null) throw Invalid();
            var document = new ProviderLauncherStarted("nexus.execution.provider-launcher-started", 1, proof.Binding, actualRoot);
            if (current.Stage == "started")
            { if (!Same(ReadArtifact<ProviderLauncherStarted>(current.Artifact!), document)) throw Invalid(); return true; }
            if (current.Stage != "root" || ProviderLauncherRows.Root(_database, proof.Binding) != actualRoot) throw Invalid();
            PublishArtifact(state, proof.Binding, "started", document); return true;
        })));

    internal void RecordStopped(ProviderLauncherBinding binding, OwnedWorkerJob job, PreparedStoppedTarget stopped, PreparedLauncherStart? started) =>
        stopped.Use(job, inventory => Use(() => _lease.PublishExecution(() =>
        {
            var state = ReadFresh(); var current = Required(state, binding);
            var root = ProviderLauncherRows.Root(_database, binding);
            int? nativeError = null;
            if (started is not null) started.Use(binding, (actualRoot, error) =>
            { if (actualRoot is not null && actualRoot != root) throw Invalid(); nativeError = error; return true; });
            var document = new ProviderLauncherStopped("nexus.execution.provider-launcher-stopped", 1, binding, root, inventory,
                started?.Succeeded == true, nativeError);
            if (current.Stage == "stopped")
            { if (!Same(ReadArtifact<ProviderLauncherStopped>(current.Artifact!), document)) throw Invalid(); return true; }
            if (current.Stage == "retained") throw Invalid();
            var artifact = new ImmutableBlobStore(_instance, _contracts).PersistPlan(BoundedJson.Serialize(document, StepResultCodec.Options));
            using var held = new ImmutableBlobStore(_instance, _contracts).OpenVerified(artifact);
            Publish(state, new(ProviderLauncherCodec.Format, 1, binding, "stopped", artifact, LaunchEngine(document))); return true;
        })));

    internal PreparedRetentionIntent PrepareRetention(ProviderLauncherBinding binding, OwnedWorkerJob job, OwnedTargetInventory inventory) =>
        Use(() => _provider.Use(_ => _lease.PublishExecution(() =>
        {
            var state = ReadFresh(); var current = Required(state, binding);
            if (current.Stage is not ("started" or "retentionPrepared")) throw Invalid();
            RequireRetainInventory(state, binding, inventory);
            var necessary = LauncherBoundaryReader.Read(_instance, _database, _contracts, state, binding.StepId);
            var root = ProviderLauncherRows.Root(_database, binding) ?? throw Invalid();
            if (current.Stage == "retentionPrepared")
            {
                var prior = ReadArtifact<ProviderLauncherRetention>(current.Artifact!);
                if (Same(prior.Inventory, inventory) && prior.NecessaryClosures.SequenceEqual(necessary.Closures) &&
                    prior.PrimaryResult == necessary.Primary && prior.Terminal == necessary.Terminal)
                    return new PreparedRetentionIntent(new PreparedProviderRetention(this, job, binding, current.Artifact!));
            }
            if (ProviderLauncherCodec.ReadAll(state.Latest.Root, state.Total, state.Attempt.ExecutionPlan.Sha256)
                .Count(item => item.Binding == binding && item.Stage == "retentionPrepared") >= ProviderLauncherCodec.MaximumRetentionIntents) throw Invalid();
            var document = new ProviderLauncherRetention("nexus.execution.provider-launcher-retention", 1, binding, root,
                inventory, necessary.Closures, necessary.Primary, necessary.Terminal, _references, state.Journal.Revision);
            var artifact = PublishArtifact(state, binding, "retentionPrepared", document);
            _observer?.Reached(TargetPersistencePoint.RetentionPrepared, binding);
            return new PreparedRetentionIntent(new PreparedProviderRetention(this, job, binding, artifact));
        })));

    internal T UseRetention<T>(ProviderLauncherBinding binding, OwnedWorkerJob job, BlobReference reference,
        OwnedTargetInventory inventory, Func<T> effect) => Use(() => _lease.PublishExecution(() =>
    {
        var state = ReadFresh(); var current = Required(state, binding);
        if (current.Stage != "retentionPrepared" || current.Artifact != reference || binding.JobName != job.Name) throw Invalid();
        var document = ReadArtifact<ProviderLauncherRetention>(reference);
        if (!Same(document.Inventory, inventory) || !document.References.SequenceEqual(_references)) throw Invalid();
        RequireRetainInventory(state, binding, inventory);
        var necessary = LauncherBoundaryReader.Read(_instance, _database, _contracts, state, binding.StepId);
        if (!necessary.Closures.SequenceEqual(document.NecessaryClosures) || necessary.Primary != document.PrimaryResult ||
            necessary.Terminal != document.Terminal) throw Invalid();
        return job.HasTargetLifetimeChange && job.CaptureTargetInventory().LimitFlags == 0 ? effect() : _provider.Use(_ => effect());
    }));

    internal ProviderLauncherLifetime RecordRetained(ProviderLauncherBinding binding, OwnedWorkerJob job,
        PreparedRetentionIntent intent, OwnedTargetHandoff handoff) => job.UseTargetHandoff(handoff, () => Use(() => _lease.PublishExecution(() =>
    {
        var state = ReadFresh(); var current = Required(state, binding);
        var document = new ProviderLauncherRetained("nexus.execution.provider-launcher-retained", 1, binding, intent.Reference, handoff);
        BlobReference artifact;
        if (current.Stage == "retained")
        { if (!Same(ReadArtifact<ProviderLauncherRetained>(current.Artifact!), document)) throw Invalid(); artifact = current.Artifact!; }
        else
        {
            if (current.Stage != "retentionPrepared" || current.Artifact != intent.Reference ||
                handoff.PreviousLimitFlags != 0x00002000 || handoff.EffectiveLimitFlags != 0 ||
                !Same(ReadArtifact<ProviderLauncherRetention>(intent.Reference).Inventory, handoff.Inventory)) throw Invalid();
            artifact = PublishArtifact(state, binding, "retained", document);
        }
        if (_transferred) throw Invalid();
        var lifetime = new ProviderLauncherLifetime(_instance, _database, _contracts, _lease, _resources,
            binding, _subject, artifact, job, handoff, _references, _passive!, _package!);
        _provider.KeepRetained(lifetime);
        _passive = null; _package = null; _transferred = true;
        return lifetime;
    })));

    internal ProviderLauncherBinding? FindRegistered(OwnedWorkerJob job) => Use(() => _lease.PublishExecution(() =>
    {
        var current = Current(ReadFresh());
        if (current is null) return null;
        if (current.Binding.JobName != job.Name || current.Binding.OwnerWriterId != JournalWriterStore.CurrentWriter.WriterId) throw Invalid();
        return current.Binding;
    }));

    private void RequireRetainInventory(AttemptWriterState state, ProviderLauncherBinding binding, OwnedTargetInventory inventory)
    {
        TargetJobEvidence.RequireInventory(inventory);
        var image = state.Total.Steps.Single(step => step.StepId == binding.StepId).Provider!.Launcher!.Image;
        var root = ProviderLauncherRows.Root(_database, binding) ?? throw Invalid();
        if (inventory.ActiveProcesses == 0 || inventory.LimitFlags != 0x00002000 || !inventory.AllFamily.Contains(root) ||
            inventory.LivingFamily.Any(member => member.UserSid != root.UserSid || member.IsElevated != root.IsElevated ||
                !StringComparer.OrdinalIgnoreCase.Equals(member.ImagePath, image.Path) || member.ImageSha256 != image.Sha256)) throw Invalid();
    }

    private ProviderLauncherState? Current(AttemptWriterState state) =>
        ProviderLauncherCodec.Read(state.Latest.Root, state.Total, state.Attempt.ExecutionPlan.Sha256).SingleOrDefault(item => item.Binding.StepId == _stepId);
    private static ProviderLauncherState Required(AttemptWriterState state, ProviderLauncherBinding binding) =>
        ProviderLauncherCodec.Read(state.Latest.Root, state.Total, state.Attempt.ExecutionPlan.Sha256).SingleOrDefault(item => item.Binding == binding) ?? throw Invalid();

    private AttemptWriterState ReadFresh()
    {
        _lease.VerifyHeld(_instance);
        var state = AttemptWriterState.Read(_instance, _database, _contracts, _journalId);
        if (state.Latest.Revision != state.Journal.Revision)
        { ExecutionAttemptProjection.Synchronize(_instance, _database, _contracts, state); state = AttemptWriterState.Read(_instance, _database, _contracts, _journalId); }
        return state;
    }
    private T Use<T>(Func<T> callback) => _root.Use(callback);
    private T ReadArtifact<T>(BlobReference reference) where T : class =>
        new TargetJobEvidence(_instance, _database, _contracts).Read<T>(reference);
    private BlobReference PublishArtifact<T>(AttemptWriterState state, ProviderLauncherBinding binding, string stage, T document) =>
        TargetJournalPublisher.PublishParticipant(_instance, _database, _contracts, state, document, artifact =>
            ProviderLauncherCodec.Participant(new(ProviderLauncherCodec.Format, 1, binding, stage, artifact),
                state.Total.Steps.Single(step => step.StepId == binding.StepId).ScriptId));
    private void Publish(AttemptWriterState state, ProviderLauncherIntent intent) =>
        TargetJournalPublisher.AppendParticipant(_instance, _database, _contracts, state,
            ProviderLauncherCodec.Participant(intent, state.Total.Steps.Single(step => step.StepId == _stepId).ScriptId));
    private static void RequireEmpty(OwnedWorkerJob job)
    { if (job.GetAccounting() is not { ActiveProcesses: 0, TotalProcesses: 0 } || job.ActiveProcessIds().Count != 0) throw Invalid(); }
    private static bool Same<T>(T first, T second) => JsonElement.DeepEquals(
        JsonSerializer.SerializeToElement(first, StepResultCodec.Options), JsonSerializer.SerializeToElement(second, StepResultCodec.Options));

    public void Dispose()
    {
        if (_disposed) return;
        var current = Current(ReadFresh());
        if (current is not null && current.Stage is not ("stopped" or "retained")) throw Invalid();
        if (current?.Stage == "retained" && !_transferred) throw Invalid();
        if (!_transferred) _provider.ReleaseRetainedReservation(_launcherId);
        _passive?.Dispose(); _package?.Dispose(); _root.Dispose(); _disposed = true;
    }
    internal static EngineOutcome LaunchEngine(ProviderLauncherStopped document) =>
        document.StartReturned ? EngineOutcome.Succeeded : document.Inventory.TotalProcesses != 0 ? EngineOutcome.Unknown :
            document.NativeStartErrorCode is not null ? EngineOutcome.Failed : EngineOutcome.NotStarted;
    private static IOException Invalid() => new("execution_provider_launcher_boundary_unverified");
}
