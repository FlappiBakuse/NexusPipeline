using System.IO.Enumeration;
using NexusPipeline.Extensibility.Protocol;
using NexusPipeline.Platform.Serialization;

namespace NexusPipeline.Platform.Storage.Execution;

internal static class FileLogEvidence
{
    internal static FileLogBaseline? Require(RuntimeInstance instance, ContractReader contracts, ImmutableBlobStore blobs,
        ExecutionPlan total, StepEvidenceBinding binding, BlobReference? expectedBaseline, RawOutputManifest raw)
    {
        var step = total.Steps.Single(item => item.StepId == binding.StepId);
        if (raw.FileLogBaseline != expectedBaseline || (expectedBaseline is not null) != (step.LogTargets.Count != 0)) throw Invalid();
        if (expectedBaseline is null) return null;
        var before = LogBaselineCodec.Read(blobs, expectedBaseline, binding.ParentJournalId, binding.StepId, binding.JobName, step.LogTargets);
        foreach (var checkpoint in before.Candidates.Select(candidate => candidate.Checkpoint).OfType<BlobReference>().Distinct()) blobs.Verify(checkpoint);
        var selectors = ExecutionLogSelectors.Read(instance, contracts, total, binding.StepId);
        var seen = new HashSet<(int, PhysicalObjectIdentity)>();
        foreach (var epoch in raw.FileSources)
        {
            if (epoch.DeclarationIndex >= selectors.Count) throw Invalid();
            var selector = selectors[epoch.DeclarationIndex]; var target = selector.Target;
            LogBaselineCodec.RequirePath(target.ScopePath, epoch.Path);
            var expanded = selector.Expand(epoch.SelectedAt);
            if (target.Selector == "file" && epoch.Path != expanded ||
                target.Selector == "latestFile" && Path.GetDirectoryName(epoch.Path) != target.ScopePath ||
                target.Selector == "latestMatchingFile" && (Path.GetDirectoryName(epoch.Path) != Path.GetDirectoryName(expanded) ||
                    !FileSystemName.MatchesSimpleExpression(Path.GetFileName(expanded), Path.GetFileName(epoch.Path), ignoreCase: true))) throw Invalid();
            var original = before.Candidates.FirstOrDefault(candidate => candidate.DeclarationIndex == epoch.DeclarationIndex && candidate.Identity == epoch.Identity);
            var first = seen.Add((epoch.DeclarationIndex, epoch.Identity));
            if (epoch.Reason == "created" && (!first || original is not null || epoch.StartOffset != 0) ||
                epoch.Reason == "baseline" && (!first || original is null || epoch.StartOffset != original.Length || epoch.ParentIdentity != original.ParentIdentity) ||
                epoch.Reason == "rewritten" && first) throw Invalid();
        }
        return before;
    }

    internal static IReadOnlyList<BlobReference> References(ImmutableBlobStore blobs, RawOutputManifest raw)
    {
        var result = raw.Parts.Select(part => part.Content).ToList();
        if (raw.FileLogBaseline is { } reference)
        {
            result.Add(reference);
            // The registered baseline owner validates its identity; discovery never opens checkpoint content under a GC DELETE guard.
        }
        return result;
    }

    private static IOException Invalid() => new("execution_file_log_evidence_unbound");
}
