using System.Collections.Immutable;
using System.Security.Cryptography;
using Microsoft.Data.Sqlite;
using NexusPipeline.Core.Configuration;
using NexusPipeline.Core.Operations;
using NexusPipeline.Platform.Storage.Journals;
using NexusPipeline.Platform.Storage.Sqlite;

namespace NexusPipeline.Platform.Storage.Extensions;

internal static class ExtensionRestorationProjection
{
    internal static void ApplyExpected(Dictionary<string, FileEffect> expected, RegisteredJournal registered,
        IReadOnlyList<JournalDocument> history, ImmutableArray<ExtensionRestorationEntry> entries)
    {
        var first = entries[0];
        foreach (var entry in entries.Where(item => item.Intent.BeforeRevision < registered.Revision))
        {
            var digest = Digest(history.Single(item => item.Revision == entry.Intent.BeforeRevision + 1));
            switch (entry.Record.Stage)
            {
                case "started":
                    foreach (var effect in ReverseEffects(first))
                        expected.Add(effect.StepId, effect with { State = FileEffectState.Started, ReceiptDigest = digest });
                    break;
                case "candidate-retired": Complete("restore-retire-candidate", digest); break;
                case "original-restored": Complete("restore-original", digest); break;
                case "restored":
                    foreach (var effect in expected.Values.Where(item => (item.Action is FileEffectAction.PreserveOriginal or
                        FileEffectAction.InstallCandidate) && (item.State is FileEffectState.Pending or FileEffectState.Started)).ToArray())
                        expected[effect.StepId] = effect with { State = FileEffectState.Reverted, ReceiptDigest = digest };
                    break;
                case "committed": break;
                default: throw Invalid();
            }
        }
        void Complete(string step, string digest)
        {
            if (!expected.TryGetValue(step, out var effect) || effect.State != FileEffectState.Started) throw Invalid();
            expected[step] = effect with { State = FileEffectState.Completed, ReceiptDigest = digest };
        }
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    internal static void Advance(StateDatabase database, SqliteConnection connection, SqliteTransaction transaction,
        BoundExtensionPayload bound, ExtensionRestorationEntry entry, JournalDocument document,
        JournalRecoveryOwnership.ExtensionStopProof stop, ExtensionTreeEffects.RestoredTreeLease? restored)
    {
        if (entry.Record.Stage is "restored" or "committed")
            (restored ?? throw Invalid()).Require(bound.Plan, bound.Physical[^1].Record);
        if (!bound.Restoration.Contains(entry) || !bound.History.Any(item => item.Revision == document.Revision &&
                item.Export().AsSpan().SequenceEqual(document.Export()))) throw Invalid();
        stop.RequireInside(connection, transaction, bound.Plan.JournalId);
        var actual = RecoveryJournalRegistry.ReadJournalInside(connection, transaction, bound.Plan.JournalId);
        if (actual.Kind != JournalKind.ExtensionApply || actual.OperationId != bound.Source.OperationId ||
            actual.Revision != bound.Registered.Revision || actual.Revision != entry.Intent.BeforeRevision ||
            actual.Phase != bound.Registered.Phase || actual.PlanPath != bound.PlanReference.RelativePath ||
            actual.PlanDigest != bound.PlanReference.Sha256 || !actual.ResourceKeys.SequenceEqual(bound.Plan.ResourceKeys) ||
            OperationRows.ReadExisting(connection, transaction, bound.Identity) is not { Status: "accepted" }) throw Invalid();
        JournalEffectGate.Require(connection, transaction, actual.JournalId, EffectGateState.Entered);
        ExtensionInstalledRows.RequireExpectedInside(connection, transaction, bound.Source.ExtensionId,
            bound.Source.ExpectedInstalledRevision, bound.Source.OriginalFootprint);
        PayloadEffectCursor.RequireEffectsInside(connection, transaction, actual,
            bound.Plan, bound.History, bound.Physical, bound.Restoration);
        new RecoveryJournalRegistry(database).AdvanceInside(connection, transaction,
            actual with { Revision = document.Revision, Phase = document.Phase }, actual.Revision,
            hasVerifiedRecoveryEvidence: true);
        var digest = Digest(document);
        switch (entry.Record.Stage)
        {
            case "started":
                var reverse = ReverseEffects(entry);
                FileEffectRows.Insert(connection, transaction, actual.JournalId, reverse);
                foreach (var effect in reverse) Move(effect.StepId, FileEffectState.Pending, FileEffectState.Started);
                break;
            case "candidate-retired": Move("restore-retire-candidate", FileEffectState.Started, FileEffectState.Completed); break;
            case "original-restored": Move("restore-original", FileEffectState.Started, FileEffectState.Completed); break;
            case "restored":
                foreach (var effect in FileEffectRows.Read(connection, actual.JournalId, transaction)
                    .Where(item => (item.Action is FileEffectAction.PreserveOriginal or FileEffectAction.InstallCandidate) &&
                        (item.State is FileEffectState.Pending or FileEffectState.Started)))
                    Move(effect.StepId, effect.State, FileEffectState.Reverted);
                break;
            case "committed":
                using (var command = connection.CreateCommand())
                {
                    command.Transaction = transaction;
                    command.CommandText = "UPDATE operations SET status='completed' WHERE operation_id=$op AND status='accepted' AND initial_journal_id=$journal;";
                    command.Parameters.AddWithValue("$op", actual.OperationId.ToString("D"));
                    command.Parameters.AddWithValue("$journal", actual.JournalId.ToString("D"));
                    if (command.ExecuteNonQuery() != 1) throw Invalid();
                }
                break;
            default: throw Invalid();
        }
        void Move(string step, FileEffectState previous, FileEffectState next)
        {
            using var command = connection.CreateCommand(); command.Transaction = transaction;
            command.CommandText = "UPDATE file_effects SET state=$next,receipt_digest=$digest WHERE journal_id=$journal " +
                "AND step_id=$step AND state=$previous AND EXISTS(SELECT 1 FROM operation_journals WHERE journal_id=$journal " +
                "AND operation_kind='extensionApply' AND revision=$revision AND phase=$phase);";
            command.Parameters.AddWithValue("$next", FileEffectRows.Wire(next));
            command.Parameters.AddWithValue("$digest", digest);
            command.Parameters.AddWithValue("$journal", actual.JournalId.ToString("D"));
            command.Parameters.AddWithValue("$step", step);
            command.Parameters.AddWithValue("$previous", FileEffectRows.Wire(previous));
            command.Parameters.AddWithValue("$revision", document.Revision);
            command.Parameters.AddWithValue("$phase", FileEffectRows.Wire(document.Phase));
            if (command.ExecuteNonQuery() != 1) throw Invalid();
        }
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    internal static void SynchronizePhysical(StateDatabase database, SqliteConnection connection, SqliteTransaction transaction,
        BoundExtensionPayload bound, ExtensionPhysicalEntry entry, JournalDocument document,
        JournalRecoveryOwnership.ExtensionStopProof stop)
    {
        if (!bound.Physical.Contains(entry) || !bound.History.Any(item => item.Revision == document.Revision &&
                item.Export().AsSpan().SequenceEqual(document.Export()))) throw Invalid();
        stop.RequireInside(connection, transaction, bound.Plan.JournalId);
        var actual = RecoveryJournalRegistry.ReadJournalInside(connection, transaction, bound.Plan.JournalId);
        if (actual.Kind != JournalKind.ExtensionApply || actual.OperationId != bound.Source.OperationId ||
            actual.Revision != bound.Registered.Revision || actual.Revision != entry.Intent.BeforeRevision ||
            actual.Phase != JournalPhase.Applying || document.Phase != JournalPhase.Applying ||
            actual.PlanPath != bound.PlanReference.RelativePath || actual.PlanDigest != bound.PlanReference.Sha256 ||
            !actual.ResourceKeys.SequenceEqual(bound.Plan.ResourceKeys) ||
            OperationRows.ReadExisting(connection, transaction, bound.Identity) is not { Status: "accepted" }) throw Invalid();
        JournalEffectGate.Require(connection, transaction, actual.JournalId, EffectGateState.Entered);
        ExtensionInstalledRows.RequireExpectedInside(connection, transaction, bound.Source.ExtensionId,
            bound.Source.ExpectedInstalledRevision, bound.Source.OriginalFootprint);
        PayloadEffectCursor.RequireEffectsInside(connection, transaction, actual, bound.Plan,
            bound.History, bound.Physical, bound.Restoration);
        new RecoveryJournalRegistry(database).AdvanceInside(connection, transaction,
            actual with { Revision = document.Revision, Phase = document.Phase }, actual.Revision,
            hasVerifiedRecoveryEvidence: true);
        ExtensionPhysicalJournal.RecordEffectsInside(connection, transaction, bound.Plan, document, entry.Record.Stage);
    }

    private static FileEffect[] ReverseEffects(ExtensionRestorationEntry first)
    {
        var value = first.Record;
        var effects = new List<FileEffect>();
        if (value.RetireCandidate) effects.Add(new(value.JournalId, "restore-retire-candidate",
            FileEffectAction.RetireCandidate, FileEffectState.Pending, first.Intent.Artifact.Sha256, null));
        if (value.RestoreOriginal) effects.Add(new(value.JournalId, "restore-original",
            FileEffectAction.RestoreOriginal, FileEffectState.Pending, first.Intent.Artifact.Sha256, null));
        return effects.ToArray();
    }
    private static string Digest(JournalDocument value) => Convert.ToHexStringLower(SHA256.HashData(value.Export()));
    private static IOException Invalid() => new("extension_restoration_projection_unbound");
}
