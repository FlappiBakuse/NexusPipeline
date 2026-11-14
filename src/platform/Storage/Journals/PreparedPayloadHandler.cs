using NexusPipeline.Core.Configuration;
using NexusPipeline.Platform.Storage.Extensions;
using NexusPipeline.Platform.Serialization;
using NexusPipeline.Platform.Storage.Sqlite;
using NexusPipeline.Platform.Storage.Maintenance;

namespace NexusPipeline.Platform.Storage.Journals;

public sealed class PreparedPayloadHandler : IJournalRecoveryHandler
{
    private readonly RuntimeInstance _instance;
    private readonly ContractReader _contracts;
    private readonly StateDatabase _database;
    private readonly PayloadApplyRoots _roots;
    private readonly Func<Guid, PayloadWriterObservation> _writers;
    private readonly IPayloadPersistenceObserver? _observer;
    private readonly JournalRecoveryOwnership? _recoveryOwnership;
    private readonly InstanceLease? _executionLease;
    private readonly UpdateFeedVerifier? _maintenanceVerifier;
    public JournalKind Kind { get; }

    public PreparedPayloadHandler(RuntimeInstance instance, ContractReader contracts, StateDatabase database,
        PayloadApplyRoots roots, JournalKind kind, Func<Guid, PayloadWriterObservation> writers, IPayloadPersistenceObserver? observer = null,
        JournalRecoveryOwnership? recoveryOwnership = null, InstanceLease? executionLease = null,
        UpdateReleaseTrust? maintenanceTrust = null)
    {
        if (kind is not (JournalKind.UpdateApply or JournalKind.ExtensionApply)) throw new ArgumentException("payload_recovery_kind_invalid");
        if (database.InstanceId != instance.InstanceId) throw new IOException("payload_recovery_instance_unbound");
        _instance = instance; _contracts = contracts; _database = database; _roots = roots; Kind = kind; _writers = writers; _observer = observer;
        _recoveryOwnership = recoveryOwnership; _executionLease = executionLease;
        _maintenanceVerifier = maintenanceTrust is null ? null : new(contracts, maintenanceTrust.Policy);
    }

    public IJournalRecoveryWork Prepare(RegisteredJournal journal)
    {
        if (journal.Kind != Kind) throw new IOException("payload_handler_kind_unbound");
        var frozen = journal with { ResourceKeys = Array.AsReadOnly(journal.ResourceKeys.ToArray()) };
        Verify(frozen);
        if (IsEnteredMaintenance(frozen))
            return new EnteredMaintenanceWork(() => ReadMaintenance(frozen), () =>
            {
                if (!OperatingSystem.IsWindows() || _recoveryOwnership is null || _executionLease is null || _maintenanceVerifier is null)
                    throw new IOException("maintenance_recovery_owner_unbound");
                var current = ReadMaintenance(frozen);
                return current.Commit is not null
                    ? new MaintenanceCommitStore(_instance, _database, _contracts, _roots, _maintenanceVerifier, _observer)
                        .ReconcileRecorded(_executionLease, _recoveryOwnership, frozen.JournalId)
                    : new MaintenancePayloadRestoration(_instance, _database, _contracts, _roots, _executionLease,
                        _recoveryOwnership, _maintenanceVerifier, _observer).Recover(frozen.JournalId);
            });
        if (IsEnteredExtension(frozen))
            return new EnteredExtensionWork(() => ReadEntered(frozen), () =>
            {
                if (!OperatingSystem.IsWindows() || _recoveryOwnership is null || _executionLease is null)
                    throw new IOException("extension_recovery_owner_unbound");
                var current = ReadEntered(frozen);
                if (!current.Restoration.IsEmpty || current.Latest.Phase == JournalPhase.Applying)
                    return new ExtensionPayloadRestoration(_instance, _database, _contracts, _roots,
                        _executionLease, _recoveryOwnership, _observer).Recover(frozen.JournalId);
                if ((current.Plan.Action == PayloadAction.Remove || !current.Source.DesiredEnabled) && current.Latest.Phase == JournalPhase.Applied)
                    return new PayloadCommitStore(_instance, _database, _contracts, _roots, _observer)
                        .RecoverWithoutActivation(_executionLease, _recoveryOwnership, frozen.JournalId);
                return new ExtensionCommittedReconciliation(_instance, _database, _contracts, _roots,
                    _executionLease, _recoveryOwnership).Reconcile(frozen.JournalId);
            });
        return new Work(() =>
        {
            Verify(frozen);
            if (!Directory.Exists(JournalPath(frozen.JournalId)))
                new PayloadApplyAcceptance(_instance, _contracts, _database, _roots, _observer).MaterializeInitial(frozen);
            return new(_instance, frozen.JournalId, _contracts, _database, _roots, () => _writers(frozen.JournalId), _observer);
        });
    }

    private void Verify(RegisteredJournal expected)
    {
        var snapshot = new RecoveryJournalRegistry(_database).ReadSnapshot(expected.JournalId);
        RecoveryRegistryPolicy.Verify(snapshot);
        var actual = snapshot.Journals.Single(item => item.JournalId == expected.JournalId);
        if (actual.OperationId != expected.OperationId || actual.Kind != expected.Kind || actual.Phase != expected.Phase ||
            actual.Revision != expected.Revision || actual.PlanPath != expected.PlanPath || actual.PlanDigest != expected.PlanDigest ||
            !actual.ResourceKeys.SequenceEqual(expected.ResourceKeys)) throw new IOException("payload_registration_changed_after_preflight");
        if (Directory.Exists(JournalPath(expected.JournalId)))
        {
            if (IsEnteredMaintenance(expected))
                _ = ReadMaintenance(expected);
            else if (IsEnteredExtension(expected))
                _ = new ExtensionPayloadBinding(_instance, _database, _contracts, _roots).Read(expected.JournalId);
            else
                _ = PayloadPreparedBinding.Read(_instance, _contracts, _database, _roots, expected.JournalId);
            return;
        }
        if (File.Exists(JournalPath(expected.JournalId)) || expected.Phase != JournalPhase.Prepared || expected.Revision != 1)
            throw new IOException("missing_payload_journal_not_initial");
        var plan = PayloadPreparedBinding.ReadPlan(_instance, _contracts, _roots, expected);
        PayloadPreparedBinding.VerifyPending(snapshot, plan, expected);
        var gate = new JournalEffectGate(_database).Read(expected.JournalId);
        if (gate.Kind != plan.Kind || gate.OperationId != plan.OperationId || gate.PlanDigest != expected.PlanDigest)
            throw new IOException("missing_payload_journal_gate_unbound");
    }

    private bool IsEnteredExtension(RegisteredJournal journal) =>
        journal.Kind == JournalKind.ExtensionApply &&
        new JournalEffectGate(_database).Read(journal.JournalId).State == EffectGateState.Entered;

    private bool IsEnteredMaintenance(RegisteredJournal journal) => journal.Kind == JournalKind.UpdateApply &&
        new JournalEffectGate(_database).Read(journal.JournalId).State == EffectGateState.Entered;

    private BoundMaintenancePayload ReadMaintenance(RegisteredJournal expected)
    {
        if (!OperatingSystem.IsWindows() || _maintenanceVerifier is null)
            throw new IOException("maintenance_recovery_trust_unavailable");
        var bound = new MaintenancePayloadBinding(_instance, _database, _contracts, _roots, _maintenanceVerifier).Read(expected.JournalId);
        if (bound.Registered.OperationId != expected.OperationId || bound.Registered.Kind != expected.Kind ||
            bound.Registered.Phase != expected.Phase || bound.Registered.Revision != expected.Revision ||
            bound.PlanReference.RelativePath != expected.PlanPath || bound.PlanReference.Sha256 != expected.PlanDigest ||
            !bound.Registered.ResourceKeys.SequenceEqual(expected.ResourceKeys))
            throw new IOException("payload_registration_changed_after_preflight");
        return bound;
    }

    private sealed class EnteredMaintenanceWork(Func<BoundMaintenancePayload> read, Func<JournalRecoveryResult> reconcile) : IJournalRecoveryWork
    {
        public JournalRecoveryResult Execute()
        {
            var bound = read();
            if (bound.Registered.Phase == JournalPhase.Committed && bound.Latest.Phase == JournalPhase.Committed &&
                bound.Registered.Revision == bound.Latest.Revision) return JournalRecoveryResult.AlreadyCompleted;
            return reconcile();
        }
    }

    private BoundExtensionPayload ReadEntered(RegisteredJournal expected)
    {
        var bound = new ExtensionPayloadBinding(_instance, _database, _contracts, _roots).Read(expected.JournalId);
        if (bound.Registered.OperationId != expected.OperationId || bound.Registered.Kind != expected.Kind ||
            bound.Registered.Phase != expected.Phase || bound.Registered.Revision != expected.Revision ||
            bound.PlanReference.RelativePath != expected.PlanPath || bound.PlanReference.Sha256 != expected.PlanDigest ||
            !bound.Registered.ResourceKeys.SequenceEqual(expected.ResourceKeys))
            throw new IOException("payload_registration_changed_after_preflight");
        return bound;
    }

    private sealed class EnteredExtensionWork(Func<BoundExtensionPayload> read, Func<JournalRecoveryResult> reconcile) : IJournalRecoveryWork
    {
        public JournalRecoveryResult Execute()
        {
            var bound = read();
            if (bound.Registered.Phase == JournalPhase.Committed && bound.Latest.Phase == JournalPhase.Committed &&
                bound.Registered.Revision == bound.Latest.Revision && bound.Commit is not null)
                return JournalRecoveryResult.AlreadyCompleted;
            if (!bound.Restoration.IsEmpty && bound.Registered.Phase == JournalPhase.Committed &&
                bound.Latest.Phase == JournalPhase.Committed && bound.Registered.Revision == bound.Latest.Revision &&
                bound.Restoration[^1].Record.Stage == "committed") return JournalRecoveryResult.AlreadyCompleted;
            if (bound.Latest.Phase is JournalPhase.Committed or JournalPhase.Applying || !bound.Restoration.IsEmpty ||
                (bound.Plan.Action == PayloadAction.Remove || !bound.Source.DesiredEnabled) && bound.Latest.Phase == JournalPhase.Applied)
                return reconcile();
            throw new IOException("extension_payload_entered_recovery_pending");
        }
    }

    private string JournalPath(Guid id) => PhysicalPathPolicy.NormalizeWithoutLinks(Path.Combine(_instance.RuntimeRoot, "journals", id.ToString("D")));
    private sealed class Work(Func<PreparedPayloadSession> open) : IJournalRecoveryWork
    {
        public JournalRecoveryResult Execute() => new PreparedPayloadRecovery(open()).Recover();
    }
}
