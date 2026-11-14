using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using NexusPipeline.Core.Configuration;
using NexusPipeline.Core.Operations;
using NexusPipeline.Core.Records;
using NexusPipeline.Platform.Serialization;
using NexusPipeline.Platform.Storage;
using NexusPipeline.Platform.Storage.Journals;
using NexusPipeline.Platform.Storage.Sqlite;
using Xunit;

namespace NexusPipeline.StorageTests;

public sealed class BlobCleanupTests
{
    [Fact(DisplayName = "Candidate Inspection Releases Its Own Read Handles Before Deletion In An Outer Read Scope")]
    public void ReleasesCandidateReads()
    {
        var fixture = Create();
        using var reads = BlobReadScope.Hold(fixture.Instance);
        Assert.Equal(CleanupItemState.VerifiedUnreferenced, fixture.Cleanup.Inspect(fixture.Item));
        Assert.Equal(CleanupStepResult.Deleted, fixture.Cleanup.RemoveVerified(fixture.Item));
        Assert.False(File.Exists(fixture.Path));
    }

    [Fact(DisplayName = "One Exact Deletion Guard Reuses Reference Discovery While Preventing New Publication")]
    public void ReusesGuardedReferences()
    {
        var fixture = Create(); var reads = 0;
        var index = new BlobReferenceIndex(fixture.Instance, new(), fixture.Database, fixture.Blobs) { InventoryRead = () => reads++ };
        var cleanup = new BlobCleanup(fixture.Instance, fixture.Blobs, index);
        using var held = PhysicalObjectDeletion.HoldFile(BlobCleanup.Intent(fixture.Item, PhysicalPathPolicy.NormalizeWithoutLinks(fixture.Path)));
        Assert.Equal(CleanupItemState.VerifiedUnreferenced, cleanup.Inspect([fixture.Item], held)[fixture.Item.StepId]);
        Assert.Equal(CleanupItemState.VerifiedUnreferenced, cleanup.Inspect([fixture.Item], held)[fixture.Item.StepId]);
        Assert.Throws<IOException>(() => Attach(fixture));
        Assert.Equal(CleanupStepResult.Deleted, cleanup.RemoveVerified(fixture.Item, held));
        Assert.Equal(1, reads); Assert.False(File.Exists(fixture.Path));
    }

    [Fact(DisplayName = "An Unreferenced Exact Content Object Can Be Removed Without Touching Other Bytes")]
    public void DeletesExactContent()
    {
        var fixture = Create();
        var unrelated = Path.Combine(fixture.Instance.RuntimeRoot, "unrelated-user-bytes");
        File.WriteAllText(unrelated, "preserve");
        var identity = PhysicalObjectIdentity.Read(unrelated);
        Assert.Equal(CleanupStepResult.Deleted, fixture.Cleanup.RemoveVerified(fixture.Item));
        Assert.False(Path.Exists(fixture.Path));
        Assert.Equal(identity, PhysicalObjectIdentity.Read(unrelated));
        Assert.Equal("preserve", File.ReadAllText(unrelated));
    }

    [Fact(DisplayName = "A Reference Committed After Inspection Is Rechecked Under The Native Deletion Guard")]
    public void RechecksPublishedReferences()
    {
        var fixture = Create();
        var bytes = File.ReadAllBytes(fixture.Path);
        Assert.Equal(CleanupItemState.VerifiedUnreferenced, fixture.Cleanup.Inspect(fixture.Item));
        var attached = Attach(fixture);
        Assert.Equal(CleanupStepResult.SkippedReferenced, fixture.Cleanup.RemoveVerified(fixture.Item));
        var assets = new RecordAssetStore(fixture.Database, fixture.Blobs);
        Assert.Equal(attached, Assert.Single(assets.ReadForRun(attached.RunId).Items));
        Assert.Equal(fixture.Item.Identity, PhysicalObjectIdentity.Read(fixture.Path));
        Assert.Equal(bytes, File.ReadAllBytes(fixture.Path));
    }

    [Fact(DisplayName = "A Deletion Guard Prevents A New Asset Reference From Being Published Before Its File Disappears")]
    public void GuardsReferencePublication()
    {
        var fixture = Create();
        var record = RecordStoreFixture.Record(1, Guid.NewGuid());
        new RunRecordStore(fixture.Database).AppendCompleted([record]);
        var item = fixture.Item;
        var assets = new RecordAssetStore(fixture.Database, fixture.Blobs);
        var deleted = PhysicalObjectDeletion.TryDeleteUnreferenced(new(fixture.Path, item.ParentIdentity, item.Identity,
            FileObjectKind.File, item.Blob.SizeBytes, item.Blob.Sha256), () =>
        {
            var rejection = Assert.Throws<IOException>(() => assets.AttachVerified(
                new(record.FinalAttempt.RunId, item.Blob.Sha256, item.Blob.SizeBytes, RecordAssetKind.Screenshot)));
            Assert.Equal("immutable_blob_open_failed", rejection.Message);
            return true;
        });
        Assert.True(deleted);
        Assert.Empty(assets.ReadForRun(record.FinalAttempt.RunId).Items);
        Assert.False(Path.Exists(fixture.Path));
    }

    [Fact(DisplayName = "Both Prepared And Committed Configuration Plans Protect Their Transitive Contents")]
    public void ProtectsPlanClosure()
    {
        var fixture = Create();
        var contracts = new ContractReader();
        var target = Path.Combine(Path.GetDirectoryName(fixture.Instance.RuntimeRoot)!, "configuration");
        var bytes = File.ReadAllBytes(fixture.Path);
        File.WriteAllBytes(target, bytes);
        var archive = new FileTreeArchive(new(SnapshotBudget.Default), fixture.Blobs);
        var before = archive.Capture(target, default);
        var plan = new FrozenConfigurationExchange(fixture.Instance.InstanceId, Guid.NewGuid(), Guid.NewGuid(), JournalKind.ConfigEdit,
            target, PhysicalObjectIdentity.Read(Path.GetDirectoryName(target)!), before, before);
        using var prepared = new ConfigurationPlanCodec(fixture.Instance, contracts, fixture.Blobs).Freeze(plan);
        Assert.Equal("deletion_open_failed", Assert.Throws<IOException>(() => fixture.Cleanup.RemoveVerified(fixture.Item)).Message);
        var reference = prepared.Reference;
        var registry = new RecoveryJournalRegistry(fixture.Database);
        var registered = new RegisteredJournal(plan.JournalId, plan.OperationId, plan.Kind, JournalPhase.Prepared, 1,
            reference.RelativePath, reference.Sha256, ["configuration:blob-reference"]);
        registry.AcceptPrepared(new(plan.OperationId, "fixture", reference.Sha256, "configuration.edit"), registered, [], DateTimeOffset.UtcNow);
        prepared.Dispose();
        Assert.Equal(CleanupStepResult.SkippedReferenced, fixture.Cleanup.RemoveVerified(fixture.Item));
        registry.Advance(registered with { Phase = JournalPhase.Committed, Revision = 2 }, 1, new(true, false, false, false));
        Assert.Equal(CleanupStepResult.SkippedReferenced, fixture.Cleanup.RemoveVerified(fixture.Item));
        Assert.Equal(fixture.Item.Identity, PhysicalObjectIdentity.Read(fixture.Path));
        Assert.Equal(bytes, File.ReadAllBytes(fixture.Path));
    }

    [Theory(DisplayName = "Unverified Or Cross Instance Cleanup Cannot Remove The Current Object")]
    [InlineData("changed-bytes", "deletion_object_bytes_changed")]
    [InlineData("equal-byte-replacement", "deletion_object_identity_changed")]
    [InlineData("foreign-index", "blob_cleanup_instance_or_path_unbound")]
    public void RejectsUnverifiedCleanup(string fault, string reason)
    {
        var fixture = Create();
        var original = fixture.Path + ".preserved";
        if (fault == "changed-bytes") File.WriteAllBytes(fixture.Path, [0, 1, 255, 3, 9]);
        if (fault == "equal-byte-replacement")
        {
            var bytes = File.ReadAllBytes(fixture.Path);
            File.Move(fixture.Path, original);
            File.WriteAllBytes(fixture.Path, bytes);
        }
        var cleanup = fixture.Cleanup;
        if (fault == "foreign-index")
        {
            var other = Create();
            cleanup = new(fixture.Instance, fixture.Blobs, new(other.Instance, new(), other.Database, other.Blobs));
        }
        var preserved = File.ReadAllBytes(fixture.Path);
        var identity = PhysicalObjectIdentity.Read(fixture.Path);
        Assert.Equal(reason, Assert.Throws<IOException>(() => cleanup.RemoveVerified(fixture.Item)).Message);
        Assert.Equal(preserved, File.ReadAllBytes(fixture.Path));
        Assert.Equal(identity, PhysicalObjectIdentity.Read(fixture.Path));
        if (fault == "equal-byte-replacement")
        {
            Assert.Equal(fixture.Item.Identity, PhysicalObjectIdentity.Read(original));
            Assert.Equal(preserved, File.ReadAllBytes(original));
        }
    }

    [Theory(DisplayName = "Unrecognized Reference Owners Preserve Content Instead Of Being Ignored")]
    [InlineData("unknown-kind", "blob_reference_evidence_unrecognized")]
    [InlineData("unknown-plan-version", "blob_reference_evidence_unrecognized")]
    [InlineData("unbound-plan", "cleanup_plan_registration_unbound")]
    public void KeepsUnknownOwners(string fault, string reason)
    {
        var fixture = Create();
        var codec = new CleanupPlanCodec(fixture.Instance, new(), fixture.Blobs);
        var plan = codec.Capture(Guid.NewGuid(), Guid.NewGuid(), [fixture.Item.Blob]);
        var reference = codec.Persist(plan);
        if (fault != "unknown-kind")
        {
            var changed = JsonNode.Parse(File.ReadAllBytes(Path.Combine(fixture.Instance.RuntimeRoot, reference.RelativePath)))!;
            if (fault == "unknown-plan-version") changed["version"] = 17;
            else changed["operationId"] = Guid.NewGuid().ToString("D");
            reference = fixture.Blobs.PersistPlan(JsonSerializer.SerializeToUtf8Bytes(changed));
        }
        var journal = new RegisteredJournal(plan.JournalId, plan.OperationId, fault == "unknown-kind" ? JournalKind.RunAttempt : JournalKind.FileCleanup,
            JournalPhase.Prepared, 1, reference.RelativePath, reference.Sha256, ["blob:" + fixture.Item.Blob.Sha256]);
        new RecoveryJournalRegistry(fixture.Database).AcceptPrepared(new(plan.OperationId, "fixture", reference.Sha256, "file.cleanup"),
            journal, [], DateTimeOffset.UtcNow);
        var bytes = File.ReadAllBytes(fixture.Path);
        Assert.Equal(reason, Assert.Throws<IOException>(() => fixture.Cleanup.RemoveVerified(fixture.Item)).Message);
        Assert.Equal(fixture.Item.Identity, PhysicalObjectIdentity.Read(fixture.Path));
        Assert.Equal(bytes, File.ReadAllBytes(fixture.Path));
    }

    [Fact(DisplayName = "An Orphaned Actual Asset Row Cannot Disappear From Cleanup Reference Validation")]
    public void RejectsOrphanedAssets()
    {
        var fixture = Create();
        var asset = Attach(fixture);
        var preserved = File.ReadAllBytes(fixture.Path);
        using (var connection = NexusPipeline.TestFixtures.DatabaseFixtureConnection.Open(
            new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder { DataSource = fixture.Database.DatabasePath, Pooling = false }.ToString()))
        {
            connection.Open(); using var command = connection.CreateCommand();
            command.CommandText = "PRAGMA foreign_keys=OFF;"; command.ExecuteNonQuery();
            command.CommandText = "DELETE FROM content.blobs WHERE digest=$digest;";
            command.Parameters.AddWithValue("$digest", fixture.Item.Blob.Sha256);
            command.ExecuteNonQuery();
            command.CommandText = "SELECT run_id FROM records.record_assets WHERE digest=$digest;";
            Assert.Equal(asset.RunId.ToString("D"), command.ExecuteScalar());
        }
        Assert.Equal("record_retirement_unbound", Assert.Throws<IOException>(() => fixture.Cleanup.RemoveVerified(fixture.Item)).Message);
        Assert.Equal(fixture.Item.Identity, PhysicalObjectIdentity.Read(fixture.Path));
        Assert.Equal(preserved, File.ReadAllBytes(fixture.Path));
    }

    private static RecordAsset Attach(Fixture fixture)
    {
        var record = RecordStoreFixture.Record(1, Guid.NewGuid());
        new RunRecordStore(fixture.Database).AppendCompleted([record]);
        var asset = new RecordAsset(record.FinalAttempt.RunId, fixture.Item.Blob.Sha256, fixture.Item.Blob.SizeBytes, RecordAssetKind.Screenshot);
        new RecordAssetStore(fixture.Database, fixture.Blobs).AttachVerified(asset);
        return asset;
    }

    private static Fixture Create()
    {
        var root = Path.Combine(Environment.GetEnvironmentVariable("NEXUS_TEST_ROOT")!, Guid.NewGuid().ToString("N"));
        var contracts = new ContractReader();
        var instance = new InstanceRoot(contracts).OpenOrCreate(Path.Combine(root, "runtime"));
        var database = StateDatabase.CreateFresh(instance);
        var blobs = new ImmutableBlobStore(instance, contracts);
        var source = Path.Combine(root, "input.bin");
        File.WriteAllBytes(source, [0, 1, 255, 3, 0]);
        var frozen = Assert.Single(new FileTreeScanner(SnapshotBudget.Default).Capture(source, default).Entries);
        var blob = blobs.PersistContent(source, frozen, default);
        var plan = new CleanupPlanCodec(instance, contracts, blobs).Capture(Guid.NewGuid(), Guid.NewGuid(), [blob]);
        return new(instance, database, blobs, Assert.Single(plan.Items), Path.Combine(instance.RuntimeRoot, blob.RelativePath),
            new(instance, blobs, new(instance, contracts, database, blobs)));
    }

    private sealed record Fixture(RuntimeInstance Instance, StateDatabase Database, ImmutableBlobStore Blobs,
        FrozenCleanupItem Item, string Path, BlobCleanup Cleanup);
}
