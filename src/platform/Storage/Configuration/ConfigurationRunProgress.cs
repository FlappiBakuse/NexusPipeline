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
        writer.RequireScope(instance, session.SessionId);
        writer.RequireSampling(session, sealedOutput);
        var elapsed = writer.ElapsedSinceStart;
        if (!sealedOutput) return Read(session, elapsed, sealedOutput: false, wasExecuted: true);
        var stopped = new RunStopEvidence(instance, database, contracts).ReadClosed(session);
        if (elapsed.Ticks / TimeSpan.TicksPerMillisecond != stopped.Elapsed.Ticks / TimeSpan.TicksPerMillisecond)
            throw new IOException("configuration_run_stop_duration_unbound");
        return Read(session, stopped.Elapsed, sealedOutput: true, stopped.WasExecuted);
    }

    internal ConfigurationRunProgress ReadRestored(ConfigurationRunStatus session)
    {
        var stopped = new RunStopEvidence(instance, database, contracts).ReadRestored(session);
        return Read(session, stopped.Elapsed, sealedOutput: true, stopped.WasExecuted);
    }

    private ConfigurationRunProgress Read(ConfigurationRunStatus session, TimeSpan elapsed, bool sealedOutput, bool wasExecuted)
    {
        var binding = ConfigurationRunBinding.Read(instance, database, contracts, session);
        var blobs = new ImmutableBlobStore(instance, contracts);
        var archive = new FileTreeArchive(new(SnapshotBudget.Default), blobs);
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
                var workspace = ConfigurationJournalBinding.Read(instance, physical, contracts, database).Prepared ?? throw new IOException("configuration_run_workspace_missing");
                using var backup = sealedOutput ? null : new ConfigurationSwap(new(SnapshotBudget.Default), archive).HoldWritableWorkspace(workspace);
                var raw = sealedOutput ? Sealed(resource) : archive.Capture(frozen.Plan.TargetPath, default);
                using var scene = sealedOutput ? null : TreeReadLease.Acquire(frozen.Plan.TargetPath, frozen.Plan.ParentIdentity, raw);
                if (!sealedOutput) PhysicalTreeIdentity.RequireSingleLinks(frozen.Plan.TargetPath, raw.Snapshot);
                ConfigurationOutputEvidence.Require(workspace, raw);
                new ConfigurationContentValidation(blobs).Require(resource.OperationId, resource.Address, frozen.State.Source.Revision, frozen.Plan.EditPolicy!, raw.Snapshot);
                var normalized = new ConfigurationTaskTransformation(blobs).Restore(resource.OperationId, frozen.State.Source, frozen.State.Tasks, raw.Snapshot);
                new ConfigurationContentValidation(blobs).Require(resource.OperationId, resource.Address, frozen.State.Source.Revision, frozen.Plan.EditPolicy!, normalized);
                var trigger = sealedOutput ? ConfigurationSyncTrigger.Finalization : ConfigurationSyncTrigger.InitialDetection;
                var baseline = ConfigurationSynchronizationPolicy.Evaluate(frozen.State.Source.Tree, normalized, elapsed, true, trigger);
                var current = ConfigurationSynchronizationPolicy.Evaluate(saved.Tree, normalized, elapsed, true, trigger);
                reason = !baseline.IsAllowed ? baseline.ReasonCode : !current.IsAllowed ? current.ReasonCode :
                    FileTreeDelta.Compare(saved.Tree, normalized).Changes.IsEmpty ? "configuration_sync_unchanged" : "configuration_sync_allowed";
                if (reason == "configuration_sync_allowed") candidate = normalized;
            }
            catch (Exception error) when (error is IOException or ConfigurationException or System.Text.Json.JsonException)
            { reason = "configuration_sync_capture_rejected"; }
            result.Add(new(resource, saved, candidate, reason));
        }
        var sample = new ConfigurationRunProgress(session, checked((long)elapsed.TotalMilliseconds), wasExecuted, result.ToImmutable());
        return sample.Batch is { } batch ? sample with { Prepared = new ConfigurationBatchStore(instance, database, contracts).Prepare(batch) } : sample;
    }

    private ArchivedFileTree Sealed(RunResourceStatus resource)
    {
        var physical = new JournalStore(instance, contracts).Read(resource.JournalId);
        if (physical.Root.GetProperty("participants").GetArrayLength() != 2) throw new IOException("configuration_run_output_not_sealed");
        var binding = ConfigurationJournalBinding.Read(instance, physical, contracts, database);
        return (binding.Prepared ?? throw new IOException("configuration_run_output_not_sealed")).Plan.Candidate;
    }
}
