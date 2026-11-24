using System.Collections.Immutable;
using System.Runtime.Versioning;
using NexusPipeline.Core.Configuration;
using NexusPipeline.Platform.Serialization;
using NexusPipeline.Platform.Storage.Journals;
using NexusPipeline.Platform.Storage.Sqlite;

namespace NexusPipeline.Platform.Storage.Configuration;

internal sealed record RunProgressItem(RunResourceStatus Resource, ConfigurationRevision Saved,
    FileTreeSnapshot? Candidate, string ReasonCode);
internal sealed record ConfigurationRunProgress(ConfigurationRunStatus Session, long ElapsedMilliseconds, bool WasExecuted,
    ImmutableArray<RunProgressItem> Items) : IDisposable
{
    internal PreparedConfigurationBatch? Prepared { get; init; }
    internal ConfigurationRevisionBatch? Batch => Items.Any(item => item.Candidate is not null) ? new(Items.Where(item => item.Candidate is not null)
        .Select(item => new ConfigurationRevisionChange(item.Resource.Address, item.Resource.CurrentRevision, Session.ExpectedBindingRevision,
            item.Resource.Contract, item.Candidate!, ConfigurationUpdateKind.Progress)).ToImmutableArray()) : null;
    public void Dispose() => Prepared?.Dispose();
}

[SupportedOSPlatform("windows")]
internal sealed class RunProgressCapture(RuntimeInstance instance, StateDatabase database, ContractReader contracts)
{
    internal ConfigurationRunProgress Read(ConfigurationRunStatus session, RunWriterProcess writer, bool sealedOutput)
    {
        using var history = new HistoryReadScope(instance, database, contracts);
        using var parents = JournalReadScope.HoldParents(instance, contracts, database.AttemptReads);
        writer.RequireScope(instance, session.SessionId);
        writer.RequireSampling(session, sealedOutput);
        var elapsed = writer.ElapsedSinceStart;
        if (!sealedOutput) return Read(session, elapsed, sealedOutput: false, wasExecuted: true);
        var stopped = new RunStopEvidence(instance, database, contracts).ReadClosed(session);
        if (elapsed.Ticks / TimeSpan.TicksPerMillisecond != stopped.Elapsed.Ticks / TimeSpan.TicksPerMillisecond)
            throw new IOException("configuration_run_stop_duration_unbound");
        return Read(session, stopped.Elapsed, sealedOutput: true, stopped.WasExecuted, stopped.Binding);
    }

    internal ConfigurationRunProgress ReadRestored(ConfigurationRunStatus session)
    {
        using var history = new HistoryReadScope(instance, database, contracts);
        using var parents = JournalReadScope.HoldParents(instance, contracts, database.AttemptReads);
        var stopped = new RunStopEvidence(instance, database, contracts).ReadRestored(session);
        return Read(session, stopped.Elapsed, sealedOutput: true, stopped.WasExecuted, stopped.Binding);
    }

    private ConfigurationRunProgress Read(ConfigurationRunStatus session, TimeSpan elapsed, bool sealedOutput, bool wasExecuted,
        BoundConfigurationRun? bound = null)
    {
        var binding = bound ?? ConfigurationRunBinding.Read(instance, database, contracts, session);
        var blobs = new ImmutableBlobStore(instance, contracts);
        var scanner = new FileTreeScanner(SnapshotBudget.Default);
        var archive = new FileTreeArchive(scanner, blobs);
        var swap = new ConfigurationSwap(scanner, archive);
        var revisions = new ConfigurationRevisionStore(instance, database, contracts);
        var result = ImmutableArray.CreateBuilder<RunProgressItem>();
        foreach (var resource in session.Resources)
        {
            var frozen = binding.Resources.Single(item => item.State.Source.Address == resource.Address);
            var saved = revisions.Read(resource.Address) ?? throw new IOException("configuration_run_current_missing");
            if (saved.Revision != resource.CurrentRevision || saved.Contract != resource.Contract || saved.UserId != frozen.State.Source.UserId ||
                saved.ScriptId != frozen.State.Source.ScriptId) throw new ConfigurationException("configuration_revision_conflict");
            var enabled = frozen.State.AutoSyncEnabled && (sealedOutput ? frozen.State.SyncAtFinish : frozen.State.SyncInitially);
            if (!enabled || !wasExecuted || !sealedOutput && elapsed < TimeSpan.FromSeconds(15))
            {
                result.Add(new(resource, saved, null, !enabled ? "configuration_sync_disabled" : "configuration_sync_not_due"));
                continue;
            }
            FileTreeSnapshot? candidate = null; string reason;
            try
            {
                var physical = new JournalStore(instance, contracts).Read(resource.JournalId);
                var registered = new RecoveryJournalRegistry(database).ReadJournal(resource.JournalId);
                if (physical.Revision != registered.Revision || physical.Phase != registered.Phase ||
                    !sealedOutput && (physical.Phase != JournalPhase.Applied || session.Phase != ConfigurationRunPhase.Running || session.IsWriterAdmissionClosed))
                    throw new IOException("configuration_run_sampling_closed");
                var child = ConfigurationJournalBinding.ReadForRestoration(instance, physical, contracts, database, verifyOutputContent: sealedOutput);
                var workspace = child.Prepared ?? throw new IOException("configuration_run_workspace_missing");
                if (sealedOutput && !ConfigurationJournalBinding.HasSealedOutput(physical))
                    throw new IOException("configuration_run_output_not_sealed");
                var raw = workspace.Plan.Candidate;
                using var backup = sealedOutput ? null : swap.HoldWritableWorkspace(workspace);
                using var scene = sealedOutput ? null : archive.CaptureProtected(frozen.Plan.TargetPath, frozen.Plan.ParentIdentity, default, out raw);
                ConfigurationOutputEvidence.Require(workspace, raw);
                var normalized = new ConfigurationContentValidation(blobs).RequireTransformed(resource.OperationId, resource.Address,
                    frozen.State.Source.Revision, frozen.Plan.EditPolicy!, raw.Snapshot,
                    tree => new ConfigurationTaskTransformation(blobs).Restore(resource.OperationId, frozen.State.Source, frozen.State.Tasks, tree));
                var trigger = sealedOutput ? ConfigurationSyncTrigger.Finalization : ConfigurationSyncTrigger.InitialDetection;
                var sync = ConfigurationSynchronizationPolicy.Evaluate(frozen.State.Source.Tree, saved.Tree, normalized, elapsed, true, trigger);
                reason = !sync.IsAllowed ? sync.ReasonCode :
                    sync.Delta!.Changes.IsEmpty ? "configuration_sync_unchanged" : "configuration_sync_allowed";
                if (reason == "configuration_sync_allowed") candidate = normalized;
            }
            catch (Exception error) when (error is IOException or ConfigurationException or System.Text.Json.JsonException)
            {
                reason = "configuration_sync_capture_rejected";
                new Records.RunDiagnosticStore(instance, database).Record(session.ParentJournalId, session.StepId,
                    frozen.State.Source.UserId, "configuration-sync", reason, error);
            }
            result.Add(new(resource, saved, candidate, reason));
        }
        var sample = new ConfigurationRunProgress(session, checked((long)elapsed.TotalMilliseconds), wasExecuted, result.ToImmutable());
        return sample.Batch is { } batch ? sample with { Prepared = new ConfigurationBatchStore(instance, database, contracts).Prepare(batch) } : sample;
    }

}
