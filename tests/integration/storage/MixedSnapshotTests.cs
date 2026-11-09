using System.Text;
using System.Runtime.Versioning;
using System.Text.Json;
using NexusPipeline.Core.Configuration;
using NexusPipeline.Core.Execution;
using NexusPipeline.Core.Secrets;
using NexusPipeline.Platform.Storage;
using NexusPipeline.Platform.Storage.Execution;
using NexusPipeline.Platform.Storage.Journals;
using NexusPipeline.Platform.Storage.Sqlite;
using NexusPipeline.TestFixtures;
using Xunit;
using static NexusPipeline.StorageTests.InitialRecoveryTests;

namespace NexusPipeline.StorageTests;

[SupportedOSPlatform("windows")]
public sealed class MixedSnapshotTests
{
    [Fact(DisplayName = "A Real Backup Restores Six Prepared Kinds And Closed Secret Evidence Without Changing Its Source")]
    public async Task RestoresMixedKinds()
    {
        var fixture = MixedRecoveryFixture.Create(false);
        var key = new SecretFieldKey("fixture-extension", Guid.NewGuid(), "credential");
        var secretOperation = Guid.NewGuid();
        var sourceFields = new SecretFieldStore(fixture.Attempt.Instance, fixture.Attempt.Database);
        const string canary = "six-kind-snapshot-secret-018";
        SecretFieldStatus sourceSecret;
        using (var secret = new SecretValue(canary))
            sourceSecret = await sourceFields.ApplyAsync(secretOperation, key, 0, SecretDisposition.Set, secret, default);
        Assert.True(sourceSecret.IsReadable);
        using var sourceLease = InstanceLease.Acquire(fixture.Attempt.Instance, new());
        var sourceSnapshot = fixture.Registry.ReadSnapshot();
        var sourceJournal = fixture.Registry.ReadOperationJournal(secretOperation);
        Assert.Equal(JournalKind.SecretPrepare, sourceJournal.Kind);
        Assert.Equal(JournalPhase.Committed, sourceJournal.Phase);
        var sourceHistory = ReadHistory(fixture.Attempt.Instance, sourceJournal.JournalId);
        var target = fixture.Attempt.Configuration.TargetPath;
        var original = new FileTreeScanner(SnapshotBudget.Default).Capture(target, default);
        var objects = PhysicalTreeIdentity.Capture(target, original);
        var attemptId = Guid.Parse(fixture.Attempt.Plan.AttemptId);
        var artifact = Path.Combine(fixture.Attempt.Root, "six-kind-snapshot");
        var files = FixtureInstanceSnapshot.Create(fixture.Attempt.Instance, fixture.Attempt.Database, artifact);
        var restored = FixtureInstanceSnapshot.Restore(artifact, Path.Combine(fixture.Attempt.Root, "restored-runtime"), files);
        Assert.Equal(fixture.Attempt.Instance.InstanceId, restored.InstanceId);
        using var restoredLease = InstanceLease.Acquire(restored, new());
        var database = StateDatabase.OpenExisting(restored, Path.Combine(fixture.Attempt.Root, "restored-diagnostics"));
        var registry = new RecoveryJournalRegistry(database);
        var before = AssertInventory(registry, sourceSnapshot);
        Assert.Equal(sourceHistory, ReadHistory(restored, sourceJournal.JournalId));
        var receipts = new AttemptReceiptStore(database);
        Assert.Null(receipts.Read(attemptId));
        var blobs = new ImmutableBlobStore(restored, new());
        var restoredSource = fixture.Attempt.Source with { Instance = restored, Database = database, Blobs = blobs, Codec = new(restored, new(), blobs) };
        var recoveredFixture = fixture with { Attempt = fixture.Attempt with { Source = restoredSource, Database = database }, Registry = registry };
        var evidence = new JournalRecoveryEvidence(restored, new(restored, new()), database);
        var index = new RecoveryIndex();
        var results = new RecoveryCoordinator(registry, evidence, index).RecoverAndPublish(evidence, recoveredFixture.Handlers());
        var prepared = before.Journals.Where(journal => journal.Phase == JournalPhase.Prepared).ToArray();
        Assert.Equal(new[] { JournalKind.ConfigExchange, JournalKind.ConfigEdit, JournalKind.FileCleanup,
            JournalKind.RunAttempt, JournalKind.UpdateApply, JournalKind.ExtensionApply }.Order(), prepared.Select(item => item.Kind).Order());
        Assert.Equal(prepared.Select(item => item.JournalId).Order(), results.Keys.Order());
        var cleanup = Assert.Single(prepared, journal => journal.Kind == JournalKind.FileCleanup);
        foreach (var journal in prepared)
        {
            var quarantined = journal.JournalId == cleanup.JournalId;
            Assert.Equal(quarantined ? JournalRecoveryResult.Quarantined : JournalRecoveryResult.NotExecuted, results[journal.JournalId]);
            AssertRegistration(registry, journal, quarantined ? JournalPhase.Quarantined : JournalPhase.Committed);
        }
        Assert.True(index.Current.IsReady);
        AssertCleanupIsolation(restored, blobs, cleanup, index.Current.Isolations);
        var receipt = receipts.Read(attemptId);
        Assert.NotNull(receipt);
        Assert.Equal(fixture.Attempt.JournalId, receipt.JournalId);
        Assert.Equal(Guid.Parse(fixture.Attempt.Plan.OperationId), receipt.OperationId);
        Assert.Equal(Guid.Parse(fixture.Attempt.Plan.RunId), receipt.Facts.RunId);
        Assert.Equal(attemptId, receipt.Facts.AttemptId);
        Assert.Equal(EngineOutcome.NotStarted, receipt.Facts.Engine);
        Assert.Equal(RecoveryOutcome.NotRequired, receipt.Facts.Recovery);
        var restoredFields = new SecretFieldStore(restored, database);
        Assert.Equal(sourceSecret, await restoredFields.GetStateAsync(key, default));
        using (var material = await restoredFields.ReadAsync(key, default)) material.Use(chars => Assert.True(chars.SequenceEqual(canary)));
        using (var replay = new SecretValue(canary))
            Assert.Equal(sourceSecret, await restoredFields.ApplyAsync(secretOperation, key, 0, SecretDisposition.Set, replay, default));
        Assert.Equal(sourceHistory, ReadHistory(restored, sourceJournal.JournalId));
        foreach (var entry in files)
            Assert.True(File.ReadAllBytes(Path.Combine(artifact, entry.RelativePath)).AsSpan().IndexOf(Encoding.UTF8.GetBytes(canary)) < 0);
        FixtureInstanceSnapshot.VerifyFiles(artifact, files);
        var sourceAfter = AssertInventory(fixture.Registry, sourceSnapshot);
        Assert.Equal(sourceHistory, ReadHistory(fixture.Attempt.Instance, sourceJournal.JournalId));
        Assert.Equal(sourceSecret, await sourceFields.GetStateAsync(key, default));
        Assert.Null(new AttemptReceiptStore(fixture.Attempt.Database).Read(attemptId));
        var unchanged = new FileTreeScanner(SnapshotBudget.Default).Capture(target, default);
        Assert.Equal(original.Shape, unchanged.Shape);
        Assert.Equal(original.Entries, unchanged.Entries);
        Assert.Equal(objects, PhysicalTreeIdentity.Capture(target, unchanged));
        File.WriteAllBytes(Path.Combine(fixture.Attempt.Root, "six-kind-snapshot-proof.json"), JsonSerializer.SerializeToUtf8Bytes(new
        {
            status = "PASS", files, sourceInstanceId = fixture.Attempt.Instance.InstanceId, restoredInstanceId = restored.InstanceId,
            before, after = registry.ReadSnapshot(), sourceAfter,
            scope = "Actual SQLite backup plus six prepared kinds, closed secret evidence and DPAPI ciphertext; explicit fixture-only path rebind; five no-effect closures, GC isolation for copied identities and historical secret proof validation; no Host or engine launch",
            productionBackupAndRestore = "NOT_IMPLEMENTED"
        }));
    }

    private static RecoveryRegistrySnapshot AssertInventory(RecoveryJournalRegistry registry, RecoveryRegistrySnapshot expected)
    {
        var actual = registry.ReadSnapshot();
        Assert.Equal(expected.Revision, actual.Revision);
        Assert.Equal(expected.IsReady, actual.IsReady);
        Assert.Equal(expected.Isolations, actual.Isolations);
        Assert.Equal(expected.FileEffects, actual.FileEffects);
        Assert.Equal(expected.Journals.Count, actual.Journals.Count);
        foreach (var journal in expected.Journals)
        {
            var restored = Assert.Single(actual.Journals, item => item.JournalId == journal.JournalId);
            Assert.Equal(journal.ResourceKeys.Order(StringComparer.Ordinal), restored.ResourceKeys.Order(StringComparer.Ordinal));
            Assert.Equal(journal with { ResourceKeys = restored.ResourceKeys }, restored);
        }
        return actual;
    }

    private static byte[][] ReadHistory(RuntimeInstance instance, Guid journalId) =>
        new JournalStore(instance, new()).ReadAllRevisions(journalId).Select(item => item.Export()).ToArray();

    private static void AssertCleanupIsolation(RuntimeInstance instance, ImmutableBlobStore blobs, RegisteredJournal cleanup,
        IReadOnlyList<ResourceIsolation> isolations)
    {
        Assert.All(isolations, isolation => Assert.Equal(cleanup.JournalId, isolation.JournalId));
        Assert.Equal(cleanup.ResourceKeys.Order(StringComparer.Ordinal), isolations.Select(item => item.ResourceKey).Order(StringComparer.Ordinal));
        var journal = new JournalStore(instance, new()).Read(cleanup.JournalId);
        Assert.Equal(JournalPhase.Quarantined, journal.Phase);
        Assert.Equal("cleanup_prepared_no_effect_unverified", journal.Root.GetProperty("receipts").EnumerateArray().Last().GetProperty("detailCode").GetString());
        var reference = new BlobReference(journal.PlanPath, new FileInfo(Path.Combine(instance.RuntimeRoot, journal.PlanPath)).Length, journal.PlanDigest);
        foreach (var item in new CleanupPlanCodec(instance, new(), blobs).Read(reference).Items)
        {
            blobs.Verify(item.Blob);
            Assert.NotEqual(item.Identity, PhysicalObjectIdentity.Read(Path.Combine(instance.RuntimeRoot, item.Blob.RelativePath)));
        }
    }
}
