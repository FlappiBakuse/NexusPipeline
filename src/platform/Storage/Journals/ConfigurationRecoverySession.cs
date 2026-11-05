using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using NexusPipeline.Core.Configuration;
using NexusPipeline.Core.Execution;
using NexusPipeline.Platform.Serialization;
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
    private readonly PreparedConfigurationExchange? _prepared;
    private readonly JsonObject _journal;
    private readonly RuntimeInstance _instance;
    private readonly StateDatabase _database;
    private readonly ContractReader _contracts;

    public ConfigurationRecoverySession(RuntimeInstance instance, Guid journalId, ContractReader contracts, StateDatabase database,
        Func<ConfigurationWriterObservation> observeWriters, IConfigurationPersistenceObserver? observer = null)
    {
        if (database.InstanceId != instance.InstanceId) throw new IOException("configuration_database_instance_unbound");
        _instance = instance; _database = database; _contracts = contracts;
        _observeWriters = observeWriters;
        _observer = observer;
        _store = new(instance, contracts);
        _registry = new(database);
        _outbox = new(database);
        var physical = _store.Read(journalId);
        var binding = ConfigurationJournalBinding.Read(instance, physical, contracts, database);
        _journal = binding.Document;
        _plan = binding.Plan;
        _prepared = binding.Prepared;
        _scanner = new(SnapshotBudget.Default);
        _swap = new(_scanner, new(_scanner, new ImmutableBlobStore(instance, contracts)));
    }

    public ConfigurationRecoveryInspection Inspect()
    {
        var registered = _registry.ReadJournal(_plan.JournalId);
        var phase = CurrentPhase;
        var agrees = registered.Phase == phase && registered.Revision == CurrentRevision;
        if (phase == JournalPhase.Committed && agrees) return new(phase, true, false, WriterState.Unknown, false, false, false, false);
        var writers = Observe();
        var noEffects = false;
        var canRestore = false;
        var restored = false;
        if (writers.IsComplete && writers.State == WriterState.Stopped)
        {
            try
            {
                if (_prepared is null) noEffects = VerifyNoEffects() &&
                    (phase == JournalPhase.Prepared ? _outbox.Read(_plan.JournalId).All(effect => effect.State == FileEffectState.Pending) :
                        phase == JournalPhase.Committed && IsUnexecutedCommit());
                else
                {
                    canRestore = _swap.CanRestore(_prepared, writers);
                    if (canRestore)
                    {
                        try { _swap.VerifyRestored(_prepared, writers); restored = true; }
                        catch (IOException) { }
                    }
                }
            }
            catch (IOException) { }
        }
        var effectsClosed = _outbox.Read(_plan.JournalId).All(effect => effect.Action == FileEffectAction.RemoveOwned ||
            effect.State is FileEffectState.Completed or FileEffectState.NotExecuted);
        return new(phase, agrees, writers.IsComplete, writers.State, noEffects, canRestore, restored,
            phase == JournalPhase.Committed && effectsClosed && (restored || noEffects));
    }

    public void CommitNoEffects()
    {
        RequireStopped();
        if (!VerifyNoEffects()) throw new IOException("configuration_no_effect_unverified");
        ReconcileAcceptedHistory();
        Save(JournalPhase.Prepared, "verify-no-effect", "notExecuted");
        CloseInterruptedEffects();
        Save(JournalPhase.Committed, "verify-no-effect", "notExecuted", new(true, false, false, false));
    }

    public void BeginRestoration()
    {
        var writers = RequireStopped();
        if (_prepared is null || !_swap.CanRestore(_prepared, writers)) throw new IOException("configuration_restoration_unverified");
        ReconcileAcceptedHistory();
        Save(JournalPhase.Reverting, "retire-candidate", "started", recoveryVerified: true);
    }

    public void RetireCandidate()
    {
        var prepared = _prepared ?? throw new IOException("configuration_workspace_unverified");
        _swap.RetireCandidate(prepared, RequireStopped());
        Save(JournalPhase.Reverting, "retire-candidate", "reverted");
    }

    public void RestoreOriginal()
    {
        var prepared = _prepared ?? throw new IOException("configuration_workspace_unverified");
        Save(JournalPhase.Reverting, "restore-original", "started");
        _swap.RestoreOriginal(prepared, RequireStopped());
        _swap.VerifyRestored(prepared, RequireStopped());
        Save(JournalPhase.Reverted, "restore-original", "reverted");
    }

    public void CommitRestoration()
    {
        var prepared = _prepared ?? throw new IOException("configuration_workspace_unverified");
        _swap.VerifyRestored(prepared, RequireStopped());
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
        if (_prepared is not null || PhysicalObjectIdentity.Read(Path.GetDirectoryName(_plan.TargetPath)!) != _plan.ParentIdentity) return false;
        var adjacent = Path.Combine(Path.GetDirectoryName(_plan.TargetPath)!, ".nexus-" + _plan.JournalId.ToString("D"));
        if (_scanner.Capture(adjacent, CancellationToken.None).Shape != FileObjectKind.Missing) return false;
        var actual = _scanner.Capture(_plan.TargetPath, CancellationToken.None);
        return actual.Shape == _plan.Before.Snapshot.Shape && actual.Entries.SequenceEqual(_plan.Before.Snapshot.Entries) &&
            PhysicalTreeIdentity.Capture(_plan.TargetPath, actual).SequenceEqual(_plan.Before.OriginalObjects);
    }

    private ConfigurationWriterObservation Observe()
    {
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
            catch (IOException) { return new(_plan.InstanceId, _plan.JournalId, false, WriterState.Unknown); }
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
        var registered = _registry.ReadJournal(_plan.JournalId);
        foreach (var next in _store.ReadAllRevisions(_plan.JournalId).Where(item => item.Revision > registered.Revision))
        {
            if (next.Phase == JournalPhase.Committed && !allowCommitted) throw new IOException("committed_journal_requires_reconciliation");
            _registry.Advance(registered with { Revision = next.Revision, Phase = next.Phase }, next.Revision - 1,
                noEffect: next.Phase == JournalPhase.Committed && _prepared is null && IsUnexecutedCommit() ? new(true, false, false, false) : null,
                hasVerifiedRecoveryEvidence: _prepared is not null && Observe() is { IsComplete: true, State: WriterState.Stopped });
        }
    }

    private void CloseInterruptedEffects()
    {
        var digest = Convert.ToHexStringLower(SHA256.HashData(_store.Read(_plan.JournalId).Export()));
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
        var pending = _outbox.Read(_plan.JournalId).SingleOrDefault(item => item.StepId == step);
        if (pending is not null && pending.State is FileEffectState.Pending or FileEffectState.Started)
        {
            var state = effect == "started" ? FileEffectState.Started : FileEffectState.Completed;
            if (state != pending.State) _outbox.Record(_plan.JournalId, step, pending.State, state, Convert.ToHexStringLower(SHA256.HashData(next.Export())));
        }
        var registered = _registry.ReadJournal(_plan.JournalId);
        _registry.Advance(registered with { Revision = next.Revision, Phase = next.Phase }, previous, noEffect, recoveryVerified);
        _observer?.Reached(ConfigurationPersistencePoint.SqlProjectionCommitted, _plan.JournalId, next.Revision, next.Phase);
    }

    private long CurrentRevision => _journal["revision"]!.GetValue<long>();
    private JournalPhase CurrentPhase => Enum.Parse<JournalPhase>(_journal["phase"]!.GetValue<string>(), true);
}
