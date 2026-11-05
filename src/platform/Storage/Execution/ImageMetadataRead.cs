using System.Collections.Immutable;
using NexusPipeline.Core.Configuration;
using NexusPipeline.Extensibility.Protocol;
using NexusPipeline.Platform.Serialization;
using NexusPipeline.Platform.Storage.Journals;
using NexusPipeline.Platform.Storage.Sqlite;

namespace NexusPipeline.Platform.Storage.Execution;

internal sealed record ExecutionImageMetadata(RunAttemptPlan Plan, ExecutionPlan Total,
    IReadOnlyList<JournalDocument> History, ImmutableArray<CaptureSavedEvidence> Captures);

internal sealed class ImageMetadataRead(RuntimeInstance instance, StateDatabase database, ContractReader contracts)
{
    internal ExecutionImageMetadata Read(RegisteredJournal journal)
    {
        if (journal.Kind != JournalKind.RunAttempt || journal.Phase != JournalPhase.Committed) throw Invalid();
        var metadata = AttemptPreparedBinding.ReadPlanMetadata(instance, contracts, journal);
        var attempt = metadata.Plan; var total = metadata.Total;
        var history = new JournalStore(instance, contracts).ReadAllRevisions(journal.JournalId);
        PreviewDomainCodec.RequireHistory(history, total, attempt.ExecutionPlan.Sha256);
        var latest = history[^1];
        if (latest.Revision != journal.Revision || latest.Phase != journal.Phase) throw Invalid();
        foreach (var document in history)
        {
            if (document.Kind != journal.Kind || document.InstanceId != instance.InstanceId ||
                document.JournalId != journal.JournalId || document.PlanPath != journal.PlanPath || document.PlanDigest != journal.PlanDigest ||
                document.Root.GetProperty("operationId").GetString() != attempt.OperationId ||
                document.Root.GetProperty("runId").GetString() != attempt.RunId ||
                document.Root.GetProperty("attemptId").GetString() != attempt.AttemptId ||
                !document.Root.GetProperty("resourceKeys").EnumerateArray().Select(item => item.GetString()!).SequenceEqual(attempt.ResourceKeys))
                throw Invalid();
            AttemptWriterState.RequireOwnedJobs(document.Root, total, attempt.ExecutionPlan.Sha256);
        }
        var gate = new JournalEffectGate(database).Read(journal.JournalId);
        if (gate.InstanceId != instance.InstanceId || gate.OperationId != journal.OperationId ||
            gate.Kind != journal.Kind || gate.PlanDigest != journal.PlanDigest) throw Invalid();
        JournalResourceClaims.VerifyAttempt(journal, attempt, total, JournalResourceClaims.Read(database, journal.JournalId));
        TargetRootFacts.Verify(instance, database, contracts, journal.JournalId, total, attempt.ExecutionPlan.Sha256, latest.Root, gate.State);
        AttemptWriterState.VerifySql(database, journal, latest, total, attempt.ExecutionPlan.Sha256, gate);
        _ = ExecutionCancellationRows.Verify(database, journal, attempt, total, latest.Root, latest.Root);
        var captures = new CaptureMetadataReader(instance, database, contracts).Read(journal, attempt, total, history);
        ExecutionCaptureCodec.RequireResolved(latest.Root, total, attempt.ExecutionPlan.Sha256);
        PreviewDomainCodec.RequireResolved(latest.Root, total, attempt.ExecutionPlan.Sha256);
        return new(attempt, total, history, captures);
    }

    private static IOException Invalid() => ImageCatalogRows.Invalid();
}
