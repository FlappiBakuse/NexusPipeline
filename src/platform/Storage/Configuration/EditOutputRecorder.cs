using System.Runtime.Versioning;
using System.Text.Json;
using System.Text.Json.Nodes;
using NexusPipeline.Core.Configuration;
using NexusPipeline.Core.Execution;
using NexusPipeline.Platform.Processes;
using NexusPipeline.Platform.Serialization;
using NexusPipeline.Platform.Storage.Journals;
using NexusPipeline.Platform.Storage.Sqlite;
using NexusPipeline.Platform.Storage.Execution;

namespace NexusPipeline.Platform.Storage.Configuration;

public enum ConfigurationOutputBoundary { ContentArchived, PlanFrozen, JournalFlushed, SqlProjected }
public interface IConfigurationOutputObserver { void Reached(ConfigurationOutputBoundary boundary, Guid journalId); }

[SupportedOSPlatform("windows")]
public sealed class EditOutputRecorder(RuntimeInstance instance, StateDatabase database, ContractReader contracts,
    InstanceLease lease, IConfigurationOutputObserver? observer = null)
{
    public FileTreeSnapshot Seal(Guid journalId, StoppedConfigurationEditor stopped)
    {
        lease.VerifyHeld(instance);
        if (stopped.Closure.InstanceId != instance.InstanceId || !stopped.Closure.JournalIds.Contains(journalId) ||
            stopped.Closure.Creator != ProcessIdentityReader.Read(Environment.ProcessId)) throw new IOException("configuration_editor_stop_proof_unbound");
        return SealCore(journalId, stopped.Closure, stopped.Reference);
    }

    internal void SealRecoveredRun(Guid journalId)
    {
        lease.VerifyHeld(instance);
        var session = RunRecoveryBinding.Read(instance, database, contracts, journalId);
        if (session is null) return;
        _ = ConfigurationRunBinding.Read(instance, database, contracts, session);
        var physical = new JournalStore(instance, contracts).Read(journalId);
        if (physical.Phase != JournalPhase.Applied || physical.Root.GetProperty("participants").GetArrayLength() != 1) return;
        var state = AttemptWriterState.Read(instance, database, contracts, session.ParentJournalId);
        if (!state.IsExecutionStart) return;
        try
        {
            StepWritersEvidence.RequireConfigurationStopped(instance, database, contracts, journalId);
        }
        catch (IOException error) when (error.Message is "execution_target_collection_pending" or "execution_provider_launcher_collection_pending" or
            "writer_job_closure_missing" or "execution_step_writer_stop_unconfirmed" or "execution_step_writer_owner_running")
        {
            // Unresolved native domains must be isolated without sampling their writable scene.
            return;
        }
        var closures = new JobClosureStore(instance, database, contracts);
        var actual = new List<FrozenJobClosure>();
        foreach (var job in state.Jobs.Where(job => job.StepId == session.StepId && job.Role != "judgement"))
        {
            var closure = closures.Read(journalId, job.WriterId, job.JobName, job.SessionId);
            if (closure is null) continue;
            if (WriterStopEvidence.ObserveProcess(closure.Creator) != WriterState.Stopped ||
                closure.Family.Any(member => WriterStopEvidence.ObserveProcess(member) != WriterState.Stopped))
                throw new IOException("configuration_writer_stop_unconfirmed");
            actual.Add(closure);
        }
        if (actual.Count == 0) return;
        var stopped = actual.LastOrDefault(item => item.WasExecuted) ?? actual[^1];
        _ = SealCore(journalId, stopped, closures.RequireStoredReference(journalId, stopped));
    }

    private FileTreeSnapshot SealCore(Guid journalId, FrozenJobClosure closure, BlobReference stopReference)
    {
        lease.VerifyHeld(instance);
        var closures = new JobClosureStore(instance, database, contracts);
        var parent = closures.ReadAttemptBinding(closure);
        StepWritersEvidence.RequireConfigurationStopped(instance, database, contracts, journalId, parent);
        closures.RequireReference(journalId, closure, stopReference, parent);
        var store = new JournalStore(instance, contracts); var registry = new RecoveryJournalRegistry(database);
        var physical = store.Read(journalId); var binding = ConfigurationJournalBinding.Read(instance, physical, contracts, database);
        var prepared = binding.Prepared ?? throw new IOException("configuration_edit_workspace_missing");
        var run = physical.Kind == JournalKind.ConfigExchange && prepared.Plan.RunState is not null && prepared.Plan.EditPolicy is not null &&
            !prepared.Plan.PhysicalClaims.IsDefaultOrEmpty;
        if (physical.Kind != JournalKind.ConfigEdit && !run || physical.Phase != JournalPhase.Applied ||
            !run && prepared.Plan.OperationId != closure.OperationId)
            throw new IOException("configuration_edit_output_phase_invalid");
        var writers = new JournalWriterStore(database).ObserveForCurrentExecutor(journalId);
        if (!writers.IsComplete || writers.State != WriterState.Stopped) throw new IOException("configuration_writer_stop_unconfirmed");
        if (binding.Document["participants"]!.AsArray().Count == 2) return prepared.Plan.Candidate.Snapshot;
        var observed = new ConfigurationWriterObservation(instance.InstanceId, journalId, true, WriterState.Stopped);
        var scanner = new FileTreeScanner(SnapshotBudget.Default); var blobs = new ImmutableBlobStore(instance, contracts);
        var archive = new FileTreeArchive(scanner, blobs); var swap = new ConfigurationSwap(scanner, archive);
        swap.VerifyEditableScene(prepared, observed);
        var output = archive.Capture(prepared.Plan.TargetPath, default);
        using var scene = TreeReadLease.Acquire(prepared.Plan.TargetPath, prepared.Plan.ParentIdentity, output);
        PhysicalTreeIdentity.RequireSingleLinks(prepared.Plan.TargetPath, output.Snapshot);
        observer?.Reached(ConfigurationOutputBoundary.ContentArchived, journalId);
        var codec = new EditOutputCodec(instance, contracts, blobs);
        var reference = codec.Write(prepared, physical.PlanDigest, output, stopReference, database, parent);
        using var plans = StableDirectoryLease.Acquire(Path.Combine(instance.RuntimeRoot, "blobs/plans"));
        using var plan = blobs.OpenVerified(reference); using var stop = blobs.OpenVerified(stopReference);
        observer?.Reached(ConfigurationOutputBoundary.PlanFrozen, journalId);
        lease.VerifyHeld(instance);
        var document = binding.Document; var previous = physical.Revision;
        document["participants"]!.AsArray().Add(new JsonObject
        {
            ["participantId"] = Guid.NewGuid().ToString("D"), ["kind"] = "file",
            ["resourceKey"] = document["participants"]![0]!["resourceKey"]!.GetValue<string>(),
            ["identity"] = JsonSerializer.SerializeToNode(codec.Reference(reference))
        });
        document["revision"] = previous + 1; document["updatedAt"] = DateTimeOffset.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'");
        document["receipts"]!.AsArray().Add(new JsonObject { ["receiptId"] = Guid.NewGuid().ToString("D"),
            ["recordedAt"] = document["updatedAt"]!.GetValue<string>(), ["stepId"] = "seal-output", ["effect"] = "completed", ["detailCode"] = "configuration_editor_output_frozen" });
        physical = store.Append(journalId, previous, BoundedJson.Serialize(document));
        observer?.Reached(ConfigurationOutputBoundary.JournalFlushed, journalId);
        var registered = registry.ReadJournal(journalId);
        registry.Advance(registered with { Revision = physical.Revision }, previous);
        observer?.Reached(ConfigurationOutputBoundary.SqlProjected, journalId);
        return output.Snapshot;
    }
}
