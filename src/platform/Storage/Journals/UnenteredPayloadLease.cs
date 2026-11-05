using NexusPipeline.Core.Configuration;
using NexusPipeline.Platform.Storage.Extensions;

namespace NexusPipeline.Platform.Storage.Journals;

internal sealed class UnenteredPayloadLease : IDisposable
{
    private readonly List<IDisposable> _held = [];

    internal static UnenteredPayloadLease Acquire(RuntimeInstance instance, FrozenPayloadApply plan, ImmutableBlobStore blobs, BlobReference planReference)
    {
        var lease = new UnenteredPayloadLease();
        try
        {
            if (instance.InstanceId != plan.InstanceId || blobs.Instance.InstanceId != instance.InstanceId)
                throw new IOException("payload_no_effect_instance_unbound");
            var parent = Path.GetDirectoryName(plan.TargetPath)!;
            lease._held.Add(plan.Kind == JournalKind.UpdateApply
                ? TreeReadLease.AcquireApplication(plan.TargetPath, plan.ParentIdentity, plan.Before)
                : TreeReadLease.Acquire(plan.TargetPath, plan.ParentIdentity, plan.Before));
            foreach (var blob in plan.Before.Contents.Concat(plan.Candidate.Contents).Distinct()) lease._held.Add(blobs.OpenVerified(blob));
            lease._held.Add(blobs.OpenVerified(planReference));
            if (plan.ExtensionSource is { } source) lease._held.Add(blobs.OpenVerified(source));
            var scanner = new FileTreeScanner(SnapshotBudget.Default);
            var adjacent = plan.Kind == JournalKind.ExtensionApply ? ExtensionWorkspacePath.For(plan) :
                Path.Combine(parent, ".nexus-payload-" + plan.JournalId.ToString("D"));
            if (scanner.Capture(adjacent, default).Shape != FileObjectKind.Missing) throw new IOException("payload_adjacent_workspace_unknown");
            return lease;
        }
        catch { lease.Dispose(); throw; }
    }

    public void Dispose()
    {
        for (var index = _held.Count - 1; index >= 0; index--) _held[index].Dispose();
        _held.Clear();
    }
}
