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
    [Fact(DisplayName = "An Unreferenced Exact Content Object Can Be Removed Without Touching Other Bytes")]
    public void DeletesExactContent()
    {
        var fixture = Create();
        var unrelated = Path.Combine(fixture.Instance.RuntimeRoot, "unrelated-user-bytes");
        File.WriteAllText(unrelated, "preserve");
        Assert.Equal(CleanupItemState.VerifiedUnreferenced, fixture.Cleanup.Inspect(fixture.Item));
        Assert.Equal(CleanupStepResult.Deleted, fixture.Cleanup.RemoveVerified(fixture.Item));
        Assert.False(File.Exists(fixture.Path));
        Assert.Equal("preserve", File.ReadAllText(unrelated));
    }

    [Fact(DisplayName = "A Reference Committed After Inspection Is Rechecked Under The Native Deletion Guard")]
    public void RechecksPublishedReferences()
    {
        var fixture = Create();
        Assert.Equal(CleanupItemState.VerifiedUnreferenced, fixture.Cleanup.Inspect(fixture.Item));
        Attach(fixture);
        Assert.Equal(CleanupStepResult.SkippedReferenced, fixture.Cleanup.RemoveVerified(fixture.Item));
        Assert.Equal(CleanupItemState.Referenced, fixture.Cleanup.Inspect(fixture.Item));
        fixture.Blobs.Verify(fixture.Item.Blob);
    }

    [Fact(DisplayName = "A Deletion Guard Prevents A New Asset Reference From Being Published Before Its File Disappears")]
    public void GuardsReferencePublication()
    {
        var fixture = Create();
        var record = RecordStoreFixture.Record(1, Guid.NewGuid());
        new RunRecordStore(fixture.Database).AppendCompleted([record]);
        var item = fixture.Item;
        var invoked = false;
        var deleted = PhysicalObjectDeletion.TryDeleteUnreferenced(new(fixture.Path, item.ParentIdentity, item.Identity,
            FileObjectKind.File, item.Blob.SizeBytes, item.Blob.Sha256), () =>
        {
            invoked = true;
            Assert.Throws<IOException>(() => new RecordAssetStore(fixture.Database, fixture.Blobs).AttachVerified(
                new(record.FinalAttempt.RunId, item.Blob.Sha256, item.Blob.SizeBytes, RecordAssetKind.Screenshot)));
            return true;
        });
        Assert.True(invoked);
        Assert.True(deleted);
        Assert.Empty(new RecordAssetStore(fixture.Database, fixture.Blobs).ReadForRun(record.FinalAttempt.RunId).Items);
        Assert.False(File.Exists(fixture.Path));
    }

    [Fact(DisplayName = "Both Prepared And Committed Configuration Plans Protect Their Transitive Contents")]
    public void ProtectsPlanClosure()
    {
        var fixture = Create();
        var contracts = new ContractReader();
        var target = Path.Combine(Path.GetDirectoryName(fixture.Instance.RuntimeRoot)!, "configuration");
        File.WriteAllBytes(target, File.ReadAllBytes(fixture.Path));
        var archive = new FileTreeArchive(new(SnapshotBudget.Default), fixture.Blobs);
        var before = archive.Capture(target, default);
        var plan = new FrozenConfigurationExchange(fixture.Instance.InstanceId, Guid.NewGuid(), Guid.NewGuid(), JournalKind.ConfigEdit,
            target, PhysicalObjectIdentity.Read(Path.GetDirectoryName(target)!), before, before);
        var prepared = new ConfigurationPlanCodec(fixture.Instance, contracts, fixture.Blobs).Freeze(plan);
        Assert.Throws<IOException>(() => fixture.Cleanup.RemoveVerified(fixture.Item));
        var reference = prepared.Reference;
        var registry = new RecoveryJournalRegistry(fixture.Database);
        var registered = new RegisteredJournal(plan.JournalId, plan.OperationId, plan.Kind, JournalPhase.Prepared, 1,
            reference.RelativePath, reference.Sha256, ["configuration:blob-reference"]);
        registry.AcceptPrepared(new(plan.OperationId, "fixture", reference.Sha256, "configuration.edit"), registered, [], DateTimeOffset.UtcNow);
        prepared.Dispose();
        Assert.Equal(CleanupStepResult.SkippedReferenced, fixture.Cleanup.RemoveVerified(fixture.Item));
        registry.Advance(registered with { Phase = JournalPhase.Committed, Revision = 2 }, 1, new(true, false, false, false));
        Assert.Equal(CleanupStepResult.SkippedReferenced, fixture.Cleanup.RemoveVerified(fixture.Item));
        fixture.Blobs.Verify(fixture.Item.Blob);
    }

    [Theory(DisplayName = "Unverified Or Cross Instance Cleanup Cannot Remove The Current Object")]
    [InlineData("changed-bytes")]
    [InlineData("equal-byte-replacement")]
    [InlineData("foreign-index")]
    public void RejectsUnverifiedCleanup(string fault)
    {
        var fixture = Create();
        if (fault == "changed-bytes") File.WriteAllText(fixture.Path, "preserve unknown bytes");
        if (fault == "equal-byte-replacement")
        {
            var bytes = File.ReadAllBytes(fixture.Path);
            File.Move(fixture.Path, fixture.Path + ".preserved");
            File.WriteAllBytes(fixture.Path, bytes);
        }
        var cleanup = fixture.Cleanup;
        if (fault == "foreign-index")
        {
            var other = Create();
            cleanup = new(fixture.Instance, fixture.Blobs, new(other.Instance, new(), other.Database, other.Blobs));
        }
        var preserved = File.ReadAllBytes(fixture.Path);
        Assert.Equal(CleanupItemState.Unknown, cleanup.Inspect(fixture.Item));
        Assert.Throws<IOException>(() => cleanup.RemoveVerified(fixture.Item));
        Assert.Equal(preserved, File.ReadAllBytes(fixture.Path));
    }

    [Theory(DisplayName = "Unrecognized Reference Owners Preserve Content Instead Of Being Ignored")]
    [InlineData("unknown-kind")]
    [InlineData("unknown-plan-version")]
    [InlineData("unbound-plan")]
    public void KeepsUnknownOwners(string fault)
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
        Assert.Equal(CleanupItemState.Unknown, fixture.Cleanup.Inspect(fixture.Item));
        Assert.ThrowsAny<Exception>(() => fixture.Cleanup.RemoveVerified(fixture.Item));
        Assert.Equal(bytes, File.ReadAllBytes(fixture.Path));
    }

    [Fact(DisplayName = "An Orphaned Actual Asset Row Cannot Disappear From Cleanup Reference Validation")]
    public void RejectsOrphanedAssets()
    {
        var fixture = Create(); Attach(fixture);
        var preserved = File.ReadAllBytes(fixture.Path);
        using (var connection = new Microsoft.Data.Sqlite.SqliteConnection(
            new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder { DataSource = fixture.Database.DatabasePath, Pooling = false }.ToString()))
        {
            connection.Open(); using var command = connection.CreateCommand();
            command.CommandText = "PRAGMA foreign_keys=OFF;"; command.ExecuteNonQuery();
            command.CommandText = "DELETE FROM blobs WHERE digest=$digest;";
            command.Parameters.AddWithValue("$digest", fixture.Item.Blob.Sha256);
            Assert.Equal(1, command.ExecuteNonQuery());
            command.CommandText = "SELECT count(*) FROM record_assets WHERE digest=$digest;";
            Assert.Equal(1L, command.ExecuteScalar());
        }
        Assert.Equal(CleanupItemState.Unknown, fixture.Cleanup.Inspect(fixture.Item));
        Assert.Throws<IOException>(() => fixture.Cleanup.RemoveVerified(fixture.Item));
        Assert.Equal(preserved, File.ReadAllBytes(fixture.Path));
    }

    private static void Attach(Fixture fixture)
    {
        var record = RecordStoreFixture.Record(1, Guid.NewGuid());
        new RunRecordStore(fixture.Database).AppendCompleted([record]);
        new RecordAssetStore(fixture.Database, fixture.Blobs).AttachVerified(new(record.FinalAttempt.RunId,
            fixture.Item.Blob.Sha256, fixture.Item.Blob.SizeBytes, RecordAssetKind.Screenshot));
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
