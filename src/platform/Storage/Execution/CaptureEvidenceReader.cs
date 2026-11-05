using System.Collections.Immutable;
using System.Runtime.Versioning;
using NexusPipeline.Core.Configuration;
using NexusPipeline.Core.Execution;
using NexusPipeline.Extensibility.Protocol;
using NexusPipeline.Platform.Media;
using NexusPipeline.Platform.Processes;
using NexusPipeline.Platform.Serialization;
using NexusPipeline.Platform.Storage.Journals;
using NexusPipeline.Platform.Storage.Sqlite;
using NexusPipeline.Platform.Storage.Records;

namespace NexusPipeline.Platform.Storage.Execution;

internal sealed class CaptureEvidenceReader(RuntimeInstance instance, StateDatabase database, ContractReader contracts)
{
    internal ImmutableArray<CaptureSavedEvidence> ReadMetadata(RegisteredJournal journal, RunAttemptPlan attempt,
        ExecutionPlan total, IReadOnlyList<JournalDocument> history, AndroidControlEvidence? androidReader = null) =>
        new CaptureMetadataReader(instance, database, contracts).Read(journal, attempt, total, history, androidReader);

    internal IReadOnlyList<BlobReference> ReadReferences(RegisteredJournal journal, RunAttemptPlan attempt,
        ExecutionPlan total, IReadOnlyList<JournalDocument> history) =>
        ReadRetainedReferences(journal, attempt, total, history);

    private IReadOnlyList<BlobReference> ReadRetainedReferences(RegisteredJournal journal, RunAttemptPlan attempt,
        ExecutionPlan total, IReadOnlyList<JournalDocument> history)
    {
        var retirements = new VerifiedImageRetirement.ReadScope(instance, database, contracts, Guid.Parse(attempt.RunId));
        return ReadMetadata(journal, attempt, total, history).SelectMany(saved =>
        {
            if (saved.Result is null) return saved.References;
            var groups = CaptureFramePolicy.Images(saved.Result, saved.State.Binding.CaptureId)
                .GroupBy(frame => frame.Png).ToArray();
            var fullyRetired = groups.Where(group => group.All(frame => retirements.ForFrame(saved, frame) is not null))
                .Select(group => group.Key).ToHashSet();
            return saved.References.Where(reference => !fullyRetired.Contains(reference));
        }).Distinct().ToArray();
    }

    [SupportedOSPlatform("windows")]
    internal IDisposable HoldCollectedForPublication(RegisteredJournal journal, RunAttemptPlan attempt,
        ExecutionPlan total, IReadOnlyList<JournalDocument> history, ExecutionCaptureBinding binding, BlobReference result)
    {
        var saved = ReadMetadata(journal, attempt, total, history).SingleOrDefault(value =>
            value.State.Binding == binding && value.State.Result == result) ?? throw Invalid();
        RequireActuallyStopped(saved);
        var held = new List<IDisposable>();
        try
        {
            if (saved.Result?.Outcome == "captured")
            {
                new MuMuCaptureEvidence(new(instance, contracts)).RequireCapturedMapping(saved);
                foreach (var frame in CaptureFramePolicy.Images(saved.Result, binding.CaptureId))
                    held.Add(OpenPng(saved, frame.Png));
            }
            var blobs = new ImmutableBlobStore(instance, contracts);
            var pngs = saved.Result is null ? new HashSet<BlobReference>() :
                CaptureFramePolicy.Images(saved.Result, binding.CaptureId).Select(frame => frame.Png).ToHashSet();
            foreach (var reference in saved.References.Distinct().Where(reference => !pngs.Contains(reference)))
                held.Add(blobs.OpenVerified(reference));
            return new HeldPublication(held);
        }
        catch { for (var index = held.Count - 1; index >= 0; index--) held[index].Dispose(); throw; }
    }

    [SupportedOSPlatform("windows")]
    internal FileStream OpenCaptured(ExecutionCaptureBinding binding, BlobReference result, Guid? frameId = null)
    {
        _ = AttemptPreparedBinding.Read(instance, contracts, database, binding.Primary.ParentJournalId);
        var journal = new RecoveryJournalRegistry(database).ReadJournal(binding.Primary.ParentJournalId);
        var attempt = AttemptPreparedBinding.ReadPlan(instance, contracts, journal);
        var total = new ExecutionPlanCodec(instance, contracts, new(instance, contracts))
            .Read(AttemptPlanCodec.Reference(attempt.ExecutionPlan)).ReadWire();
        var history = new JournalStore(instance, contracts).ReadAllRevisions(journal.JournalId);
        var projected = history.Where(document => document.Revision <= journal.Revision).ToArray();
        var saved = ReadMetadata(journal, attempt, total, projected).SingleOrDefault(value =>
            value.State.Binding == binding && value.State.Result == result && value.Result?.Outcome == "captured") ?? throw Invalid();
        return OpenSavedCaptured(saved, frameId ?? binding.CaptureId);
    }

    [SupportedOSPlatform("windows")]
    internal FileStream OpenOriginalCaptured(ExecutionCaptureBinding binding, BlobReference result, Guid frameId,
        AttemptValidatedRead validatedRead)
    {
        var journal = new RecoveryJournalRegistry(database).ReadJournal(binding.Primary.ParentJournalId);
        var history = new JournalStore(instance, contracts).ReadAllRevisions(journal.JournalId);
        // Reuse this request's validated capture graph only after reading and comparing current journal bytes.
        validatedRead = validatedRead.WithCoherentFreshHistory(history);
        if (journal.Phase != JournalPhase.Committed || history[^1].Revision != journal.Revision ||
            history[^1].Phase != journal.Phase) throw Invalid();
        var saved = validatedRead.Captures.SingleOrDefault(value =>
            value.State.Binding == binding && value.State.Result == result && value.Result?.Outcome == "captured") ?? throw Invalid();
        return OpenSavedCaptured(saved, frameId);
    }

    [SupportedOSPlatform("windows")]
    private FileStream OpenSavedCaptured(CaptureSavedEvidence saved, Guid frameId)
    {
        var binding = saved.State.Binding;
        RequireActuallyStopped(saved);
        var frame = CaptureFramePolicy.Images(saved.Result!, binding.CaptureId).SingleOrDefault(value =>
            value.FrameId == frameId) ?? throw Invalid();
        new VerifiedImageRetirement.ReadScope(instance, database, contracts, binding.Primary.RunId)
            .ForFrame(saved, frame)?.ThrowRetired();
        new MuMuCaptureEvidence(new(instance, contracts)).RequireCapturedMapping(saved);
        return OpenPng(saved, frame.Png);
    }

    [SupportedOSPlatform("windows")]
    internal void RequireResolvedForRecovery(RegisteredJournal journal, RunAttemptPlan attempt,
        ExecutionPlan total, IReadOnlyList<JournalDocument> history, string? stepId = null)
    {
        var retirements = new VerifiedImageRetirement.ReadScope(instance, database, contracts, Guid.Parse(attempt.RunId));
        foreach (var saved in ReadMetadata(journal, attempt, total, history).Where(value =>
            stepId is null || value.State.Binding.Primary.StepId == stepId))
        {
            if (saved.Result is null) throw new IOException("execution_capture_collection_pending");
            RequireActuallyStopped(saved);
            if (saved.Result.Outcome == "captured")
            {
                new MuMuCaptureEvidence(new(instance, contracts)).RequireCapturedMapping(saved);
                foreach (var frame in CaptureFramePolicy.Images(saved.Result, saved.State.Binding.CaptureId))
                    if (retirements.ForFrame(saved, frame) is null) { using var png = OpenPng(saved, frame.Png); }
            }
        }
    }

    [SupportedOSPlatform("windows")]
    private static void RequireActuallyStopped(CaptureSavedEvidence saved)
    {
        if (saved.Result is null || saved.Parent is null) throw Invalid();
        foreach (var member in saved.Parent.Family)
            if (WriterStopEvidence.ObserveProcess(member) != WriterState.Stopped)
                throw new IOException("execution_capture_parent_stop_unconfirmed");
        if (saved.Result.HelperStopped is { } inner)
            foreach (var member in inner.AllFamily)
                if (WriterStopEvidence.ObserveProcess(member) != WriterState.Stopped)
                    throw new IOException("execution_capture_helper_stop_unconfirmed");
    }

    private FileStream OpenPng(CaptureSavedEvidence saved, BlobReference png)
    {
        if (saved.Result is not { Outcome: "captured" } result ||
            !CaptureFramePolicy.Images(result, saved.State.Binding.CaptureId).Any(frame => frame.Png == png)) throw Invalid();
        var input = new ImmutableBlobStore(instance, contracts).OpenVerified(png);
        try
        {
            if (saved.Source.Window is { } window) RgbCapturePng.Verify(input, window.Width, window.Height);
            else if (saved.Source.Android is not null && saved.Result.Image is { } expected)
            {
                var actual = BoundedCapturePng.Verify(input, allowRgba: true, allowAncillary: true);
                if (actual != expected) throw Invalid();
            }
            else throw Invalid();
            input.Position = 0; return input;
        }
        catch { input.Dispose(); throw; }
    }

    private sealed class HeldPublication(List<IDisposable> files) : IDisposable
    {
        private List<IDisposable>? _files = files;
        public void Dispose()
        {
            var actual = Interlocked.Exchange(ref _files, null);
            if (actual is null) return;
            for (var index = actual.Count - 1; index >= 0; index--) actual[index].Dispose();
        }
    }

    private static IOException Invalid() => CaptureMetadataReader.Invalid();
}
