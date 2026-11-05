using System.Globalization;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using NexusPipeline.Core.Configuration;
using NexusPipeline.Core.Operations;
using NexusPipeline.Platform.Serialization;
using NexusPipeline.Platform.Storage.Journals;
using NexusPipeline.Platform.Storage.Sqlite;

namespace NexusPipeline.Platform.Storage.Extensions;

[SupportedOSPlatform("windows")]
internal sealed class ExtensionCommittedReconciliation(RuntimeInstance instance, StateDatabase database,
    ContractReader contracts, PayloadApplyRoots roots, InstanceLease lease, JournalRecoveryOwnership ownership)
{
    internal JournalRecoveryResult Reconcile(Guid journalId)
    {
        lease.VerifyHeld(instance);
        var stop = ownership.CaptureExtensionWriters(instance, database, lease, journalId);
        var binding = new ExtensionPayloadBinding(instance, database, contracts, roots);
        var bound = binding.Read(journalId);
        RequireRecorded(bound);
        using var contents = HoldRecorded(bound);
        using var physical = new ExtensionTreeEffects(new(new(4096, 268435456)),
            new(new(new(4096, 268435456)), new(instance, contracts))).HoldInstalled(bound.Plan, bound.Physical[^1].Record);
        return lease.PublishExecution(() =>
        {
            var fresh = binding.Read(journalId, bound.PlanReference);
            RequireRecorded(fresh);
            if (fresh.Latest.Revision != bound.Latest.Revision ||
                !fresh.Latest.Export().AsSpan().SequenceEqual(bound.Latest.Export())) throw Invalid();
            database.Write((connection, transaction) =>
            {
                stop.RequireInside(connection, transaction, journalId);
                var actual = RecoveryJournalRegistry.ReadJournalInside(connection, transaction, journalId);
                if (actual.OperationId != fresh.Registered.OperationId || actual.Kind != JournalKind.ExtensionApply ||
                    actual.Phase != JournalPhase.Applied || actual.Revision != fresh.Registered.Revision ||
                    actual.PlanPath != fresh.PlanReference.RelativePath || actual.PlanDigest != fresh.PlanReference.Sha256 ||
                    !actual.ResourceKeys.SequenceEqual(fresh.Registered.ResourceKeys) ||
                    OperationRows.ReadExisting(connection, transaction, fresh.Identity) is not { Status: "accepted" }) throw Invalid();
                JournalEffectGate.Require(connection, transaction, journalId, EffectGateState.Entered);
                PayloadEffectCursor.RequireEffectsInside(connection, transaction, actual,
                    fresh.Plan, fresh.History, fresh.Physical);
                var settings = HostSettingsStore.CommitExtensionPreferenceInside(connection, transaction, journalId,
                    fresh.Source.ExtensionId, new(fresh.Source.SettingsRevision, fresh.Source.DesiredEnabled,
                        fresh.Source.InitializeEnabledPreference));
                if (settings != fresh.Commit!.SettingsAfterRevision) throw Invalid();
                ExtensionInstalledRows.InsertInside(connection, transaction, fresh.Source, fresh.Footprint!, fresh.Commit.Footprint,
                    ExtensionUpgradeSource.AllowsSameVersion(instance, contracts, roots, fresh.Source));
                new RecoveryJournalRegistry(database).AdvanceInside(connection, transaction,
                    actual with { Phase = JournalPhase.Committed, Revision = fresh.Latest.Revision }, actual.Revision);
                using var command = connection.CreateCommand(); command.Transaction = transaction;
                command.CommandText = "UPDATE operations SET status='completed' WHERE operation_id=$operation " +
                    "AND status='accepted' AND initial_journal_id=$journal;";
                command.Parameters.AddWithValue("$operation", actual.OperationId.ToString("D"));
                command.Parameters.AddWithValue("$journal", journalId.ToString("D"));
                if (command.ExecuteNonQuery() != 1) throw Invalid();
                return true;
            });
            _ = binding.Read(journalId, bound.PlanReference);
            return JournalRecoveryResult.Completed;
        });
    }

    private IDisposable HoldRecorded(BoundExtensionPayload bound)
    {
        var held = new List<IDisposable>();
        try
        {
            var references = new List<BlobReference> { bound.PlanReference, bound.Plan.ExtensionSource!,
                bound.CommitIntent!.Artifact, bound.Commit!.Footprint };
            references.AddRange(bound.Physical.Select(item => item.Intent.Artifact));
            if (bound.PreparationIntent is { } preparation) references.Add(preparation.Artifact);
            references.AddRange(PayloadSourceCodec.References(bound.Source));
            var blobs = new ImmutableBlobStore(instance, contracts);
            foreach (var reference in references.Distinct()) held.Add(blobs.OpenVerified(reference));
            foreach (var document in bound.History)
            {
                var path = PhysicalPathPolicy.NormalizeWithoutLinks(Path.Combine(instance.RuntimeRoot, "journals",
                    document.JournalId.ToString("D"), document.Revision.ToString("D10", CultureInfo.InvariantCulture) + ".json"));
                var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
                held.Add(file);
                if (file.Length != document.Export().Length ||
                    !SHA256.HashData(file).AsSpan().SequenceEqual(SHA256.HashData(document.Export()))) throw Invalid();
            }
            return new Held(held);
        }
        catch { for (var i = held.Count - 1; i >= 0; i--) held[i].Dispose(); throw; }
    }

    private static void RequireRecorded(BoundExtensionPayload bound)
    {
        if (bound.Registered.Kind != JournalKind.ExtensionApply || bound.Registered.Phase != JournalPhase.Applied ||
            bound.Latest.Phase != JournalPhase.Committed || bound.Latest.Revision != bound.Registered.Revision + 1 ||
            bound.Commit is null || bound.Footprint is null || bound.Physical.IsEmpty) throw Invalid();
    }

    private static IOException Invalid() => new("extension_recorded_installation_unverified");
    private sealed class Held(List<IDisposable> values) : IDisposable
    {
        private List<IDisposable>? _values = values;
        public void Dispose()
        {
            var current = Interlocked.Exchange(ref _values, null);
            if (current is not null) for (var i = current.Count - 1; i >= 0; i--) current[i].Dispose();
        }
    }
}
