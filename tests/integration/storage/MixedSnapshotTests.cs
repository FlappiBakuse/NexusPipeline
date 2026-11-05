using System.Text;
using System.Runtime.Versioning;
using System.Text.Json;
using NexusPipeline.Core.Configuration;
using NexusPipeline.Core.Secrets;
using NexusPipeline.Platform.Storage;
using NexusPipeline.Platform.Storage.Execution;
using NexusPipeline.Platform.Storage.Journals;
using NexusPipeline.Platform.Storage.Sqlite;
using NexusPipeline.TestFixtures;
using Xunit;

namespace NexusPipeline.StorageTests;

[SupportedOSPlatform("windows")]
public sealed class MixedSnapshotTests
{
    [Fact(DisplayName = "A Real Backup Restores Six Prepared Kinds And Closed Secret Evidence Without Changing Its Source")]
    public async Task RestoresMixedKinds()
    {
        var fixture = MixedRecoveryFixture.Create(false);
        var key = new SecretFieldKey("fixture-extension", Guid.NewGuid(), "credential");
        const string canary = "six-kind-snapshot-secret-018";
        using (var secret = new SecretValue(canary))
            await new SecretFieldStore(fixture.Attempt.Instance, fixture.Attempt.Database).ApplyAsync(Guid.NewGuid(), key, 0, SecretDisposition.Set, secret, default);
        using var sourceLease = InstanceLease.Acquire(fixture.Attempt.Instance, new());
        var sourceSnapshot = fixture.Registry.ReadSnapshot();
        var artifact = Path.Combine(fixture.Attempt.Root, "six-kind-snapshot");
        var files = FixtureInstanceSnapshot.Create(fixture.Attempt.Instance, fixture.Attempt.Database, artifact);
        var restored = FixtureInstanceSnapshot.Restore(artifact, Path.Combine(fixture.Attempt.Root, "restored-runtime"), files);
        using var restoredLease = InstanceLease.Acquire(restored, new());
        var database = StateDatabase.OpenExisting(restored, Path.Combine(fixture.Attempt.Root, "restored-diagnostics"));
        var registry = new RecoveryJournalRegistry(database);
        var before = registry.ReadSnapshot();
        Assert.Equal(7, before.Journals.Count);
        Assert.Equal(sourceSnapshot.Revision, before.Revision);
        Assert.Equal(sourceSnapshot.Isolations, before.Isolations);
        Assert.Equal(6, before.Journals.Count(journal => journal.Phase == JournalPhase.Prepared));
        var secretJournal = Assert.Single(before.Journals, journal => journal.Kind == JournalKind.SecretPrepare);
        Assert.Equal(JournalPhase.Committed, secretJournal.Phase);
        Assert.Null(new AttemptReceiptStore(database).Read(Guid.Parse(fixture.Attempt.Plan.AttemptId)));
        var blobs = new ImmutableBlobStore(restored, new());
        var restoredSource = fixture.Attempt.Source with { Instance = restored, Database = database, Blobs = blobs, Codec = new(restored, new(), blobs) };
        var recoveredFixture = fixture with { Attempt = fixture.Attempt with { Source = restoredSource, Database = database }, Registry = registry };
        var evidence = new JournalRecoveryEvidence(restored, new(restored, new()), database);
        var index = new RecoveryIndex();
        var results = new RecoveryCoordinator(registry, evidence, index).RecoverAndPublish(evidence, recoveredFixture.Handlers());
        Assert.Equal(6, results.Count);
        Assert.DoesNotContain(secretJournal.JournalId, results.Keys);
        var cleanupId = before.Journals.Single(journal => journal.Kind == JournalKind.FileCleanup).JournalId;
        Assert.Equal(JournalRecoveryResult.Quarantined, results[cleanupId]);
        Assert.All(results.Where(item => item.Key != cleanupId && item.Key != secretJournal.JournalId), item => Assert.Equal(JournalRecoveryResult.NotExecuted, item.Value));
        Assert.True(index.Current.IsReady);
        Assert.Equal(cleanupId, Assert.Single(index.Current.Isolations).JournalId);
        var cleanupJournal = new JournalStore(restored, new()).Read(cleanupId);
        using (var document = JsonDocument.Parse(cleanupJournal.Export()))
            Assert.Equal("cleanup_prepared_no_effect_unverified", document.RootElement.GetProperty("receipts").EnumerateArray().Last().GetProperty("detailCode").GetString());
        var cleanupReference = new BlobReference(cleanupJournal.PlanPath, new FileInfo(Path.Combine(restored.RuntimeRoot, cleanupJournal.PlanPath)).Length, cleanupJournal.PlanDigest);
        foreach (var item in new CleanupPlanCodec(restored, new(), blobs).Read(cleanupReference).Items)
        {
            blobs.Verify(item.Blob);
            Assert.NotEqual(item.Identity, PhysicalObjectIdentity.Read(Path.Combine(restored.RuntimeRoot, item.Blob.RelativePath)));
        }
        Assert.NotNull(new AttemptReceiptStore(database).Read(Guid.Parse(fixture.Attempt.Plan.AttemptId)));
        using var material = await new SecretFieldStore(restored, database).ReadAsync(key, default);
        material.Use(chars => Assert.True(chars.SequenceEqual(canary)));
        foreach (var entry in files)
            Assert.True(File.ReadAllBytes(Path.Combine(artifact, entry.RelativePath)).AsSpan().IndexOf(Encoding.UTF8.GetBytes(canary)) < 0);
        FixtureInstanceSnapshot.VerifyFiles(artifact, files);
        Assert.Equal(sourceSnapshot.Revision, fixture.Registry.ReadSnapshot().Revision);
        Assert.Equal(6, fixture.Registry.ReadSnapshot().Journals.Count(journal => journal.Phase == JournalPhase.Prepared));
        Assert.Equal(JournalPhase.Committed, Assert.Single(fixture.Registry.ReadSnapshot().Journals, journal => journal.Kind == JournalKind.SecretPrepare).Phase);
        Assert.Null(new AttemptReceiptStore(fixture.Attempt.Database).Read(Guid.Parse(fixture.Attempt.Plan.AttemptId)));
        File.WriteAllBytes(Path.Combine(fixture.Attempt.Root, "six-kind-snapshot-proof.json"), JsonSerializer.SerializeToUtf8Bytes(new
        {
            status = "PASS", files, sourceInstanceId = fixture.Attempt.Instance.InstanceId, restoredInstanceId = restored.InstanceId,
            before, after = registry.ReadSnapshot(), sourceAfter = fixture.Registry.ReadSnapshot(),
            scope = "Actual SQLite backup plus six prepared kinds, closed secret evidence and DPAPI ciphertext; explicit fixture-only path rebind; five no-effect closures, GC isolation for copied identities and historical secret proof validation; no Host or engine launch",
            productionBackupAndRestore = "NOT_IMPLEMENTED"
        }));
    }
}
