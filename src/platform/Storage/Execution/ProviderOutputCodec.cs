using System.Text.Json;
using NexusPipeline.Core.Configuration;
using NexusPipeline.Platform.Serialization;
using NexusPipeline.Platform.Storage.Sqlite;

namespace NexusPipeline.Platform.Storage.Execution;

internal sealed record ProviderOutputArchive(string Format, int Version, StepEvidenceBinding Binding,
    BlobReference ProviderControl, BlobReference Closure, ArchivedFileTree Tree);

internal static class ProviderOutputCodec
{
    private const string Format = "nexus.execution.provider-output";
    private const long MaximumBytes = 64L * 1024 * 1024;

    internal static BlobReference Capture(RuntimeInstance instance, StateDatabase database, ContractReader contracts,
        StepEvidenceBinding binding, BlobReference control, Action<BlobReference> hold, CancellationToken cancellation)
    {
        var bootstrap = new ProviderControlReader(instance, database, contracts).Bootstrap(binding, control);
        var grant = bootstrap.Workspace ?? throw Invalid();
        var closure = new StepEvidenceReader(instance, database, contracts).ClosureReference(binding);
        if (new JobClosureStore(instance, database, contracts).ReadForExecutionEvidence(binding.ParentJournalId,
            binding.WriterId, binding.JobName, binding.SessionId) is not { WasExecuted: true }) throw Invalid();
        using var root = StableDirectoryLease.Acquire(grant.Path);
        if (PhysicalObjectIdentity.Read(grant.Path) != new PhysicalObjectIdentity(grant.Identity.VolumeSerial, grant.Identity.FileId)) throw Invalid();
        var path = Path.Combine(grant.Path, "outputs");
        using var output = StableDirectoryLease.Acquire(path);
        if (PhysicalObjectIdentity.Read(path) != new PhysicalObjectIdentity(grant.OutputIdentity.VolumeSerial, grant.OutputIdentity.FileId)) throw Invalid();
        RequireTree(path, cancellation);
        var blobs = new ImmutableBlobStore(instance, contracts);
        var tree = new FileTreeArchive(new(new(256, MaximumBytes)), blobs).Capture(path, cancellation);
        var document = new ProviderOutputArchive(Format, 1, binding, control, closure, tree);
        Validate(instance, database, contracts, document, binding, control, closure, verifyContents: true);
        foreach (var reference in tree.Contents) hold(reference);
        var result = blobs.PersistPlan(BoundedJson.Serialize(document, StepResultCodec.Options)); hold(result); return result;
    }

    internal static BlobReference? Recover(RuntimeInstance instance, StateDatabase database, ContractReader contracts,
        StepEvidenceBinding binding, BlobReference control)
    {
        if (new ProviderControlReader(instance, database, contracts).Bootstrap(binding, control).Workspace is null) return null;
        var held = new List<IDisposable>();
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var blobs = new ImmutableBlobStore(instance, contracts);
            return Capture(instance, database, contracts, binding, control, reference => held.Add(blobs.OpenVerified(reference)), timeout.Token);
        }
        finally { foreach (var item in held) item.Dispose(); }
    }

    internal static IReadOnlyList<BlobReference> Read(RuntimeInstance instance, StateDatabase database, ContractReader contracts,
        StepEvidenceBinding binding, BlobReference reference, BlobReference control, BlobReference closure, bool verifyContents)
    {
        StepEvidenceCodec.Plan(reference);
        var blobs = new ImmutableBlobStore(instance, contracts);
        using var input = blobs.OpenVerified(reference); var bytes = new byte[checked((int)input.Length)]; input.ReadExactly(bytes);
        using var parsed = StrictJson.Parse(bytes);
        var document = parsed.RootElement.Deserialize<ProviderOutputArchive>(StepResultCodec.Options) ?? throw Invalid();
        if (!JsonElement.DeepEquals(parsed.RootElement, JsonSerializer.SerializeToElement(document, StepResultCodec.Options))) throw Invalid();
        Validate(instance, database, contracts, document, binding, control, closure, verifyContents);
        return new[] { reference }.Concat(document.Tree.Contents).ToArray();
    }

    private static void Validate(RuntimeInstance instance, StateDatabase database, ContractReader contracts,
        ProviderOutputArchive document, StepEvidenceBinding binding, BlobReference control, BlobReference closure, bool verifyContents)
    {
        if (document.Format != Format || document.Version != 1 || document.Binding != binding || document.ProviderControl != control ||
            document.Closure != closure || document.Tree is null || document.Tree.Snapshot.Shape != FileObjectKind.Directory ||
            document.Tree.Snapshot.Entries.Count > 256 || document.Tree.Snapshot.Entries.Sum(entry => entry.SizeBytes) > MaximumBytes) throw Invalid();
        FileTreeArchive.ValidateArchive(document.Tree);
        var bootstrap = new ProviderControlReader(instance, database, contracts).Bootstrap(binding, control);
        var grant = bootstrap.Workspace ?? throw Invalid();
        var expected = new PhysicalObjectIdentity(grant.OutputIdentity.VolumeSerial, grant.OutputIdentity.FileId);
        if (document.Tree.OriginalIdentity != expected || document.Tree.OriginalObjects.Count != document.Tree.Snapshot.Entries.Count ||
            document.Tree.OriginalObjects.Select(item => item.RelativePath).Distinct(StringComparer.Ordinal).Count() != document.Tree.OriginalObjects.Count ||
            document.Tree.OriginalObjects.Any(item => !document.Tree.Snapshot.Entries.Any(entry => entry.RelativePath == item.RelativePath) ||
                item.Identity is null || !Hex(item.Identity.VolumeSerial, 16) || !Hex(item.Identity.FileId, 32))) throw Invalid();
        if (closure != new StepEvidenceReader(instance, database, contracts).ClosureReference(binding) ||
            new JobClosureStore(instance, database, contracts).ReadForExecutionEvidence(binding.ParentJournalId,
                binding.WriterId, binding.JobName, binding.SessionId) is not { WasExecuted: true }) throw Invalid();
        if (!verifyContents) return;
        var blobs = new ImmutableBlobStore(instance, contracts);
        foreach (var content in document.Tree.Contents) using (blobs.OpenVerified(content)) { }
    }
    private static bool Hex(string value, int length) => value.Length == length && value.All(character => char.IsAsciiHexDigitLower(character));
    private static void RequireTree(string root, CancellationToken cancellation)
    {
        var pending = new Stack<string>(); pending.Push(PhysicalPathPolicy.NormalizeWithoutLinks(root));
        var count = 1; long bytes = 0;
        while (pending.Count > 0)
            foreach (var entry in Directory.EnumerateFileSystemEntries(pending.Pop()))
            {
                cancellation.ThrowIfCancellationRequested();
                if (++count > 256) throw Invalid();
                var path = PhysicalPathPolicy.NormalizeWithoutLinks(entry);
                if (Directory.Exists(path)) pending.Push(path);
                else
                {
                    PhysicalObjectIdentity.RequireSingleLink(path); bytes = checked(bytes + new FileInfo(path).Length);
                    if (bytes > MaximumBytes) throw Invalid();
                }
            }
    }
    private static IOException Invalid() => new("execution_provider_output_unbound");
}
