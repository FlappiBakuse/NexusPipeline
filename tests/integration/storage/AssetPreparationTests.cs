using NexusPipeline.Core.Automation;
using NexusPipeline.Core.Configuration;
using NexusPipeline.Core.Execution;
using NexusPipeline.Platform.Storage;
using NexusPipeline.Platform.Storage.Automation;
using NexusPipeline.Platform.Storage.Sqlite;
using Xunit;

namespace NexusPipeline.StorageTests;

public sealed class AssetPreparationTests
{
    [Theory(DisplayName = "Accepted And Published Asset References Are Rechecked Under The Actual Native Deletion Guard")]
    [InlineData(false)]
    [InlineData(true)]
    public void RechecksDeletionGuards(bool published)
    {
        using var fixture = AssetPreparationFixture.Create();
        if (published) fixture.Execute();
        else Assert.Throws<Interrupted>(() => fixture.Execute(new Observer((point, _, _) =>
        {
            if (point == AssetPreparationPoint.ContentFlushed) throw new Interrupted();
        })));
        var asset = Assert.Single(fixture.Prepared.Plan.ReadWire().Assets);
        var reference = new BlobReference(asset.Content.RelativePath, asset.Content.SizeBytes, asset.Content.Sha256);
        var path = Path.Combine(fixture.Instance.RuntimeRoot, reference.RelativePath);
        var blobs = new ImmutableBlobStore(fixture.Instance, new());
        var item = new NexusPipeline.Platform.Storage.Journals.FrozenCleanupItem("asset-gc", reference,
            PhysicalObjectIdentity.Read(Path.GetDirectoryName(path)!), PhysicalObjectIdentity.Read(path));
        var cleanup = new BlobCleanup(fixture.Instance, blobs, new(fixture.Instance, new(), fixture.Database, blobs));
        Assert.Equal(CleanupStepResult.SkippedReferenced, cleanup.RemoveVerified(item));
        blobs.Verify(reference);
    }

    [Fact(DisplayName = "A Rejected Catalog Revision Leaves The Runtime Writable Without Accepting An Asset Operation Or Creating Content")]
    public void RejectsStaleCatalogs()
    {
        using var fixture = AssetPreparationFixture.Create();
        var changes = AutomationDefinitionSeed.Create(Path.Combine(fixture.Root, "other"));
        var operation = AutomationDefinitionStore.Identity(Guid.NewGuid(), "other", changes);
        new AutomationDefinitionStore(fixture.Database).Commit(operation, changes, DateTimeOffset.UtcNow);
        Assert.Throws<AutomationConflictException>(() => fixture.Execute());
        Assert.True(fixture.Index.Current.IsReady);
        Assert.Empty(new RecoveryJournalRegistry(fixture.Database).ReadSnapshot().Journals);
        var plan = fixture.Prepared.Plan.ReadWire();
        var input = File.ReadAllBytes(Path.Combine(fixture.Instance.RuntimeRoot, plan.Input.RelativePath));
        var roots = plan.Roots.ToDictionary(root => Guid.Parse(root.SourceRootId), root => root.Path);
        var imports = new AutomationImportService(fixture.Instance, new(), fixture.Database, fixture.InstanceLease, fixture.ResourceManager, fixture.Index);
        Assert.Null(imports.ReadAccepted(fixture.Operation.OperationId, fixture.Operation.SubjectId, input, roots,
            plan.ExpectedCatalogRevision, plan.PreviewDigest));
        Assert.All(plan.Assets, asset => Assert.False(File.Exists(Path.Combine(fixture.Instance.RuntimeRoot, asset.Content.RelativePath))));
    }

    [Theory(DisplayName = "Unknown Asset Sidecar Evidence Rejects Copied Startup Inspection Before Changing Original Bytes")]
    [InlineData("version")]
    [InlineData("writer")]
    [InlineData("unknown-entry")]
    [InlineData("missing-started")]
    [InlineData("plan")]
    public void PreservesUnknownSidecars(string fault)
    {
        using var fixture = AssetPreparationFixture.Create();
        Assert.Throws<Interrupted>(() => fixture.Execute(new Observer((point, _, _) =>
        {
            if (point == AssetPreparationPoint.CompletedReceipt) throw new Interrupted();
        })));
        var plan = fixture.Prepared.Plan.ReadWire();
        var asset = Assert.Single(plan.Assets);
        var workspace = Path.Combine(fixture.Instance.RuntimeRoot, "asset-preparations", plan.JournalId);
        var path = Path.Combine(workspace, asset.Content.Sha256 + ".completed.json");
        if (fault == "unknown-entry") File.WriteAllText(Path.Combine(workspace, "unknown.json"), "{}");
        else if (fault == "missing-started") File.Delete(Path.Combine(workspace, asset.Content.Sha256 + ".started.json"));
        else
        {
            var document = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllBytes(path))!;
            if (fault == "version") document["version"] = 99;
            else document[fault == "writer" ? "writerId" : "planDigest"] = fault == "writer" ? Guid.NewGuid().ToString("D") : new string('b', 64);
            File.WriteAllBytes(path, System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(document));
        }
        var before = Fingerprints(fixture.Instance.RuntimeRoot);
        var error = Record.Exception(() => StateDatabase.OpenExisting(fixture.Instance, Path.Combine(fixture.Root, "rejected-startup"),
            new NexusPipeline.Platform.Storage.Journals.JournalStartupAdmission(fixture.Instance, new())));
        Assert.True(error is IOException or System.Text.Json.JsonException, error?.ToString() ?? "Expected read-only rejection");
        Assert.Equal(before, Fingerprints(fixture.Instance.RuntimeRoot));
        Assert.Empty(new AutomationDefinitionStore(fixture.Database).Read().Scripts);
    }

    [Theory(DisplayName = "Equal Bytes In A Replacement File Cannot Use The Original Assets Completion Receipt")]
    [InlineData(false)]
    [InlineData(true)]
    public void RejectsReplacedFiles(bool committed)
    {
        using var fixture = AssetPreparationFixture.Create();
        Assert.Throws<Interrupted>(() => fixture.Execute(new Observer((point, _, _) =>
        {
            if (point == (committed ? AssetPreparationPoint.CommittedJournal : AssetPreparationPoint.CompletedReceipt)) throw new Interrupted();
        })));
        var asset = Assert.Single(fixture.Prepared.Plan.ReadWire().Assets);
        var path = Path.Combine(fixture.Instance.RuntimeRoot, asset.Content.RelativePath);
        var bytes = File.ReadAllBytes(path);
        var original = PhysicalObjectIdentity.Read(path);
        File.Move(path, Path.Combine(fixture.Root, "original-content.bin"));
        File.WriteAllBytes(path, bytes);
        var replacement = PhysicalObjectIdentity.Read(path);
        Assert.NotEqual(original, replacement);
        var registered = Assert.Single(new RecoveryJournalRegistry(fixture.Database).ReadSnapshot().Journals);
        var handler = new AssetRecoveryHandler(fixture.Instance, new(), fixture.Database, _ => new(true, WriterState.Stopped));
        if (committed)
        {
            var before = Fingerprints(fixture.Instance.RuntimeRoot);
            Assert.Throws<IOException>(() => handler.Prepare(registered));
            Assert.Equal(before, Fingerprints(fixture.Instance.RuntimeRoot));
        }
        else
        {
            using var work = handler.Prepare(registered);
            Assert.Equal(JournalRecoveryResult.Quarantined, work.Execute());
        }
        Assert.Equal(replacement, PhysicalObjectIdentity.Read(path));
        Assert.Equal(bytes, File.ReadAllBytes(path));
        Assert.Empty(new AutomationDefinitionStore(fixture.Database).Read().Scripts);
    }

    [Fact(DisplayName = "Every Attachment Completes Before One Transaction Publishes The Whole Definition Graph And Its Asset Owners")]
    public void PublishesCompleteAssets()
    {
        using var fixture = AssetPreparationFixture.Create(2);
        var observed = false;
        var result = fixture.Execute(new Observer((point, _, _) =>
        {
            if (point != AssetPreparationPoint.Published)
                Assert.Empty(new AutomationDefinitionStore(fixture.Database).Read().Scripts);
            if (point == AssetPreparationPoint.CompletedOutbox) observed = true;
        }));
        Assert.True(observed);
        Assert.Equal(AutomationImportState.Published, result.State);
        Assert.True(result.IsNew);
        Assert.Equal(1, result.CatalogRevision);
        var reopened = StateDatabase.OpenExisting(fixture.Instance, Path.Combine(fixture.Root, "reopen"));
        var blobs = new ImmutableBlobStore(fixture.Instance, new());
        var references = new BlobReferenceIndex(fixture.Instance, new(), reopened, blobs);
        foreach (var asset in fixture.Prepared.Plan.ReadWire().Assets)
        {
            var content = new BlobReference(asset.Content.RelativePath, asset.Content.SizeBytes, asset.Content.Sha256);
            blobs.Verify(content);
            Assert.True(references.IsReferenced(content));
        }
        var catalog = new AutomationDefinitionStore(reopened).Read();
        Assert.Single(catalog.Scripts);
        Assert.Equal(2, catalog.Users.Length);
        Assert.Equal(2, catalog.Bindings.Length);
        Assert.Single(catalog.Queues);
        Assert.Single(catalog.Schedules);
        var journal = Assert.Single(new RecoveryJournalRegistry(reopened).ReadSnapshot().Journals);
        Assert.Equal(JournalKind.AssetPrepare, journal.Kind);
        Assert.Equal(JournalPhase.Committed, journal.Phase);
        Assert.All(new FileEffectOutbox(reopened).Read(journal.JournalId), value => Assert.Equal(FileEffectState.Completed, value.State));
        Assert.True(fixture.Index.Current.IsReady);
        Assert.Empty(fixture.Index.Current.Isolations);
        Assert.Equal(result with { IsNew = false }, fixture.Execute());
    }

    [Theory(DisplayName = "Independent Recovery Uses Durable Asset Receipts Without Repeating An Unproven Content Write")]
    [InlineData(AssetPreparationPoint.Accepted, JournalRecoveryResult.NotExecuted)]
    [InlineData(AssetPreparationPoint.InitialJournal, JournalRecoveryResult.NotExecuted)]
    [InlineData(AssetPreparationPoint.EffectEntry, JournalRecoveryResult.Quarantined)]
    [InlineData(AssetPreparationPoint.Applying, JournalRecoveryResult.Quarantined)]
    [InlineData(AssetPreparationPoint.Workspace, JournalRecoveryResult.Quarantined)]
    [InlineData(AssetPreparationPoint.StartedReceipt, JournalRecoveryResult.Quarantined)]
    [InlineData(AssetPreparationPoint.StartedOutbox, JournalRecoveryResult.Quarantined)]
    [InlineData(AssetPreparationPoint.ContentFlushed, JournalRecoveryResult.Quarantined)]
    [InlineData(AssetPreparationPoint.CompletedReceipt, JournalRecoveryResult.Completed)]
    [InlineData(AssetPreparationPoint.CompletedOutbox, JournalRecoveryResult.Completed)]
    [InlineData(AssetPreparationPoint.Applied, JournalRecoveryResult.Completed)]
    [InlineData(AssetPreparationPoint.CommittedJournal, JournalRecoveryResult.AlreadyCompleted)]
    [InlineData(AssetPreparationPoint.Published, JournalRecoveryResult.AlreadyCompleted)]
    public void RecoversDurableReceipts(
        AssetPreparationPoint interruption, JournalRecoveryResult expected)
    {
        using var fixture = AssetPreparationFixture.Create();
        Assert.Throws<Interrupted>(() => fixture.Execute(new Observer((point, _, _) =>
        {
            if (point == interruption) throw new Interrupted();
        })));
        var plan = fixture.Prepared.Plan.ReadWire();
        var asset = Assert.Single(plan.Assets);
        var path = Path.Combine(fixture.Instance.RuntimeRoot, asset.Content.RelativePath);
        var before = File.Exists(path) ? PhysicalObjectIdentity.Read(path) : null;
        var registered = Assert.Single(new RecoveryJournalRegistry(fixture.Database).ReadSnapshot().Journals);
        var handler = new AssetRecoveryHandler(fixture.Instance, new(), fixture.Database, _ => new(true, WriterState.Stopped));
        using var work = handler.Prepare(registered);
        Assert.Equal(expected, work.Execute());
        var after = File.Exists(path) ? PhysicalObjectIdentity.Read(path) : null;
        Assert.Equal(before, after);
        var recovered = Assert.Single(new RecoveryJournalRegistry(fixture.Database).ReadSnapshot().Journals);
        if (expected == JournalRecoveryResult.Quarantined)
        {
            Assert.Equal(JournalPhase.Quarantined, recovered.Phase);
            Assert.Empty(new AutomationDefinitionStore(fixture.Database).Read().Scripts);
            Assert.NotEmpty(new RecoveryJournalRegistry(fixture.Database).ReadSnapshot().Isolations);
        }
        else
        {
            Assert.Equal(JournalPhase.Committed, recovered.Phase);
            Assert.Equal(expected == JournalRecoveryResult.NotExecuted ? 0 : 1, new AutomationDefinitionStore(fixture.Database).Read().Scripts.Length);
        }
    }

    private static string[] Fingerprints(string root) => RuntimeFingerprint.Capture(root);

    private sealed class Observer(Action<AssetPreparationPoint, Guid, string?> reached) : IAssetPreparationObserver
    {
        public void Reached(AssetPreparationPoint point, Guid journalId, string? contentDigest) => reached(point, journalId, contentDigest);
    }

    private sealed class Interrupted : Exception;
}
