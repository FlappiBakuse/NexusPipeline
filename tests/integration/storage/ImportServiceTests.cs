using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using NexusPipeline.Core.Automation;
using NexusPipeline.Core.Configuration;
using NexusPipeline.Core.Maintenance;
using NexusPipeline.Core.Operations;
using NexusPipeline.Platform.Storage;
using NexusPipeline.Platform.Storage.Automation;
using NexusPipeline.Platform.Storage.Journals;
using NexusPipeline.Platform.Storage.Sqlite;
using Xunit;

namespace NexusPipeline.StorageTests;

public sealed class ImportServiceTests
{
    [Fact(DisplayName = "A Prepared Export Leases Its Real Attachments And Imports Their Exact Bytes Into Another New Instance")]
    public void LeasesExportAttachments()
    {
        using var source = Create();
        var attachment = Attachment(source);
        Import(source, Encode(source, attachment.Asset), attachment, 0);
        var export = source.Service.PrepareExport(DateTimeOffset.UtcNow);
        try
        {
            var item = Assert.Single(export.Attachments);
            var sourcePath = Path.Combine(source.Instance.RuntimeRoot, "blobs", "content", item.Sha256 + ".bin");
            Assert.Throws<IOException>(() => { using var writable = new FileStream(sourcePath, FileMode.Open, FileAccess.Write, FileShare.ReadWrite); });
            var changed = export.ExportBundle();
            changed[0] = 0;
            var input = export.ExportBundle();
            using var target = Create();
            var incoming = Path.Combine(target.Root, "incoming.bin");
            using (var output = new FileStream(incoming, FileMode.CreateNew, FileAccess.Write, FileShare.None)) export.CopyAttachmentTo(item.Id, output);
            var operation = Guid.NewGuid();
            var roots = source.Roots.Keys.ToDictionary(value => value, _ => target.Roots.Values.Single());
            var preview = target.Service.Preview(operation, input, roots, 0);
            var result = target.Service.Apply(operation, "fixture", input, roots, 0, preview.PreviewDigest, new Dictionary<Guid, string> { [item.Id] = incoming });
            Assert.Equal(AutomationImportState.Published, result.State);
            var targetPath = Path.Combine(target.Instance.RuntimeRoot, "blobs", "content", item.Sha256 + ".bin");
            Assert.Equal(File.ReadAllBytes(sourcePath), File.ReadAllBytes(targetPath));
        }
        finally { export.Dispose(); }
        Assert.Throws<ObjectDisposedException>(() => export.ExportBundle());
        var original = Path.Combine(source.Instance.RuntimeRoot, "blobs", "content", attachment.Asset.Sha256 + ".bin");
        using var released = new FileStream(original, FileMode.Open, FileAccess.Write, FileShare.ReadWrite);
    }

    [Fact(DisplayName = "Definition Exports Disambiguate Shared Package Paths And Retain Every Distinct Content Object")]
    public void PreservesDistinctAssets()
    {
        using var fixture = Create();
        var first = Attachment(fixture);
        var firstInput = new AutomationBundleCodec(new()).Encode(fixture.Definitions, DateTimeOffset.UtcNow, fixture.Definitions.Scripts[0].Id, [first.Asset]);
        Import(fixture, firstInput, first, 0);
        var second = Attachment(fixture);
        File.WriteAllBytes(second.Path, [7, 11, 13]);
        second = (second.Asset with { Sha256 = Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(second.Path))) }, second.Path);
        var secondInput = new AutomationBundleCodec(new()).Encode(fixture.Definitions, DateTimeOffset.UtcNow, fixture.Definitions.Scripts[0].Id, [second.Asset]);
        Import(fixture, secondInput, second, 1);
        using var export = fixture.Service.PrepareExport(DateTimeOffset.UtcNow);
        Assert.Equal(2, export.Attachments.Count);
        Assert.Equal(2, export.Attachments.Select(value => value.PackagePath).Distinct(StringComparer.OrdinalIgnoreCase).Count());
        var catalog = new AutomationDefinitionStore(fixture.Database).Read();
        var decoded = new AutomationBundleCodec(new()).Decode(export.ExportBundle(), catalog.Roots.ToDictionary(value => value.Id, value => value.Path), 0);
        Assert.Equal(2, decoded.Attachments.Length);
        foreach (var item in export.Attachments)
        {
            using var output = new MemoryStream();
            export.CopyAttachmentTo(item.Id, output);
            Assert.Equal(item.Sha256, Convert.ToHexStringLower(SHA256.HashData(output.ToArray())));
        }
        var missing = export.Attachments[^1];
        var missingPath = Path.Combine(fixture.Instance.RuntimeRoot, "blobs", "content", missing.Sha256 + ".bin");
        var remainingPath = Path.Combine(fixture.Instance.RuntimeRoot, "blobs", "content", export.Attachments[0].Sha256 + ".bin");
        export.Dispose();
        File.Move(missingPath, Path.Combine(fixture.Root, "preserved-missing.bin"));
        Assert.Throws<FileNotFoundException>(() => fixture.Service.PrepareExport(DateTimeOffset.UtcNow));
        using var released = new FileStream(remainingPath, FileMode.Open, FileAccess.Write, FileShare.ReadWrite);
        Assert.Equal(2, new AutomationDefinitionStore(fixture.Database).Read().Scripts.Length);
    }

    [Fact(DisplayName = "A Generic Template Export Includes Only Its Selected Scripts Owned Attachments")]
    public void ScopesTemplateAttachments()
    {
        using var fixture = Create();
        var attachment = Attachment(fixture);
        var input = new AutomationBundleCodec(new()).Encode(fixture.Definitions, DateTimeOffset.UtcNow, fixture.Definitions.Scripts[0].Id, [attachment.Asset]);
        Import(fixture, input, attachment, 0);
        var metadata = new AutomationBundleCodec(new()).Encode(fixture.Definitions, DateTimeOffset.UtcNow, fixture.Definitions.Scripts[0].Id);
        var operation = Guid.NewGuid();
        var preview = fixture.Service.Preview(operation, metadata, fixture.Roots, 1);
        fixture.Service.Apply(operation, "fixture", metadata, fixture.Roots, 1, preview.PreviewDigest, new Dictionary<Guid, string>());
        var catalog = new AutomationDefinitionStore(fixture.Database).Read();
        var withoutAttachments = catalog.Scripts.Single(value => value.Id == preview.Bundle.Definitions.Scripts[0].Id);
        using var export = fixture.Service.PrepareExport(DateTimeOffset.UtcNow, withoutAttachments.Id);
        Assert.Empty(export.Attachments);
        using var document = JsonDocument.Parse(export.ExportBundle());
        Assert.Equal("scriptTemplate", document.RootElement.GetProperty("exportKind").GetString());
        Assert.Equal(1, document.RootElement.GetProperty("scripts").GetArrayLength());
    }

    [Fact(DisplayName = "Accepted Metadata Import Replays Its Original Revision Before Inspecting A Missing Source Directory")]
    public void ReplaysMetadataImport()
    {
        using var fixture = Create();
        var input = Encode(fixture);
        var operation = Guid.NewGuid();
        var preview = fixture.Service.Preview(operation, input, fixture.Roots, 0);
        var result = fixture.Service.Apply(operation, "fixture", input, fixture.Roots, 0, preview.PreviewDigest, new Dictionary<Guid, string>());
        Assert.True(result.IsNew);
        Assert.Null(result.JournalId);
        Assert.Equal(1, result.CatalogRevision);
        var store = new AutomationDefinitionStore(fixture.Database);
        var catalog = store.Read();
        var edit = new AutomationChanges(catalog.Revision, [], [catalog.Scripts[0] with { Name = "Edited later" }], [], [], [], [], []);
        store.Commit(AutomationDefinitionStore.Identity(Guid.NewGuid(), "fixture", edit), edit, DateTimeOffset.UtcNow);
        Directory.Move(fixture.Roots.Values.Single(), Path.Combine(fixture.Root, "moved-source"));
        var replay = fixture.Service.Apply(operation, "fixture", input, fixture.Roots, 0, preview.PreviewDigest, new Dictionary<Guid, string>());
        Assert.Equal(result with { IsNew = false }, replay);
        Assert.Equal(2, store.Read().Revision);
        Assert.Equal("Edited later", store.Read().Scripts[0].Name);
        Assert.Throws<OperationConflictException>(() => fixture.Service.Apply(operation, "another-subject", input, fixture.Roots, 0, preview.PreviewDigest, new Dictionary<Guid, string>()));
    }

    [Fact(DisplayName = "An Accepted Attachment Import Does Not Reopen Or Recopy Its Removed Original Attachment On Replay")]
    public void ReplaysRemovedSources()
    {
        using var fixture = Create();
        var attachment = Attachment(fixture);
        var input = Encode(fixture, attachment.Asset);
        var operation = Guid.NewGuid();
        var preview = fixture.Service.Preview(operation, input, fixture.Roots, 0);
        var selected = new Dictionary<Guid, string> { [attachment.Asset.Id] = attachment.Path };
        var result = fixture.Service.Apply(operation, "fixture", input, fixture.Roots, 0, preview.PreviewDigest, selected);
        Assert.Equal(AutomationImportState.Published, result.State);
        Assert.NotNull(result.JournalId);
        var path = Path.Combine(fixture.Instance.RuntimeRoot, "blobs", "content", attachment.Asset.Sha256 + ".bin");
        var identity = PhysicalObjectIdentity.Read(path);
        File.Delete(attachment.Path);
        var replay = fixture.Service.Apply(operation, "fixture", input, fixture.Roots, 0, preview.PreviewDigest, selected);
        Assert.Equal(result with { IsNew = false }, replay);
        Assert.Equal(identity, PhysicalObjectIdentity.Read(path));
        Assert.Single(new RecoveryJournalRegistry(fixture.Database).ReadSnapshot().Journals);
    }

    [Fact(DisplayName = "Template Requests Remap Source Attachment Ids And Create Independent Scripts While Sharing Verified Content")]
    public void RemapsAttachmentOwners()
    {
        using var fixture = Create();
        var attachment = Attachment(fixture);
        var input = new AutomationBundleCodec(new()).Encode(fixture.Definitions, DateTimeOffset.UtcNow,
            fixture.Definitions.Scripts[0].Id, [attachment.Asset]);
        var selected = new Dictionary<Guid, string> { [attachment.Asset.Id] = attachment.Path };
        var firstOperation = Guid.NewGuid();
        var first = fixture.Service.Preview(firstOperation, input, fixture.Roots, 0);
        var firstResult = fixture.Service.Apply(firstOperation, "fixture", input, fixture.Roots, 0, first.PreviewDigest, selected);
        var secondOperation = Guid.NewGuid();
        var second = fixture.Service.Preview(secondOperation, input, fixture.Roots, 1);
        var secondResult = fixture.Service.Apply(secondOperation, "fixture", input, fixture.Roots, 1, second.PreviewDigest, selected);
        var catalog = new AutomationDefinitionStore(fixture.Database).Read();
        Assert.Equal(2, catalog.Scripts.Select(value => value.Id).Distinct().Count());
        Assert.Equal(2, catalog.Roots.Select(value => value.Id).Distinct().Count());
        Assert.Empty(catalog.Users);
        Assert.NotEqual(firstResult.JournalId, secondResult.JournalId);
        Assert.Single(Directory.GetFiles(Path.Combine(fixture.Instance.RuntimeRoot, "blobs", "content"), "*.bin"));
        Assert.Equal(firstResult with { IsNew = false }, fixture.Service.Apply(firstOperation, "fixture", input, fixture.Roots, 0, first.PreviewDigest, selected));
    }

    [Fact(DisplayName = "A Changed Physical Root Cannot Apply An Earlier Preview Or Consume Its Operation Identity")]
    public void RejectsChangedRoots()
    {
        using var fixture = Create();
        var input = Encode(fixture);
        var operation = Guid.NewGuid();
        var preview = fixture.Service.Preview(operation, input, fixture.Roots, 0);
        var path = fixture.Roots.Values.Single();
        Directory.Move(path, path + "-original");
        Directory.CreateDirectory(path);
        File.WriteAllBytes(Path.Combine(path, "run.exe"), [1, 2, 3]);
        Assert.Throws<AutomationDefinitionException>(() => fixture.Service.Apply(operation, "fixture", input, fixture.Roots, 0, preview.PreviewDigest, new Dictionary<Guid, string>()));
        Assert.Empty(new AutomationDefinitionStore(fixture.Database).Read().Scripts);
        Assert.Empty(new RecoveryJournalRegistry(fixture.Database).ReadSnapshot().Journals);
        var current = fixture.Service.Preview(operation, input, fixture.Roots, 0);
        Assert.Equal(AutomationImportState.Published, fixture.Service.Apply(operation, "fixture", input, fixture.Roots, 0, current.PreviewDigest, new Dictionary<Guid, string>()).State);
    }

    [Theory(DisplayName = "Unknown Import Bytes Cannot Reach Acceptance Or Change Definitions")]
    [InlineData("version")]
    [InlineData("duplicate-id")]
    public void RejectsUnknownBytes(string fault)
    {
        using var fixture = Create();
        var document = JsonNode.Parse(Encode(fixture))!;
        if (fault == "version") document["version"] = 77;
        else document["scripts"]!.AsArray().Add(document["scripts"]![0]!.DeepClone());
        var input = JsonSerializer.SerializeToUtf8Bytes(document);
        var error = Record.Exception(() => fixture.Service.Apply(Guid.NewGuid(), "fixture", input, fixture.Roots, 0, new string('a', 64), new Dictionary<Guid, string>()));
        Assert.True(error is JsonException or AutomationDefinitionException, error?.ToString() ?? "Expected rejection");
        Assert.Empty(new AutomationDefinitionStore(fixture.Database).Read().Scripts);
        Assert.Empty(new RecoveryJournalRegistry(fixture.Database).ReadSnapshot().Journals);
    }

    [Fact(DisplayName = "An Attachment Digest Mismatch Leaves The Whole Import Unaccepted And Allows A Corrected Request")]
    public void RejectsMismatchedAttachments()
    {
        using var fixture = Create();
        var attachment = Attachment(fixture);
        var wrong = attachment.Asset with { Sha256 = new string('a', 64) };
        var input = Encode(fixture, wrong);
        var operation = Guid.NewGuid();
        var preview = fixture.Service.Preview(operation, input, fixture.Roots, 0);
        var selected = new Dictionary<Guid, string> { [attachment.Asset.Id] = attachment.Path };
        Assert.Throws<IOException>(() => fixture.Service.Apply(operation, "fixture", input, fixture.Roots, 0, preview.PreviewDigest, selected));
        Assert.Empty(new AutomationDefinitionStore(fixture.Database).Read().Scripts);
        Assert.Empty(new RecoveryJournalRegistry(fixture.Database).ReadSnapshot().Journals);
        var corrected = Encode(fixture, attachment.Asset);
        var correctedPreview = fixture.Service.Preview(operation, corrected, fixture.Roots, 0);
        Assert.Equal(AutomationImportState.Published, fixture.Service.Apply(operation, "fixture", corrected, fixture.Roots, 0, correctedPreview.PreviewDigest, selected).State);
    }

    [Theory(DisplayName = "Import Admission Cannot Bypass An Active Definition Lease Or An Unknown Recovery Index")]
    [InlineData(false)]
    [InlineData(true)]
    public void RequiresImportAdmission(bool unknown)
    {
        using var fixture = Create();
        var input = Encode(fixture);
        var operation = Guid.NewGuid();
        var preview = fixture.Service.Preview(operation, input, fixture.Roots, 0);
        using var blocker = unknown ? null : fixture.Resources.TryAcquire(Guid.NewGuid(), [new("automation:definitions", ResourceClaimMode.Mutation)]).Lease;
        if (unknown) fixture.Index.BlockUnknown("fixture-unverified");
        var error = Assert.Throws<AutomationDefinitionException>(() => fixture.Service.Apply(operation, "fixture", input, fixture.Roots, 0, preview.PreviewDigest, new Dictionary<Guid, string>()));
        Assert.Equal(unknown ? "automation_host_unavailable" : "automation_resource_conflict", error.Message);
        Assert.Empty(new AutomationDefinitionStore(fixture.Database).Read().Scripts);
    }

    [Fact(DisplayName = "A Committed Definition Export Imports Through The Same Service Into Another New Instance")]
    public void RoundTripsExports()
    {
        using var source = Create();
        var input = Encode(source);
        var operation = Guid.NewGuid();
        var preview = source.Service.Preview(operation, input, source.Roots, 0);
        source.Service.Apply(operation, "fixture", input, source.Roots, 0, preview.PreviewDigest, new Dictionary<Guid, string>());
        using var export = source.Service.PrepareExport(DateTimeOffset.UtcNow);
        var exported = export.ExportBundle();
        using var target = Create();
        var roots = source.Roots.Keys.ToDictionary(value => value, _ => target.Roots.Values.Single());
        var targetOperation = Guid.NewGuid();
        var targetPreview = target.Service.Preview(targetOperation, exported, roots, 0);
        target.Service.Apply(targetOperation, "fixture", exported, roots, 0, targetPreview.PreviewDigest, new Dictionary<Guid, string>());
        var actual = new AutomationDefinitionStore(target.Database).Read();
        Assert.Equal(target.Roots.Values.Single(), actual.Roots[0].Path);
        Assert.Equal(source.Definitions.Queues[0].ScriptIds.ToArray(), actual.Queues[0].ScriptIds.ToArray());
        Assert.Equal(source.Definitions.Scripts[0].Id, actual.Scripts[0].Id);
    }

    private static byte[] Encode(Fixture fixture, AutomationImportAttachment? attachment = null) =>
        new AutomationBundleCodec(new()).Encode(fixture.Definitions, DateTimeOffset.UtcNow, selectedAttachments: attachment is null ? [] : [attachment]);
    private static AutomationImportResult Import(Fixture fixture, byte[] input, (AutomationImportAttachment Asset, string Path) attachment, long revision)
    {
        var operation = Guid.NewGuid();
        var preview = fixture.Service.Preview(operation, input, fixture.Roots, revision);
        return fixture.Service.Apply(operation, "fixture", input, fixture.Roots, revision, preview.PreviewDigest,
            new Dictionary<Guid, string> { [attachment.Asset.Id] = attachment.Path });
    }
    private static (AutomationImportAttachment Asset, string Path) Attachment(Fixture fixture)
    {
        var path = Path.Combine(fixture.Root, "source.asset");
        File.WriteAllBytes(path, [2, 3, 5]);
        return (new(Guid.NewGuid(), "attachments/data.bin", 3, Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path)))), path);
    }
    private static Fixture Create()
    {
        var root = Path.Combine(Environment.GetEnvironmentVariable("NEXUS_TEST_ROOT")!, "import-service-" + Guid.NewGuid().ToString("N"));
        var instance = new InstanceRoot(new()).OpenOrCreate(Path.Combine(root, "runtime"));
        var database = StateDatabase.CreateFresh(instance);
        var definitions = AutomationEdits.Apply(AutomationCatalog.Empty, AutomationDefinitionSeed.Create(root));
        var roots = definitions.Roots.ToDictionary(value => value.Id, value => value.Path);
        var lease = InstanceLease.Acquire(instance, new());
        var index = new StartupJournalRecovery(instance, new(), database, new(Path.Combine(root, "host"), Path.Combine(instance.RuntimeRoot, "extensions"))).Recover(lease);
        var resources = new ResourceLeaseManager(new(index, TimeProvider.System));
        return new(root, instance, database, definitions, roots, lease, index, resources, new(instance, new(), database, lease, resources, index));
    }
    private sealed record Fixture(string Root, RuntimeInstance Instance, StateDatabase Database, AutomationCatalog Definitions,
        Dictionary<Guid, string> Roots, InstanceLease Lease, RecoveryIndex Index, ResourceLeaseManager Resources, AutomationImportService Service) : IDisposable
    {
        public void Dispose() => Lease.Dispose();
    }
}
