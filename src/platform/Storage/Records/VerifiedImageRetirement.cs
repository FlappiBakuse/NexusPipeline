using NexusPipeline.Core.Records;
using NexusPipeline.Platform.Serialization;
using NexusPipeline.Platform.Storage.Execution;
using NexusPipeline.Platform.Storage.Sqlite;

namespace NexusPipeline.Platform.Storage.Records;

internal sealed class VerifiedImageRetirement
{
    private VerifiedImageRetirement() { }

    internal static VerifiedImageRetirement? ForCapture(RuntimeInstance instance, StateDatabase database,
        ContractReader contracts, CaptureSavedEvidence saved)
    {
        if (saved.Result is not { Outcome: "captured", Png: not null } result || saved.State.Result is null) return null;
        return Read(instance, database, contracts, saved.State.Binding.Primary.RunId, result.Png,
            image => image.Binding == saved.State.Binding && image.Source == saved.State.Source &&
                image.Root == saved.State.Root && image.Result == saved.State.Result &&
                image.ParentClosure == result.ParentClosure, null);
    }

    internal static VerifiedImageRetirement? ForEntry(RuntimeInstance instance, StateDatabase database,
        ContractReader contracts, ExecutionCaptureBinding binding, ImageCatalogEntry entry)
    {
        if (entry.Png is null) return null;
        return Read(instance, database, contracts, binding.Primary.RunId, entry.Png,
            image => image.Binding == binding && image.ToEntry() == entry, null);
    }

    internal static VerifiedImageRetirement? ForAsset(RuntimeInstance instance, StateDatabase database,
        ContractReader contracts, Guid runId, BlobReference png) =>
        Read(instance, database, contracts, runId, png, _ => true, null);

    private static VerifiedImageRetirement? Read(RuntimeInstance instance, StateDatabase database,
        ContractReader contracts, Guid runId, BlobReference png, Func<RecordRetirementImage, bool> matches, ReadScope? scope)
    {
        IReadOnlyList<BlobReference> references;
        using (var connection = DatabaseConnections.Open(database.DatabasePath, readOnly: true))
        using (var transaction = connection.BeginTransaction(deferred: true))
        {
            references = RecordRetirementRows.ProofsFor(connection, transaction, runId, [png]);
            transaction.Commit();
        }
        if (references.Count == 0) return null;
        var family = scope?.Family() ?? new RetirementFamilyReader(instance, database, contracts).CaptureMetadata(runId);
        var covered = false;
        foreach (var reference in references)
        {
            var proof = scope?.Proof(family, reference) ?? VerifiedRecordRetirement.ReadProof(instance, database, contracts, family, reference);
            if (proof.ContentKind == RetirementContentKind.Images && proof.Contents.Contains(png)) covered = true;
        }
        if (!covered) return null;
        if (!family.Attempts.SelectMany(attempt => attempt.Images).Any(image => image.Png == png && matches(image)))
            throw RecordRetirementCodec.Invalid();
        return new();
    }

    internal sealed class ReadScope(RuntimeInstance instance, StateDatabase database, ContractReader contracts, Guid runId)
    {
        private RecordRetirementFamily? _family;
        private readonly Dictionary<BlobReference, RecordRetirementDocument> _proofs = [];

        internal VerifiedImageRetirement? ForCapture(CaptureSavedEvidence saved)
        {
            if (saved.State.Binding.Primary.RunId != runId) throw RecordRetirementCodec.Invalid();
            if (saved.Result is not { Outcome: "captured", Png: not null } result || saved.State.Result is null) return null;
            return Read(instance, database, contracts, runId, result.Png, image =>
                image.Binding == saved.State.Binding && image.Source == saved.State.Source &&
                image.Root == saved.State.Root && image.Result == saved.State.Result &&
                image.ParentClosure == result.ParentClosure, this);
        }

        internal VerifiedImageRetirement? ForFrame(CaptureSavedEvidence saved, CaptureFrameRecord frame)
        {
            if (saved.State.Binding.Primary.RunId != runId || saved.State.Result is null ||
                saved.Result is not { Outcome: "captured" } result ||
                !CaptureFramePolicy.Images(result, saved.State.Binding.CaptureId).Contains(frame))
                throw RecordRetirementCodec.Invalid();
            return Read(instance, database, contracts, runId, frame.Png, image =>
                image.Binding == saved.State.Binding && image.ToEntry().Item.Key.CaptureId == frame.FrameId &&
                image.Source == saved.State.Source && image.Root == saved.State.Root &&
                image.Result == saved.State.Result && image.ParentClosure == result.ParentClosure, this);
        }

        internal VerifiedImageRetirement? ForEntry(ExecutionCaptureBinding binding, ImageCatalogEntry entry)
        {
            if (binding.Primary.RunId != runId) throw RecordRetirementCodec.Invalid();
            return entry.Png is null ? null : Read(instance, database, contracts, runId, entry.Png,
                image => image.Binding == binding && image.ToEntry() == entry, this);
        }

        internal VerifiedImageRetirement? ForAsset(BlobReference png) =>
            Read(instance, database, contracts, runId, png, _ => true, this);

        internal RecordRetirementFamily Family() =>
            _family ??= new RetirementFamilyReader(instance, database, contracts).CaptureMetadata(runId);

        internal RecordRetirementDocument Proof(RecordRetirementFamily family, BlobReference reference)
        {
            if (_proofs.TryGetValue(reference, out var proof)) return proof;
            proof = VerifiedRecordRetirement.ReadProof(instance, database, contracts, family, reference);
            if (_proofs.Count == 32) _proofs.Clear();
            _proofs.Add(reference, proof); return proof;
        }
    }

    internal void ThrowRetired() => throw new RetiredContentException();
}
