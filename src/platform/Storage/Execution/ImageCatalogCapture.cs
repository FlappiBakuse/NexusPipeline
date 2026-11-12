using System.Security.Cryptography;
using NexusPipeline.Core.Configuration;
using NexusPipeline.Core.Execution;
using NexusPipeline.Core.Records;
using NexusPipeline.Platform.Serialization;
using NexusPipeline.Platform.Storage.Journals;
using NexusPipeline.Platform.Storage.Sqlite;

namespace NexusPipeline.Platform.Storage.Execution;

internal sealed class ImageCatalogCapture(RuntimeInstance instance, StateDatabase database, ContractReader contracts)
{
    internal ImageCatalogSnapshot Read(AttemptReceipt expected) => Read(expected, metadataOnly: false);
    internal ImageCatalogSnapshot ReadMetadata(AttemptReceipt expected) => Read(expected, metadataOnly: true);
    internal ImageCatalogSnapshot ReadValidated(AttemptReceipt expected, AttemptWriterState state) =>
        Read(expected, metadataOnly: false, state);

    private ImageCatalogSnapshot Read(AttemptReceipt expected, bool metadataOnly, AttemptWriterState? state = null)
    {
        if (database.InstanceId != instance.InstanceId || database.RuntimeRoot != instance.RuntimeRoot)
            throw ImageCatalogRows.Invalid();
        RegisteredJournal registered;
        FrozenStartSource source;
        using (var connection = DatabaseConnections.Open(database.DatabasePath, readOnly: true))
        using (var transaction = connection.BeginTransaction(deferred: true))
        {
            if (AttemptReceiptStore.ReadInside(connection, transaction, expected.Facts.AttemptId) != expected)
                throw ImageCatalogRows.Invalid();
            registered = RecoveryJournalRegistry.ReadJournalInside(connection, transaction, expected.JournalId);
            source = StartSourceRows.Read(connection, transaction, expected.OperationId);
            if (source.RunId != expected.Facts.RunId || source.JournalId != expected.JournalId ||
                registered.Phase != JournalPhase.Committed) throw ImageCatalogRows.Invalid();
            if (state is not null)
            {
                if (state.Journal.JournalId != registered.JournalId) throw ImageCatalogRows.Invalid();
                AttemptWriterState.RequireCursor(connection, transaction, state.Journal, state.Gate.State);
            }
            transaction.Commit();
        }
        var bound = metadataOnly || state is not null ? null : AttemptPreparedBinding.Read(instance, contracts, database, registered.JournalId);
        var metadata = metadataOnly ? new ImageMetadataRead(instance, database, contracts).Read(registered) : null;
        var plan = state?.Attempt ?? metadata?.Plan ?? bound!.Plan; var total = state?.Total ?? metadata?.Total ?? bound!.Total;
        if (!metadataOnly && !(state?.IsExecutionStart ?? bound!.IsExecutionStart) || plan.AttemptId != expected.Facts.AttemptId.ToString("D") ||
            plan.OperationId != expected.OperationId.ToString("D") ||
            plan.RunId != expected.Facts.RunId.ToString("D")) throw ImageCatalogRows.Invalid();
        StartSourceRows.RequireAttempt(source, plan);
        var validated = state?.RequireValidatedRead() ?? bound?.ValidatedRead;
        var history = metadata?.History ?? validated!.History;
        var terminal = history[^1];
        if (terminal.Revision != registered.Revision || terminal.Phase != registered.Phase ||
            AttemptPreparedBinding.ExpectedReceipt(plan, terminal) != expected) throw ImageCatalogRows.Invalid();
        var captures = metadata?.Captures ?? validated!.Captures;
        return FromMetadata(expected, terminal, source, captures) with { ValidatedRead = validated };
    }

    internal static ImageCatalogSnapshot FromMetadata(AttemptReceipt expected, JournalDocument terminal,
        FrozenStartSource source, IReadOnlyList<CaptureSavedEvidence> captures)
    {
        var entries = new List<ImageCatalogEntry>();
        var bindings = new Dictionary<Guid, ExecutionCaptureBinding>();
        foreach (var saved in captures)
        {
            var result = saved.Result ?? throw ImageCatalogRows.Invalid();
            var reference = saved.State.Result ?? throw ImageCatalogRows.Invalid();
            var frames = CaptureFramePolicy.Images(result, saved.State.Binding.CaptureId);
            IEnumerable<(Guid FrameId, BlobReference? Png)> images = frames.Count == 0
                ? [(saved.State.Binding.CaptureId, null)] : frames.Select(frame => (frame.FrameId, (BlobReference?)frame.Png));
            foreach (var frame in images)
            {
                var key = new RunImageKey(expected.Facts.RunId, expected.Facts.AttemptId,
                    saved.State.Binding.Primary.StepId, frame.FrameId);
                ImageCatalogPolicy.Validate(key);
                if (!bindings.TryAdd(frame.FrameId, saved.State.Binding)) throw ImageCatalogRows.Invalid();
                entries.Add(new(new(entries.Count, key, result.Outcome == "captured" ? RunImageOutcome.Captured : RunImageOutcome.Unavailable,
                    result.ReasonCode, saved.Source.Window?.Width ?? result.Image?.Width, saved.Source.Window?.Height ?? result.Image?.Height,
                    saved.State.Source.Sha256, saved.State.Root?.Sha256, reference.Sha256, frame.Png?.Sha256, frame.Png?.SizeBytes),
                    saved.State.Source, saved.State.Root, reference, frame.Png));
            }
        }
        if (entries.Count > 4096) throw ImageCatalogRows.Invalid();
        return new(expected, terminal.Revision, Convert.ToHexStringLower(SHA256.HashData(terminal.Export())), entries.AsReadOnly())
        { StartSource = source, Bindings = bindings };
    }
}
