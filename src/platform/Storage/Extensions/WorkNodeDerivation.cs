using System.Collections.Immutable;
using NexusPipeline.Core.Configuration;
using NexusPipeline.Platform.Storage.Journals;

namespace NexusPipeline.Platform.Storage.Extensions;

internal static class WorkNodeDerivation
{
    internal const int MaximumNodes = 4097;
    internal static ImmutableArray<ExtensionWorkNode> Derive(BoundExtensionPayload producer, string layout)
    {
        if (producer.Registered.Phase != JournalPhase.Committed || producer.Latest.Phase != JournalPhase.Committed ||
            producer.Registered.Revision != producer.Latest.Revision || producer.Physical.IsEmpty ||
            producer.Physical[^1].Record.WorkspaceIdentity is not { } workspace) throw Invalid();
        ArchivedFileTree? tree;
        if (producer.Commit is not null)
        {
            var expected = producer.Plan.Before.Snapshot.Shape == FileObjectKind.Directory ? "before" : "empty";
            if (layout != expected) throw Invalid();
            tree = layout == "before" ? producer.Plan.Before : null;
        }
        else
        {
            if (producer.Restoration.IsEmpty || producer.Restoration[^1].Record.Stage != "committed") throw Invalid();
            var objects = producer.Physical[^1].Record.CandidateObjects;
            if (objects.IsEmpty)
            {
                if (layout != "empty") throw Invalid();
                tree = null;
            }
            else
            {
                if (layout is not ("candidate" or "spent") ||
                    producer.Restoration[0].Record.RetireCandidate && layout != "spent") throw Invalid();
                tree = producer.Plan.Candidate with { OriginalObjects = objects };
            }
        }
        var result = new List<ExtensionWorkNode>
        { new(0, "", FileObjectKind.Directory, 0, null, producer.Plan.ParentIdentity, workspace) };
        if (tree is not null)
        {
            if (tree.Snapshot.Shape != FileObjectKind.Directory || tree.Snapshot.Entries.Count > 4096) throw Invalid();
            var identities = tree.OriginalObjects.ToDictionary(item => item.RelativePath, item => item.Identity, StringComparer.Ordinal);
            foreach (var entry in tree.Snapshot.Entries)
            {
                var relative = entry.RelativePath.Length == 0 ? layout : layout + "/" + entry.RelativePath;
                PhysicalPathPolicy.ValidateRelativePath(relative);
                var separator = entry.RelativePath.LastIndexOf('/');
                var parentRelative = separator >= 0 ? entry.RelativePath[..separator] : "";
                var parent = entry.RelativePath.Length == 0 ? workspace : identities[parentRelative];
                if (entry.Kind is not (FileObjectKind.File or FileObjectKind.Directory) ||
                    !identities.TryGetValue(entry.RelativePath, out var identity)) throw Invalid();
                result.Add(new(0, relative, entry.Kind, entry.SizeBytes, entry.Sha256, parent, identity));
            }
        }
        if (result.Count > MaximumNodes || result.Select(item => item.Identity).Distinct().Count() != result.Count) throw Invalid();
        return result.OrderByDescending(item => item.RelativePath.Length == 0 ? 0 : 1 + item.RelativePath.Count(c => c == '/'))
            .ThenBy(item => item.RelativePath, StringComparer.Ordinal)
            .Select((item, index) => item with { Index = index }).ToImmutableArray();
    }

    internal static string Resolve(ExtensionWorkProof proof, ExtensionWorkNode node)
    {
        var path = PhysicalPathPolicy.NormalizeWithoutLinks(Path.GetFullPath(Path.Combine(proof.WorkspacePath, node.RelativePath)));
        if (Path.GetRelativePath(proof.WorkspacePath, path).Replace('\\', '/') !=
            (node.RelativePath.Length == 0 ? "." : node.RelativePath)) throw Invalid();
        return path;
    }

    private static IOException Invalid() => new("extension_work_origin_unbound");
}
