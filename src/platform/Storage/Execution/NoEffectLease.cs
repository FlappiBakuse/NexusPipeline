using NexusPipeline.Core.Configuration;
using NexusPipeline.Extensibility.Protocol;
using NexusPipeline.Platform.Serialization;
using NexusPipeline.Platform.Storage.Journals;
using NexusPipeline.Platform.Storage.Sqlite;

namespace NexusPipeline.Platform.Storage.Execution;

internal sealed class NoEffectLease : IDisposable
{
    private readonly List<IDisposable> _held = [];

    internal static NoEffectLease Acquire(RuntimeInstance instance, ContractReader contracts, StateDatabase database, RunAttemptPlan plan,
        BlobReference reference, bool allowPreparedChildren = false)
    {
        var lease = new NoEffectLease();
        try
        {
            var blobs = new ImmutableBlobStore(instance, contracts);
            lease._held.Add(blobs.OpenVerified(reference));
            var totalReference = AttemptPlanCodec.Reference(plan.ExecutionPlan);
            lease._held.Add(blobs.OpenVerified(totalReference));
            var total = new ExecutionPlanCodec(instance, contracts, blobs).Read(totalReference).ReadWire();
            lease._held.Add(blobs.OpenVerified(new(total.DefinitionBundle.RelativePath, total.DefinitionBundle.SizeBytes, total.DefinitionBundle.Sha256)));
            PlanInputLease.Acquire(total, lease._held);
            var scanner = new FileTreeScanner(SnapshotBudget.Default);
            var children = new RecoveryJournalRegistry(database).ReadSnapshot(Guid.ParseExact(plan.JournalId, "D")).Journals
                .ToDictionary(item => item.JournalId);
            foreach (var item in plan.ConfigurationPlans)
            {
                var childReference = AttemptPlanCodec.Reference(item.Plan);
                lease._held.Add(blobs.OpenVerified(childReference));
                var codec = new ConfigurationPlanCodec(instance, contracts, blobs);
                var child = codec.Read(childReference);
                if (children.TryGetValue(child.JournalId, out var registered))
                {
                    if (registered.Kind != child.Kind || registered.OperationId != child.OperationId || registered.PlanPath != childReference.RelativePath ||
                        registered.PlanDigest != childReference.Sha256) throw new IOException("attempt_child_already_accepted");
                    var directory = Path.Combine(instance.RuntimeRoot, "journals", child.JournalId.ToString("D"));
                    if (allowPreparedChildren && registered.Phase == JournalPhase.Prepared && registered.Revision == 1)
                    {
                        new InitialConfigurationJournal(instance, contracts, database).VerifyPreparedPlan(registered);
                        if (Directory.Exists(directory))
                        {
                            var initial = new JournalStore(instance, contracts).Read(child.JournalId);
                            if (initial.Revision != 1 || initial.Phase != JournalPhase.Prepared || initial.Root.GetProperty("participants").GetArrayLength() != 0 ||
                                initial.Root.GetProperty("receipts").GetArrayLength() != 0) throw new IOException("attempt_child_already_accepted");
                        }
                    }
                    else
                    {
                        var physical = new JournalStore(instance, contracts).Read(child.JournalId);
                        Configuration.RunStopEvidence.RequireNoEffect(physical);
                        if (registered.Phase != physical.Phase || registered.Revision != physical.Revision) throw new IOException("attempt_child_already_accepted");
                    }
                }
                else RequireMissing(scanner, Path.Combine(instance.RuntimeRoot, "journals", child.JournalId.ToString("D")));
                RequireMissing(scanner, Path.Combine(Path.GetDirectoryName(child.TargetPath)!, ".nexus-" + child.JournalId.ToString("D")));
                lease._held.Add(TreeReadLease.Acquire(child.TargetPath, child.ParentIdentity, child.Before));
                foreach (var content in codec.References(child)) lease._held.Add(blobs.OpenVerified(content));
            }
            foreach (var log in plan.InitialLogs)
            {
                var objects = Array.AsReadOnly(log.OriginalObjects.Select(item => new PhysicalTreeObject(item.RelativePath, FileTreeProtocol.FromWire(item.Identity))).ToArray());
                var tree = FileTreeProtocol.FromWire(log.Before, objects);
                lease._held.Add(TreeReadLease.Acquire(log.Path, FileTreeProtocol.FromWire(log.ParentIdentity), tree));
                foreach (var content in tree.Contents) lease._held.Add(blobs.OpenVerified(content));
            }
            RequireMissing(scanner, Path.Combine(instance.RuntimeRoot, plan.OutputDirectory));
            RequireMissing(scanner, Path.Combine(instance.RuntimeRoot, plan.LogDirectory));
            return lease;
        }
        catch { lease.Dispose(); throw; }
    }

    private static void RequireMissing(FileTreeScanner scanner, string path)
    {
        if (scanner.Capture(path, default).Shape != FileObjectKind.Missing) throw new IOException("attempt_output_or_workspace_observed");
    }

    public void Dispose()
    {
        for (var index = _held.Count - 1; index >= 0; index--) _held[index].Dispose();
        _held.Clear();
    }
}
