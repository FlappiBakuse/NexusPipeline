using System.Runtime.Versioning;
using NexusPipeline.Core.Configuration;
using NexusPipeline.Core.Execution;
using NexusPipeline.Core.Secrets;
using NexusPipeline.Platform.Serialization;
using NexusPipeline.Platform.Storage.Journals;
using NexusPipeline.Platform.Storage.Notifications;
using NexusPipeline.Platform.Storage.Sqlite;

namespace NexusPipeline.Platform.Storage.Secrets;

[SupportedOSPlatform("windows")]
internal sealed class SecretBatchRecovery : IJournalRecoveryWork
{
    private sealed record Child(FrozenSecretPreparation Frozen, BlobReference Reference, SecretPreparationJournal Journal,
        SecretPreparationEvidence Evidence, PreparedSecretPayload? Source, bool CanComplete);
    private readonly RuntimeInstance _instance;
    private readonly StateDatabase _database;
    private readonly ContractReader _contracts;
    private readonly Func<Guid, WriterStopObservation> _writers;
    private readonly SecretBatchPlan _publication;
    private readonly List<Child> _children = [];
    private readonly List<IDisposable> _held = [];
    private readonly bool _wasClosed;

    internal SecretBatchRecovery(RuntimeInstance instance, StateDatabase database, ContractReader contracts,
        SecretBatchPlan publication, Func<Guid, WriterStopObservation> writers)
    {
        _instance = instance; _database = database; _contracts = contracts; _publication = publication; _writers = writers;
        try
        {
            _wasClosed = publication.Status() == "completed";
            foreach (var reference in publication.Preparations)
            {
                var frozen = new PreparationPlanCodec(instance, contracts, database).Read(reference);
                var journal = new SecretPreparationJournal(instance, contracts, database, frozen, reference);
                var physical = journal.ReadOptional();
                var evidence = new PreparationEvidenceStore(instance, contracts, database).Read(frozen, reference, inspectCandidate: !_wasClosed);
                _held.Add(evidence);
                var effect = new FileEffectOutbox(database).Read(journal.Id).Single();
                VerifyEffect(effect, reference, evidence);
                if (_wasClosed)
                {
                    if (physical is not { Phase: JournalPhase.Committed } || SecretPreparationJournal.NoEffects(physical) ||
                        journal.Registered().Phase != physical.Phase || journal.Registered().Revision != physical.Revision)
                        throw new IOException("notification_closed_child_journal_unbound");
                    continue;
                }
                var gate = new JournalEffectGate(database).Read(journal.Id);
                var canComplete = gate.State == EffectGateState.Entered && physical?.Phase is
                    JournalPhase.Applying or JournalPhase.Applied or JournalPhase.Committed &&
                    evidence.CandidateVerified && evidence.Started is not null && evidence.Completed is not null &&
                    !SecretPreparationJournal.NoEffects(physical);
                PreparedSecretPayload? source = null;
                if (gate.State == EffectGateState.Unentered && physical?.Phase is null or JournalPhase.Prepared && evidence.NoEffects &&
                    new PreparationEvidenceStore(instance, contracts, database).StillNoEffects(frozen.ReadWire()) &&
                    effect.State == FileEffectState.Pending)
                {
                    source = SecretCandidateSource.Read(instance, database, contracts, frozen, reference);
                    _held.Add(source);
                }
                _children.Add(new(frozen, reference, journal, evidence, source, canComplete));
            }
        }
        catch { Dispose(); throw; }
    }

    public JournalRecoveryResult Execute()
    {
        var status = _publication.Status();
        if (status == "completed") return JournalRecoveryResult.AlreadyCompleted;
        if (_wasClosed) throw new IOException("notification_publication_status_rewound");
        var stopped = _children.All(child =>
        {
            var observed = _writers(child.Journal.Id);
            return observed.IsComplete && observed.State == WriterState.Stopped;
        });
        if (!stopped || _children.Any(child => child.Source is null && !child.CanComplete))
            throw new IOException("notification_publication_recovery_unverified");
        foreach (var child in _children)
        {
            if (child.Source is not null)
            {
                RegisterCurrentExecutor(child.Journal.Id);
                _ = new SecretPreparationExecutor(_instance, _contracts, _database).WriteAcceptedCandidate(child.Source);
            }
            else CompleteExisting(child);
        }
        _publication.Close();
        return JournalRecoveryResult.Completed;
    }

    private void RegisterCurrentExecutor(Guid journalId)
    {
        var writer = JournalWriterStore.CurrentWriter;
        _database.Write((connection, transaction) =>
        {
            using var command = connection.CreateCommand(); command.Transaction = transaction;
            command.CommandText = "SELECT 1 FROM journal_writers WHERE journal_id=$journal AND writer_id=$writer;";
            command.Parameters.AddWithValue("$journal", journalId.ToString("D")); command.Parameters.AddWithValue("$writer", writer.WriterId.ToString("D"));
            if (command.ExecuteScalar() is not null) throw new IOException("notification_recovery_original_writer_still_current");
            JournalWriterStore.Insert(connection, transaction, journalId, writer);
            return true;
        });
    }

    private void CompleteExisting(Child child)
    {
        child.Journal.ProjectNonterminal();
        var outbox = new FileEffectOutbox(_database);
        var effect = outbox.Read(child.Journal.Id).Single();
        if (effect.State == FileEffectState.Pending)
            outbox.Record(child.Journal.Id, effect.StepId, FileEffectState.Pending, FileEffectState.Started, child.Evidence.Started!.Digest);
        if (outbox.Read(child.Journal.Id).Single().State == FileEffectState.Started)
            outbox.Record(child.Journal.Id, effect.StepId, FileEffectState.Started, FileEffectState.Completed, child.Evidence.Completed!.Digest);
        var physical = child.Journal.ReadOptional()!;
        if (physical.Phase == JournalPhase.Applying) child.Journal.Append(JournalPhase.Applied);
        child.Journal.ProjectNonterminal();
        if (child.Journal.ReadOptional()!.Phase != JournalPhase.Committed) child.Journal.Append(JournalPhase.Committed);
    }

    private static void VerifyEffect(FileEffect effect, BlobReference reference, SecretPreparationEvidence evidence)
    {
        var digest = effect.State switch
        {
            FileEffectState.Pending => null,
            FileEffectState.Started => evidence.Started?.Digest ?? throw new IOException("secret_started_receipt_missing"),
            FileEffectState.Completed => evidence.Completed?.Digest ?? throw new IOException("secret_completed_receipt_missing"),
            _ => throw new IOException("notification_child_effect_unknown")
        };
        if (effect.StepId != "secret-payload" || effect.Action != FileEffectAction.InstallCandidate ||
            effect.IntentDigest != reference.Sha256 || effect.ReceiptDigest != digest)
            throw new IOException("notification_child_effect_unbound");
    }

    public void Dispose()
    {
        for (var index = _held.Count - 1; index >= 0; index--) _held[index].Dispose();
        _held.Clear();
    }
}

internal sealed record SecretBatchPlan(Func<string> Status, IReadOnlyList<BlobReference> Preparations, Action Close);
