using NexusPipeline.Core.Automation;
using NexusPipeline.Core.Configuration;
using NexusPipeline.Core.Execution;
using NexusPipeline.Platform.Storage;
using NexusPipeline.Platform.Storage.Automation;
using NexusPipeline.Platform.Storage.Sqlite;
using System.Text.Json;
using System.Text.Json.Nodes;
using Xunit;

namespace NexusPipeline.StorageTests;

public sealed class AssetPreparationTests
{
    [Theory(DisplayName = "Attachment Publication And Recovery Use The Same Approved Revisions To Update Existing Definitions")]
    [InlineData(null)]
    [InlineData(AssetPreparationPoint.CompletedReceipt)]
    [InlineData(AssetPreparationPoint.CommittedJournal)]
    public void PublishesExistingRevisions(AssetPreparationPoint? interruption)
    {
        using var fixture = AssetPreparationFixture.Create(updateExisting: true);
        var store = new AutomationDefinitionStore(fixture.Database);
        var original = store.Read();
        var source = Assert.Single(original.Scripts);
        var root = Assert.Single(original.Roots);
        Assert.Equal(1L, source.Revision);
        Assert.Equal(1L, root.Revision);
        var plan = fixture.Prepared.Plan.ReadWire();
        var revisions = plan.DefinitionRevisions;
        Assert.NotNull(revisions);
        Assert.Contains(revisions, item => item.Kind == "root" && item.Id == root.Id.ToString("D") && item.Revision == root.Revision);
        Assert.Contains(revisions, item => item.Kind == "script" && item.Id == source.Id.ToString("D") && item.Revision == source.Revision);
        var asset = Assert.Single(plan.Assets);
        var path = Path.Combine(fixture.Instance.RuntimeRoot, asset.Content.RelativePath);
        if (interruption is { } stop)
        {
            Assert.Throws<Interrupted>(() => fixture.Execute(new Observer((point, _, _) =>
            {
                if (point == stop) throw new Interrupted();
            })));
            var beforeRecovery = Assert.Single(store.Read().Scripts);
            Assert.Equal((source.Id, source.Revision, source.Name), (beforeRecovery.Id, beforeRecovery.Revision, beforeRecovery.Name));
            var identity = PhysicalObjectIdentity.Read(path);
            var journal = Assert.Single(new RecoveryJournalRegistry(fixture.Database).ReadSnapshot().Journals);
            var handler = new AssetRecoveryHandler(fixture.Instance, new(), fixture.Database, _ => new(true, WriterState.Stopped));
            using var work = handler.Prepare(journal);
            Assert.Equal(stop == AssetPreparationPoint.CommittedJournal ? JournalRecoveryResult.AlreadyCompleted : JournalRecoveryResult.Completed,
                work.Execute());
            Assert.Equal(identity, PhysicalObjectIdentity.Read(path));
        }
        else Assert.True(fixture.Execute().IsNew);

        var applied = store.Read();
        var script = Assert.Single(applied.Scripts);
        Assert.Equal(2L, applied.Revision);
        Assert.Equal(source.Id, script.Id);
        Assert.Equal(root.Id, Assert.Single(applied.Roots).Id);
        Assert.Equal("Imported Script", script.Name);
        Assert.Equal("Imported root", Assert.Single(applied.Roots).Name);
        Assert.Equal(2L, script.Revision);
        Assert.Equal(2L, Assert.Single(applied.Roots).Revision);
        Assert.Equal(original.Users.Select(user => user.Id), applied.Users.Select(user => user.Id));
        Assert.Equal(original.Bindings.Select(binding => binding.Id), applied.Bindings.Select(binding => binding.Id));
        Assert.Equal(new byte[] { 5, 7, 0 }, File.ReadAllBytes(path));
        var replay = fixture.Execute();
        Assert.False(replay.IsNew);
        Assert.Equal(AutomationImportState.Published, replay.State);
        Assert.Equal(2L, replay.CatalogRevision);
        Assert.Equal(script.Id, Assert.Single(store.Read().Scripts).Id);
    }

    [Fact(DisplayName = "Asset Recovery Retains The Approved Revision And Does Not Overwrite A Newer Catalog")]
    public void RecoveryRejectsChangedCatalog()
    {
        using var fixture = AssetPreparationFixture.Create(updateExisting: true);
        Assert.Throws<Interrupted>(() => fixture.Execute(new Observer((point, _, _) =>
        {
            if (point == AssetPreparationPoint.CompletedReceipt) throw new Interrupted();
        })));
        var plan = fixture.Prepared.Plan.ReadWire();
        var asset = Assert.Single(plan.Assets);
        var path = Path.Combine(fixture.Instance.RuntimeRoot, asset.Content.RelativePath);
        var identity = PhysicalObjectIdentity.Read(path);
        var store = new AutomationDefinitionStore(fixture.Database);
        var current = store.Read();
        var source = Assert.Single(current.Scripts);
        var changes = new AutomationChanges(current.Revision, [], [source with { Name = "Newer script" }], [], [], [], [], []);
        store.Commit(AutomationDefinitionStore.Identity(Guid.NewGuid(), "newer-edit", changes), changes, DateTimeOffset.UtcNow);
        var journal = Assert.Single(new RecoveryJournalRegistry(fixture.Database).ReadSnapshot().Journals);
        var handler = new AssetRecoveryHandler(fixture.Instance, new(), fixture.Database, _ => new(true, WriterState.Stopped));
        var before = Fingerprints(fixture.Instance.RuntimeRoot);
        Assert.Throws<AutomationConflictException>(() => handler.Prepare(journal));
        Assert.Equal(before, Fingerprints(fixture.Instance.RuntimeRoot));
        Assert.Equal(identity, PhysicalObjectIdentity.Read(path));
        Assert.Equal("Newer script", Assert.Single(store.Read().Scripts).Name);
        var after = Assert.Single(new RecoveryJournalRegistry(fixture.Database).ReadSnapshot().Journals);
        Assert.Equal((journal.Revision, journal.Phase, journal.PlanDigest, journal.OperationId),
            (after.Revision, after.Phase, after.PlanDigest, after.OperationId));
    }

    [Theory(DisplayName = "An Asset Plan Rejects Revisions Outside Its Portable Definitions")]
    [InlineData(false)]
    [InlineData(true)]
    public void RejectsUnboundDefinitionRevisions(bool duplicate)
    {
        using var fixture = AssetPreparationFixture.Create(updateExisting: true);
        var document = JsonNode.Parse(fixture.Prepared.Plan.Export())!;
        var revisions = document["definitionRevisions"]!.AsArray();
        if (duplicate) revisions.Add(revisions[0]!.DeepClone());
        else revisions[0]!["id"] = Guid.NewGuid().ToString("D");
        var blobs = new ImmutableBlobStore(fixture.Instance, new());
        var unbound = blobs.PersistPlan(JsonSerializer.SerializeToUtf8Bytes(document));
        Assert.Equal("asset_preparation_definition_revision_unbound", Assert.Throws<IOException>(() =>
            new AssetPlanCodec(fixture.Instance, new(), blobs).Read(unbound)).Message);
        Assert.Empty(new RecoveryJournalRegistry(fixture.Database).ReadSnapshot().Journals);
    }

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

    [Theory(DisplayName = "A Rejected Catalog Revision Leaves The Runtime Writable Without Accepting An Asset Operation Or Creating Content")]
    [InlineData(false)]
    [InlineData(true)]
    public void RejectsStaleCatalogs(bool updateExisting)
    {
        using var fixture = AssetPreparationFixture.Create(updateExisting: updateExisting);
        var store = new AutomationDefinitionStore(fixture.Database);
        var changes = AutomationDefinitionSeed.Create(Path.Combine(fixture.Root, "other")) with { ExpectedCatalogRevision = store.Read().Revision };
        var operation = AutomationDefinitionStore.Identity(Guid.NewGuid(), "other", changes);
        store.Commit(operation, changes, DateTimeOffset.UtcNow);
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

    [Theory(DisplayName = "An Asset Completion Requires Its Active Preparation Its Current Gate And The Started Receipt Writer")]
    [InlineData("gate", "asset_evidence_execution_unbound")]
    [InlineData("writer", "asset_started_receipt_unbound")]
    [InlineData("owner", null)]
    public void RechecksCompletionAuthority(string fault, string? reason)
    {
        using var fixture = AssetPreparationFixture.Create();
        var plan = fixture.Prepared.Plan.ReadWire();
        var asset = Assert.Single(plan.Assets);
        var workspace = Path.Combine(fixture.Instance.RuntimeRoot, "asset-preparations", plan.JournalId);
        var content = Path.Combine(fixture.Instance.RuntimeRoot, asset.Content.RelativePath);
        PhysicalObjectIdentity? identity = null;
        var error = Record.Exception(() => fixture.Execute(new Observer((point, journalId, _) =>
        {
            if (point != AssetPreparationPoint.ContentFlushed) return;
            identity = PhysicalObjectIdentity.Read(content);
            if (fault == "gate")
            {
                fixture.Database.Write((connection, transaction) =>
                {
                    using var command = connection.CreateCommand();
                    command.Transaction = transaction;
                    command.CommandText = "UPDATE main.journal_effect_gates SET state='sealed' WHERE journal_id=$journal AND state='entered';";
                    command.Parameters.AddWithValue("$journal", journalId.ToString("D"));
                    Assert.Equal(1, command.ExecuteNonQuery());
                    return true;
                });
            }
            else if (fault == "writer")
            {
                var started = Path.Combine(workspace, asset.Content.Sha256 + ".started.json");
                var receipt = JsonNode.Parse(File.ReadAllBytes(started))!;
                receipt["writerId"] = Guid.NewGuid().ToString("D");
                File.WriteAllBytes(started, JsonSerializer.SerializeToUtf8Bytes(receipt));
            }
            else fixture.Prepared.Dispose();
        })));
        if (fault == "owner") Assert.IsType<ObjectDisposedException>(error);
        else Assert.Equal(reason, Assert.IsType<IOException>(error).Message);
        Assert.NotNull(identity);
        Assert.Equal(identity, PhysicalObjectIdentity.Read(content));
        Assert.Equal(new byte[] { 5, 7, 0 }, File.ReadAllBytes(content));
        Assert.False(File.Exists(Path.Combine(workspace, asset.Content.Sha256 + ".completed.json")));
        Assert.Empty(new AutomationDefinitionStore(fixture.Database).Read().Scripts);
        var journal = Assert.Single(new RecoveryJournalRegistry(fixture.Database).ReadSnapshot().Journals);
        Assert.Equal(JournalPhase.Applying, journal.Phase);
        Assert.Equal(FileEffectState.Started, Assert.Single(new FileEffectOutbox(fixture.Database).Read(journal.JournalId)).State);
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
