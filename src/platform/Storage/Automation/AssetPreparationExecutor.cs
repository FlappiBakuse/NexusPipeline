using NexusPipeline.Core.Automation;
using NexusPipeline.Core.Configuration;
using NexusPipeline.Core.Maintenance;
using NexusPipeline.Core.Operations;
using NexusPipeline.Platform.Serialization;
using NexusPipeline.Platform.Storage.Journals;
using NexusPipeline.Platform.Storage.Sqlite;

namespace NexusPipeline.Platform.Storage.Automation;

public enum AssetPreparationPoint { Accepted, InitialJournal, EffectEntry, Applying, Workspace, StartedReceipt, StartedOutbox,
    ContentFlushed, CompletedReceipt, CompletedOutbox, Applied, CommittedJournal, Published }
public interface IAssetPreparationObserver { void Reached(AssetPreparationPoint point, Guid journalId, string? contentDigest); }

public sealed class AssetPreparationExecutor(RuntimeInstance instance, ContractReader contracts, StateDatabase database,
    RecoveryIndex index, IAssetPreparationObserver? observer = null)
{
    public AutomationImportResult Execute(OperationIdentity operation, PreparedAssetPreparation prepared, AssetPreparationSources sources,
        InstanceLease instanceLease, ResourceOperationLease resourceLease, CancellationToken cancellationToken = default)
    {
        instanceLease.VerifyHeld(instance);
        cancellationToken.ThrowIfCancellationRequested();
        var plan = prepared.Plan.ReadWire();
        if (!resourceLease.IsActive || resourceLease.OperationId != operation.OperationId ||
            !plan.ResourceKeys.All(key => resourceLease.Claims.Any(claim => claim.ResourceKey == key && claim.Mode == ResourceClaimMode.Mutation)))
            throw new IOException("asset_preparation_resource_lease_required");
        sources.VerifyBound(prepared.Plan);
        var id = Guid.Parse(plan.JournalId);
        index.BlockUnknown("asset_preparation_accepting");
        AssetAcceptanceResult accepted;
        try { accepted = new AssetPreparationAcceptance(instance, contracts, database).Accept(operation, prepared, DateTimeOffset.UtcNow); }
        catch
        {
            try { PublishIndex(); }
            catch (Exception error) when (error is IOException or InvalidOperationException or Microsoft.Data.Sqlite.SqliteException)
                { index.BlockUnknown("asset_preparation_admission_recheck_failed"); }
            throw;
        }
        if (!accepted.Result.IsNew) { PublishIndex(); return accepted.Result; }
        At(AssetPreparationPoint.Accepted, id);
        var journal = new AssetPreparationJournal(instance, contracts, database, prepared.Plan, prepared.Reference);
        journal.Materialize();
        At(AssetPreparationPoint.InitialJournal, id);
        PublishIndex();
        var gate = new Sqlite.JournalEffectGate(database);
        gate.Enter(journal.Registered(), gate.Read(id).Revision);
        At(AssetPreparationPoint.EffectEntry, id);
        journal.Append(JournalPhase.Applying);
        journal.ProjectNonterminal();
        At(AssetPreparationPoint.Applying, id);
        var evidence = new AssetEvidenceStore(instance, contracts, database);
        var workspace = evidence.CreateWorkspace(prepared.Plan, prepared.Reference.Sha256);
        journal.Append(JournalPhase.Applying, workspace);
        journal.ProjectNonterminal();
        At(AssetPreparationPoint.Workspace, id);
        PublishIndex();
        var held = new List<IDisposable>();
        try
        {
            PrepareContents(prepared, sources, workspace, held, cancellationToken);
            using var inspection = new AssetPreparationInspection(instance, contracts, database, prepared.Plan, prepared.Reference, workspace);
            if (!inspection.AllContentReady) throw new IOException("asset_preparation_not_complete");
            new AssetPreparationPublication(instance, contracts, database).VerifyDefinitions(prepared.Plan);
            journal.Append(JournalPhase.Applied);
            journal.ProjectNonterminal();
            At(AssetPreparationPoint.Applied, id);
            var committed = journal.Append(JournalPhase.Committed);
            At(AssetPreparationPoint.CommittedJournal, id);
            var result = new AssetPreparationPublication(instance, contracts, database).Close(prepared.Plan, prepared.Reference, committed, false);
            At(AssetPreparationPoint.Published, id);
            PublishIndex();
            return result with { IsNew = true };
        }
        finally { for (var position = held.Count - 1; position >= 0; position--) held[position].Dispose(); }
    }

    private void PrepareContents(PreparedAssetPreparation prepared, AssetPreparationSources sources,
        NexusPipeline.Platform.Serialization.Protocol.AssetPreparationWorkspace workspace, List<IDisposable> held, CancellationToken cancellationToken)
    {
        var plan = prepared.Plan.ReadWire();
        var id = Guid.Parse(plan.JournalId);
        var evidence = new AssetEvidenceStore(instance, contracts, database);
        var codec = new AssetEvidenceCodec(instance, contracts);
        var writer = JournalWriterStore.CurrentWriter.WriterId;
        var outbox = new FileEffectOutbox(database);
        foreach (var asset in plan.Assets.Where(value => !value.WasPresent).DistinctBy(value => value.Content.Sha256))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var path = PhysicalPathPolicy.NormalizeWithoutLinks(Path.Combine(instance.RuntimeRoot, asset.Content.RelativePath));
            held.Add(new PhysicalDirectoryLease(Path.GetDirectoryName(path)!, AssetEvidenceCodec.Physical(asset.ParentIdentity)));
            var started = evidence.Write(prepared.Plan, prepared.Reference.Sha256, workspace,
                codec.Receipt(plan, prepared.Reference.Sha256, asset, writer, null));
            At(AssetPreparationPoint.StartedReceipt, id, asset.Content.Sha256);
            var step = AssetPreparationAcceptance.Step(asset.Content.Sha256);
            outbox.Record(id, step, FileEffectState.Pending, FileEffectState.Started, started.Sha256);
            At(AssetPreparationPoint.StartedOutbox, id, asset.Content.Sha256);
            var identity = sources.CopyNew(asset, path, cancellationToken);
            var file = new ImmutableBlobStore(instance, contracts).OpenVerified(AssetPlanCodec.Reference(asset.Content));
            held.Add(file);
            if (PhysicalObjectIdentity.Read(file.SafeFileHandle) != identity) throw new IOException("asset_preparation_content_replaced");
            At(AssetPreparationPoint.ContentFlushed, id, asset.Content.Sha256);
            var completed = evidence.Write(prepared.Plan, prepared.Reference.Sha256, workspace,
                codec.Receipt(plan, prepared.Reference.Sha256, asset, writer, identity));
            At(AssetPreparationPoint.CompletedReceipt, id, asset.Content.Sha256);
            outbox.Record(id, step, FileEffectState.Started, FileEffectState.Completed, completed.Sha256);
            At(AssetPreparationPoint.CompletedOutbox, id, asset.Content.Sha256);
        }
    }

    private void At(AssetPreparationPoint point, Guid id, string? digest = null) => observer?.Reached(point, id, digest);

    private void PublishIndex()
    {
        var evidence = new JournalRecoveryEvidence(instance, new(instance, contracts), database);
        new RecoveryCoordinator(new RecoveryJournalRegistry(database), evidence, index).VerifyAndPublish();
    }
}
