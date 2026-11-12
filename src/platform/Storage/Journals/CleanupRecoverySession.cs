using System.Text.Json;
using System.Text.Json.Nodes;
using NexusPipeline.Core.Configuration;
using NexusPipeline.Core.Execution;
using NexusPipeline.Platform.Serialization;
using NexusPipeline.Platform.Storage.Sqlite;
using NexusPipeline.Platform.Storage.Extensions;

namespace NexusPipeline.Platform.Storage.Journals;

public sealed record CleanupWriterObservation(Guid InstanceId, Guid JournalId, bool IsComplete, WriterState State);

public sealed class CleanupRecoverySession : ICleanupRecoverySession
{
    private readonly RuntimeInstance _instance;
    private readonly FrozenFileCleanup _plan;
    private readonly JournalStore _store;
    private readonly RecoveryJournalRegistry _registry;
    private readonly FileEffectOutbox _outbox;
    private readonly FileCleanupProjection _projection;
    private readonly BlobCleanup _cleanup;
    private readonly Func<CleanupWriterObservation> _writers;
    private readonly ICleanupPersistenceObserver? _observer;
    private readonly JsonObject _journal;
    private readonly WorkCleanupItems? _work;
    private readonly PhysicalObjectDeletion.FileLease? _held;

    public CleanupRecoverySession(RuntimeInstance instance, Guid journalId, ContractReader contracts, StateDatabase database,
        Func<CleanupWriterObservation> writers, ICleanupPersistenceObserver? observer = null, PayloadApplyRoots? payloadRoots = null)
        : this(instance, journalId, contracts, database, writers, observer, payloadRoots, null) { }

    internal CleanupRecoverySession(RuntimeInstance instance, Guid journalId, ContractReader contracts, StateDatabase database,
        Func<CleanupWriterObservation> writers, ICleanupPersistenceObserver? observer, PayloadApplyRoots? payloadRoots,
        ExtensionWorkExecution? workExecution, PhysicalObjectDeletion.FileLease? held = null, BlobCleanup? cleanup = null)
    {
        if (database.InstanceId != instance.InstanceId) throw new IOException("cleanup_database_unbound");
        _instance = instance; _writers = writers; _observer = observer; _held = held;
        _store = new(instance, contracts); _registry = new(database); _outbox = new(database);
        var bound = CleanupJournalBinding.Read(instance, contracts, _registry.ReadSnapshot(journalId), journalId, database, payloadRoots);
        _plan = bound.Plan; _journal = bound.Document;
        if (_plan.ExtensionWork is not null)
            _work = new(instance, database, contracts, payloadRoots ?? throw new IOException("extension_work_roots_required"), _plan, workExecution);
        _projection = new(_store, _registry, _outbox, observer, database, workExecution);
        var blobs = new ImmutableBlobStore(instance, contracts);
        _cleanup = cleanup ?? new(instance, blobs, new(instance, contracts, database, blobs, payloadRoots));
    }

    internal IDisposable ProtectReadHistory() => _store.ProtectReadHistory(_plan.JournalId);

    public FileCleanupInspection Inspect() => InspectItems(readReferences: true);

    public FileCleanupInspection InspectRecovery() => InspectItems(readReferences: Phase != JournalPhase.Prepared);

    private FileCleanupInspection InspectItems(bool readReferences)
    {
        var registered = _registry.ReadJournal(_plan.JournalId);
        var agrees = registered.Phase == Phase && registered.Revision == Revision;
        var observed = Observe();
        var inspected = _work is null ? _cleanup.Inspect(_plan.Items.Where(item =>
            !_journal["receipts"]!.AsArray().Any(node => node!["stepId"]!.GetValue<string>() == item.StepId &&
                node["effect"]!.GetValue<string>() is "completed" or "notExecuted")).ToArray(), _held, readReferences) : null;
        IReadOnlyList<CleanupItemInspection> items = _work is not null ? _work.InspectItems(_journal) :
            Array.AsReadOnly(_plan.Items.Select(item => new CleanupItemInspection(item.StepId, InspectItem(item, inspected!))).ToArray());
        // A durable cancellation marks items skipped; its proof still comes from their current physical inventory.
        var noEffects = Phase == JournalPhase.Prepared &&
            (_work is null ? inspected!.Values.All(state => state is CleanupItemState.VerifiedUnreferenced or CleanupItemState.Referenced) :
                items.All(item => item.State == CleanupItemState.VerifiedUnreferenced)) &&
            _outbox.Read(_plan.JournalId).All(item => item.State is FileEffectState.Pending or FileEffectState.NotExecuted) &&
            _journal["receipts"]!.AsArray().All(node => node!["stepId"]!.GetValue<string>() == "verify-no-effect");
        var reconciles = Phase == JournalPhase.Committed && items.All(item => item.State is CleanupItemState.DeletedWithEvidence or CleanupItemState.SkippedWithEvidence);
        return new(Phase, agrees, observed.IsComplete, observed.State, noEffects, reconciles, items);
    }

    public void CommitNoEffects()
    {
        RequireStopped();
        if (!InspectRecovery().CanProveNoEffects) throw new IOException("cleanup_no_effect_unverified");
        Synchronize(false);
        if (_journal["receipts"]!.AsArray().Count == 0)
            Save(JournalPhase.Prepared, "verify-no-effect", "notExecuted", "cleanup_no_effect");
        Save(JournalPhase.Committed, "verify-no-effect", "notExecuted", "cleanup_no_effect");
    }

    public void BeginApplying(bool hasVerifiedRecoveryEvidence)
    {
        RequireStopped();
        if (Inspect().Items.Any(item => item.State == CleanupItemState.Unknown)) throw new IOException("cleanup_inventory_unverified");
        Synchronize(hasVerifiedRecoveryEvidence);
        Save(JournalPhase.Applying, "begin-cleanup", "started", "cleanup_applying", hasVerifiedRecoveryEvidence);
    }

    public CleanupStepResult RemoveVerified(string stepId)
    {
        RequireStopped();
        if (Phase != JournalPhase.Applying) throw new IOException("cleanup_not_applying");
        var previous = _journal["receipts"]!.AsArray().LastOrDefault(node => node!["stepId"]!.GetValue<string>() == stepId);
        if (previous is null) Save(JournalPhase.Applying, stepId, "started", "cleanup_removal_started");
        else if (previous["effect"]!.GetValue<string>() != "started") throw new IOException("cleanup_removal_already_terminal");
        CleanupStepResult result;
        try
        {
            result = _work is null ? _cleanup.RemoveVerified(_plan.Items.Single(item => item.StepId == stepId), _held) :
                _work.Remove(stepId, _journal);
        }
        catch (IOException) { return CleanupStepResult.Unknown; }
        if (result == CleanupStepResult.Deleted)
        {
            _observer?.Reached(CleanupPersistencePoint.FileRemoved, _plan.JournalId, Revision, Phase);
            Save(JournalPhase.Applying, stepId, "completed", "cleanup_deleted");
        }
        return result;
    }

    public void RecordReferencedSkip(string stepId)
    {
        RequireStopped();
        if (_work is not null) throw new IOException("extension_work_referenced_skip_rejected");
        var item = _plan.Items.Single(item => item.StepId == stepId);
        if (_cleanup.Inspect(item, _held) == CleanupItemState.Unknown) throw new IOException("cleanup_referenced_object_unverified");
        Save(JournalPhase.Applying, stepId, "notExecuted", "cleanup_referenced");
    }

    public void MarkApplied()
    {
        RequireStopped();
        RequireTerminalItems();
        Save(JournalPhase.Applied, "finish-cleanup", "completed", "cleanup_applied");
    }

    public void CommitCleanup()
    {
        RequireStopped();
        RequireTerminalItems();
        _projection.Synchronize(_plan, false);
        Save(JournalPhase.Committed, "commit-cleanup", "completed", "cleanup_committed");
    }

    public void Quarantine(string reasonCode)
    {
        _projection.Synchronize(_plan, false);
        Save(JournalPhase.Quarantined, "isolate-cleanup", "unknown", reasonCode);
    }

    public void ReconcileRecordedCommit()
    {
        RequireStopped();
        if (!Inspect().CanReconcileRecordedCommit) throw new IOException("cleanup_recorded_commit_unverified");
        _projection.Synchronize(_plan, false);
    }

    private CleanupItemState InspectItem(FrozenCleanupItem item, IReadOnlyDictionary<string, CleanupItemState> inspected)
    {
        var receipt = _journal["receipts"]!.AsArray().LastOrDefault(node => node!["stepId"]!.GetValue<string>() == item.StepId);
        if (receipt?["effect"]!.GetValue<string>() == "notExecuted") return CleanupItemState.SkippedWithEvidence;
        if (receipt?["effect"]!.GetValue<string>() == "completed")
        {
            try
            {
                var path = PhysicalPathPolicy.NormalizeWithoutLinks(Path.Combine(_instance.RuntimeRoot, item.Blob.RelativePath));
                return !File.Exists(path) && !Directory.Exists(path) && PhysicalObjectIdentity.Read(Path.GetDirectoryName(path)!) == item.ParentIdentity ?
                    CleanupItemState.DeletedWithEvidence : CleanupItemState.Unknown;
            }
            catch (IOException) { return CleanupItemState.Unknown; }
        }
        var current = inspected[item.StepId];
        if (current != CleanupItemState.Unknown && _journal["receipts"]!.AsArray().Any(node => node!["stepId"]!.GetValue<string>() == "verify-no-effect"))
            return CleanupItemState.SkippedWithEvidence;
        return current;
    }

    private void RequireTerminalItems()
    {
        if (Inspect().Items.Any(item => item.State is not (CleanupItemState.DeletedWithEvidence or CleanupItemState.SkippedWithEvidence)))
            throw new IOException("cleanup_items_not_closed");
    }

    private CleanupWriterObservation Observe()
    {
        var observation = _writers();
        return observation.InstanceId == _instance.InstanceId && observation.JournalId == _plan.JournalId ? observation :
            new(_instance.InstanceId, _plan.JournalId, false, WriterState.Unknown);
    }

    private void RequireStopped()
    {
        if (Observe() is not { IsComplete: true, State: WriterState.Stopped }) throw new IOException("cleanup_writer_stop_unconfirmed");
    }

    private void Synchronize(bool verifiedRecovery)
    {
        if (_work is null) _projection.Synchronize(_plan, verifiedRecovery);
        else _work.Publish(() => _projection.Synchronize(_plan, verifiedRecovery));
    }

    private void Save(JournalPhase phase, string step, string effect, string detail, bool verifiedRecovery = false)
    {
        if (_work is null) SaveCore(phase, step, effect, detail, verifiedRecovery);
        else _work.Publish(() => SaveCore(phase, step, effect, detail, verifiedRecovery));
    }

    private void SaveCore(JournalPhase phase, string step, string effect, string detail, bool verifiedRecovery)
    {
        var previous = Revision;
        _journal["revision"] = previous + 1; _journal["phase"] = phase.ToString().ToLowerInvariant();
        _journal["updatedAt"] = DateTimeOffset.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'");
        _journal["receipts"]!.AsArray().Add(new JsonObject
        {
            ["receiptId"] = Guid.NewGuid().ToString("D"), ["recordedAt"] = _journal["updatedAt"]!.GetValue<string>(),
            ["stepId"] = step, ["effect"] = effect, ["detailCode"] = detail
        });
        var noEffects = step == "verify-no-effect" ? new NoEffectEvidence(true, false, false, false) : null;
        var next = _store.Append(_plan.JournalId, previous, JsonSerializer.SerializeToUtf8Bytes(_journal), noEffects, verifiedRecovery);
        _observer?.Reached(CleanupPersistencePoint.JournalFlushed, _plan.JournalId, next.Revision, next.Phase);
        _projection.Synchronize(_plan, verifiedRecovery);
    }

    private long Revision => _journal["revision"]!.GetValue<long>();
    private JournalPhase Phase => Enum.Parse<JournalPhase>(_journal["phase"]!.GetValue<string>(), true);
}

