using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using NexusPipeline.Core.Configuration;
using NexusPipeline.Core.Execution;
using NexusPipeline.Platform.Serialization;
using NexusPipeline.Platform.Storage.Configuration;
using NexusPipeline.Platform.Storage.Sqlite;
using NexusPipeline.Platform.Storage.Execution;

namespace NexusPipeline.Platform.Storage.Journals;

public sealed class ConfigurationRecoverySession : IConfigurationRecoverySession
{
    private readonly FrozenConfigurationExchange _plan;
    private readonly JournalStore _store;
    private readonly RecoveryJournalRegistry _registry;
    private readonly FileEffectOutbox _outbox;
    private readonly ConfigurationSwap _swap;
    private readonly FileTreeScanner _scanner;
    private readonly Func<ConfigurationWriterObservation> _observeWriters;
    private readonly IConfigurationPersistenceObserver? _observer;
    private PreparedConfigurationExchange? _prepared;
    private readonly BoundConfigurationJournal _binding;
    private readonly JsonObject _journal;
    private readonly RuntimeInstance _instance;
    private readonly StateDatabase _database;
    private readonly ContractReader _contracts;
    private readonly Action<string>? _phase;

    public ConfigurationRecoverySession(RuntimeInstance instance, Guid journalId, ContractReader contracts, StateDatabase database,
        Func<ConfigurationWriterObservation> observeWriters, IConfigurationPersistenceObserver? observer = null)
        : this(instance, journalId, contracts, database, observeWriters, observer, new(instance, contracts))
    {
    }

    internal ConfigurationRecoverySession(RuntimeInstance instance, Guid journalId, ContractReader contracts, StateDatabase database,
        Func<ConfigurationWriterObservation> observeWriters, IConfigurationPersistenceObserver? observer, JournalStore store,
        Action<string>? phase = null)
    {
        if (database.InstanceId != instance.InstanceId) throw new IOException("configuration_database_instance_unbound");
        _instance = instance; _database = database; _contracts = contracts;
        _observeWriters = observeWriters;
        _observer = observer; _phase = phase;
        _store = store;
        _registry = new(database);
        _outbox = new(database);
        var physical = _store.Read(journalId);
        _binding = ConfigurationJournalBinding.ReadForRestoration(instance, physical, contracts, database);
        _journal = _binding.Document;
        _plan = _binding.Plan;
        _prepared = _binding.Prepared;
        _scanner = new(SnapshotBudget.Default);
        _swap = new(_scanner, new(_scanner, new ImmutableBlobStore(instance, contracts)));
    }

    public ConfigurationRecoveryInspection Inspect()
    {
        _phase?.Invoke("configuration-read");
        var registered = _registry.ReadJournal(_plan.JournalId);
        if (registered.OperationId != _plan.OperationId || registered.Kind != _plan.Kind || registered.PlanDigest != _binding.Physical.PlanDigest ||
            registered.PlanPath != _binding.Physical.PlanPath || !_binding.Physical.Root.GetProperty("resourceKeys").EnumerateArray()
                .Select(key => key.GetString()!).Order(StringComparer.Ordinal).SequenceEqual(registered.ResourceKeys))
            throw new IOException("configuration_journal_registration_unbound");
        var phase = CurrentPhase;
        var agrees = registered.Phase == phase && registered.Revision == CurrentRevision;
        if (phase == JournalPhase.Committed && agrees)
        {
            if (_prepared is { Reconstruction: not null } rebuilt)
            {
                var stopped = RequireStopped();
                using var held = _swap.HoldRestored(rebuilt, stopped, OperatingSystem.IsWindows() && ConfigurationWorkspaceCleanup.IsRetired(_instance, _binding));
            }
            return new(phase, true, false, WriterState.Unknown, false, false, false, false);
        }
        var writers = Observe();
        if (_phase is not null && phase != JournalPhase.Quarantined && (!writers.IsComplete || writers.State != WriterState.Stopped))
            throw new IOException("configuration_writer_stop_unconfirmed");
        var noEffects = false;
        var canRestore = false;
        var restored = false;
        string? restorationFailure = null;
        if (writers.IsComplete && writers.State == WriterState.Stopped)
        {
            _phase?.Invoke("configuration-scene");
            try
            {
                if (_prepared is null) noEffects = VerifyNoEffects() &&
                    (phase == JournalPhase.Prepared ? _outbox.Read(_plan.JournalId).All(effect => effect.State == FileEffectState.Pending) :
                        phase == JournalPhase.Committed && IsUnexecutedCommit());
                else
                {
                    var scene = _swap.InspectRestoration(_prepared, writers);
                    restorationFailure = scene.FailureCode;
                    canRestore = restorationFailure is null;
                    if (canRestore && (_phase is null || phase is JournalPhase.Reverted or JournalPhase.Committed))
                    {
                        try { _swap.VerifyRestored(_prepared, writers); restored = true; }
                        catch (IOException) when (_phase is null) { }
                    }
                }
            }
            catch (Exception error) when (_phase is null && error is IOException or UnauthorizedAccessException) { }
        }
        if (_phase is not null && phase != JournalPhase.Quarantined && !noEffects && !canRestore &&
            restorationFailure is not ("configuration_original_lost" or "configuration_backup_unverified"))
            throw new IOException("configuration_restoration_unverified");
        var effectsClosed = _outbox.Read(_plan.JournalId).All(effect => effect.Action == FileEffectAction.RemoveOwned ||
            effect.State is FileEffectState.Completed or FileEffectState.NotExecuted);
        return new(phase, agrees, writers.IsComplete, writers.State, noEffects, canRestore, restored,
            phase == JournalPhase.Committed && effectsClosed && (restored || noEffects)) { RestorationFailureCode = restorationFailure };
    }

    public void CommitNoEffects()
    {
        RequireStopped();
        if (!VerifyNoEffects()) throw new IOException("configuration_no_effect_unverified");
        ReconcileAcceptedHistory();
        if (!HasReceipt("verify-no-effect", "notExecuted")) Save(JournalPhase.Prepared, "verify-no-effect", "notExecuted");
        CloseInterruptedEffects();
        Save(JournalPhase.Committed, "verify-no-effect", "notExecuted", new(true, false, false, false));
    }

    public void BeginRestoration()
    {
        var writers = RequireStopped();
        _phase?.Invoke("configuration-scene");
        var prepared = _prepared ?? throw new IOException("configuration_workspace_unverified");
        var scene = _swap.InspectRestoration(prepared, writers);
        if (scene.FailureCode is not null) throw new IOException(scene.FailureCode);
        ReconcileAcceptedHistory();
        if (CurrentPhase != JournalPhase.Reverting || !HasReceipt("retire-candidate", "started"))
            Save(JournalPhase.Reverting, "retire-candidate", "started", recoveryVerified: true);
        if (scene.NeedsReconstruction)
        {
            if (!prepared.ReconstructionStarted)
            {
                Save(JournalPhase.Reverting, ConfigurationRestorationProof.Step, "started", detailCode: ConfigurationRestorationProof.Intent);
                prepared = prepared with { ReconstructionStarted = true };
            }
            var proof = _swap.StageOriginal(prepared, RequireStopped());
            var participants = _journal["participants"]!.AsArray();
            participants.Add(proof.Participant(_plan.JournalId, _binding.Physical.PlanDigest, participants[0]!["resourceKey"]!.GetValue<string>()));
            _prepared = prepared with { Reconstruction = proof };
            Save(JournalPhase.Reverting, ConfigurationRestorationProof.Step, "completed", detailCode: "configuration_original_reconstructed");
        }
    }

    public void RetireCandidate()
    {
        var prepared = _prepared ?? throw new IOException("configuration_workspace_unverified");
        var writers = RequireStopped();
        _phase?.Invoke("configuration-scene");
        _swap.RetireCandidate(prepared, writers);
        if (!HasReceipt("retire-candidate", "reverted")) Save(JournalPhase.Reverting, "retire-candidate", "reverted");
    }

    public void RestoreOriginal()
    {
        var prepared = _prepared ?? throw new IOException("configuration_workspace_unverified");
        if (!HasReceipt("restore-original", "started")) Save(JournalPhase.Reverting, "restore-original", "started");
        using (var history = new HistoryReadScope(_instance, _database, _contracts))
        {
            var writers = RequireStopped();
            _phase?.Invoke("configuration-scene");
            _swap.RestoreOriginal(prepared, writers);
            writers = RequireStopped();
            _phase?.Invoke("configuration-scene");
            _swap.VerifyRestored(prepared, writers);
        }
        Save(JournalPhase.Reverted, "restore-original", "reverted");
    }

    public void CommitRestoration()
    {
        var prepared = _prepared ?? throw new IOException("configuration_workspace_unverified");
        var writers = RequireStopped();
        _phase?.Invoke("configuration-scene");
        _swap.VerifyRestored(prepared, writers);
        ReconcileAcceptedHistory();
        CloseInterruptedEffects();
        Save(JournalPhase.Committed, "interruption-recovered", "reverted");
    }

    public void Quarantine(string reasonCode)
    {
        ReconcileAcceptedHistory();
        Save(JournalPhase.Quarantined, "isolate-configuration", "unknown", detailCode: reasonCode);
    }

    public void ReconcileRecordedCommit()
    {
        var inspection = Inspect();
        if (inspection.Phase != JournalPhase.Committed || !inspection.CanReconcileRecordedCommit)
            throw new IOException("configuration_recorded_commit_unverified");
        ReconcileAcceptedHistory(allowCommitted: true);
    }

    private bool IsUnexecutedCommit() =>
        _outbox.Read(_plan.JournalId).All(effect => effect.State == FileEffectState.NotExecuted) &&
        _store.ReadAllRevisions(_plan.JournalId).All(item => item.Phase is JournalPhase.Prepared or JournalPhase.Committed) &&
        _journal["receipts"]!.AsArray().LastOrDefault()?["effect"]?.GetValue<string>() == "notExecuted";

    private bool VerifyNoEffects()
    {
        _phase?.Invoke("configuration-scene");
        if (_prepared is not null || PhysicalObjectIdentity.Read(Path.GetDirectoryName(_plan.TargetPath)!) != _plan.ParentIdentity) return false;
        var adjacent = Path.Combine(Path.GetDirectoryName(_plan.TargetPath)!, ".nexus-" + _plan.JournalId.ToString("D"));
        if (_scanner.Capture(adjacent, CancellationToken.None).Shape != FileObjectKind.Missing) return false;
        var actual = _scanner.Capture(_plan.TargetPath, CancellationToken.None);
        return actual.Shape == _plan.Before.Snapshot.Shape && actual.Entries.SequenceEqual(_plan.Before.Snapshot.Entries) &&
            PhysicalTreeIdentity.Capture(_plan.TargetPath, actual).SequenceEqual(_plan.Before.OriginalObjects);
    }

    private ConfigurationWriterObservation Observe()
    {
        _phase?.Invoke("configuration-stop");
        var observed = _observeWriters();
        if (observed.InstanceId != _plan.InstanceId || observed.JournalId != _plan.JournalId)
            return new(_plan.InstanceId, _plan.JournalId, false, WriterState.Unknown);
        if (observed.IsComplete && observed.State == WriterState.Stopped)
        {
            try
            {
                if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
                StepWritersEvidence.RequireConfigurationStopped(_instance, _database, _contracts, _plan.JournalId);
            }
            catch (IOException) when (_phase is null) { return new(_plan.InstanceId, _plan.JournalId, false, WriterState.Unknown); }
        }
        return observed;
    }

    private ConfigurationWriterObservation RequireStopped()
    {
        var observed = Observe();
        if (!observed.IsComplete || observed.State != WriterState.Stopped) throw new IOException("configuration_writer_stop_unconfirmed");
        return observed;
    }

    private void ReconcileAcceptedHistory(bool allowCommitted = false)
    {
        _phase?.Invoke("configuration-publication");
        var registered = _registry.ReadJournal(_plan.JournalId);
        foreach (var next in _store.ReadAllRevisions(_plan.JournalId).Where(item => item.Revision > registered.Revision))
        {
            if (next.Phase == JournalPhase.Committed && !allowCommitted) throw new IOException("committed_journal_requires_reconciliation");
            var receipt = next.Root.GetProperty("receipts").EnumerateArray().Last();
            var step = receipt.GetProperty("stepId").GetString()!;
            var recoveryVerified = _prepared is not null && Observe() is { IsComplete: true, State: WriterState.Stopped };
            _phase?.Invoke("configuration-publication");
            if (step is "retire-candidate" or "restore-original")
            {
                if (!recoveryVerified) throw new IOException("configuration_writer_stop_unconfirmed");
                ProjectRestorationEffect(next, step, receipt.GetProperty("effect").GetString()!);
            }
            _registry.Advance(registered with { Revision = next.Revision, Phase = next.Phase }, next.Revision - 1,
                noEffect: next.Phase == JournalPhase.Committed && _prepared is null && IsUnexecutedCommit() ? new(true, false, false, false) : null,
                hasVerifiedRecoveryEvidence: recoveryVerified);
        }
    }

    private void CloseInterruptedEffects()
    {
        var digest = _store.Read(_plan.JournalId).Sha256;
        foreach (var effect in _outbox.Read(_plan.JournalId))
        {
            if (effect.State == FileEffectState.Pending)
                _outbox.Record(_plan.JournalId, effect.StepId, effect.State, FileEffectState.NotExecuted, digest, new(true, false, false, false));
            else if (effect.State == FileEffectState.Started)
                _outbox.Record(_plan.JournalId, effect.StepId, effect.State, FileEffectState.Completed, digest);
        }
    }

    private void Save(JournalPhase phase, string step, string effect, NoEffectEvidence? noEffect = null,
        bool recoveryVerified = false, string detailCode = "configuration_recovery_observed")
    {
        _phase?.Invoke("configuration-publication");
        var previous = CurrentRevision;
        _journal["revision"] = previous + 1;
        _journal["phase"] = phase.ToString().ToLowerInvariant();
        _journal["updatedAt"] = DateTimeOffset.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'");
        _journal["receipts"]!.AsArray().Add(new JsonObject
        {
            ["receiptId"] = Guid.NewGuid().ToString("D"), ["recordedAt"] = _journal["updatedAt"]!.GetValue<string>(),
            ["stepId"] = step, ["effect"] = effect, ["detailCode"] = detailCode
        });
        var next = _store.Append(_plan.JournalId, previous, JsonSerializer.SerializeToUtf8Bytes(_journal), noEffect, recoveryVerified);
        _observer?.Reached(ConfigurationPersistencePoint.JournalFlushed, _plan.JournalId, next.Revision, next.Phase);
        ProjectRestorationEffect(next, step, effect);
        var registered = _registry.ReadJournal(_plan.JournalId);
        _registry.Advance(registered with { Revision = next.Revision, Phase = next.Phase }, previous, noEffect, recoveryVerified);
        _observer?.Reached(ConfigurationPersistencePoint.SqlProjectionCommitted, _plan.JournalId, next.Revision, next.Phase);
    }

    private void ProjectRestorationEffect(JournalDocument next, string step, string effect)
    {
        if (step is not ("retire-candidate" or "restore-original")) return;
        var pending = _outbox.Read(_plan.JournalId).SingleOrDefault(item => item.StepId == step);
        if (pending is not null && pending.State is FileEffectState.Pending or FileEffectState.Started)
        {
            var state = effect == "started" ? FileEffectState.Started : FileEffectState.Completed;
            if (state != pending.State) _outbox.Record(_plan.JournalId, step, pending.State, state, next.Sha256);
        }
    }

    private bool HasReceipt(string step, string effect) => _journal["receipts"]!.AsArray().Any(item =>
        item!["stepId"]!.GetValue<string>() == step && item["effect"]!.GetValue<string>() == effect);

    private long CurrentRevision => _journal["revision"]!.GetValue<long>();
    private JournalPhase CurrentPhase => Enum.Parse<JournalPhase>(_journal["phase"]!.GetValue<string>(), true);
}
