using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using NexusPipeline.Core.Configuration;
using NexusPipeline.TestFixtures;
using NexusPipeline.Core.Secrets;
using NexusPipeline.Extensibility.Protocol;
using NexusPipeline.Platform.Serialization;
using NexusPipeline.Platform.Storage;
using NexusPipeline.Platform.Storage.Journals;
using NexusPipeline.Platform.Storage.Sqlite;
using Xunit;

namespace NexusPipeline.WindowsTests;

public sealed class SnapshotPrototypeTests
{
    private const string Canary = "snapshot-secret-fixture-018";
    private sealed record Source(string Root, RuntimeInstance Instance, StateDatabase Database, ExecutionJournal Journal, SecretFieldKey SecretKey);

    [Fact(DisplayName = "Instance Snapshot Restores Pending Journal Blobs And Ciphertext While Keeping Recovery Isolation")]
    public async Task RestoresPendingState()
    {
        var source = await CreateSource();
        var contracts = new ContractReader();
        using var lease = InstanceLease.Acquire(source.Instance, contracts);
        var artifact = Path.Combine(source.Root, "snapshot");
        var files = FixtureInstanceSnapshot.Create(source.Instance, source.Database, artifact);
        var manifest = Path.Combine(source.Root, "snapshot-files.json");
        File.WriteAllBytes(manifest, JsonSerializer.SerializeToUtf8Bytes(files));
        var target = Path.Combine(source.Root, "restored-fixture");
        var restored = FixtureInstanceSnapshot.Restore(artifact, target, JsonSerializer.Deserialize<FixtureSnapshotFile[]>(File.ReadAllBytes(manifest))!);
        var database = StateDatabase.OpenExisting(restored, Path.Combine(source.Root, "restore-diagnostics"));
        var blobs = new ImmutableBlobStore(restored, contracts);
        var store = new JournalStore(restored, contracts);
        var journal = store.Read(Guid.Parse(source.Journal.JournalId));
        Assert.Equal(JournalPhase.Prepared, journal.Phase);
        Assert.Equal(source.Journal.PlanDigest, journal.PlanDigest);
        var planPath = Path.Combine(restored.RuntimeRoot, journal.PlanPath);
        var plan = new ConfigurationPlanCodec(restored, contracts, blobs).Read(new(journal.PlanPath, new FileInfo(planPath).Length, journal.PlanDigest));
        Assert.Equal(new byte[] { 18, 0, 1 }, File.ReadAllBytes(plan.TargetPath));
        foreach (var reference in plan.Before.Contents.Concat(plan.Candidate.Contents)) blobs.Verify(reference);
        var registry = new RecoveryJournalRegistry(database);
        var index = new RecoveryIndex();
        new RecoveryCoordinator(registry, new JournalRecoveryEvidence(restored, store), index).VerifyAndPublish();
        Assert.True(index.Current.IsReady);
        Assert.True(index.IsBlocked(new HashSet<string> { "configuration:snapshot-fixture" }, out _));
        var secrets = new SecretFieldStore(restored, database);
        Assert.True((await secrets.GetStateAsync(source.SecretKey, default)).IsReadable);
        using var material = await secrets.ReadAsync(source.SecretKey, default);
        material.Use(chars => Assert.True(chars.SequenceEqual(Canary)));
        var canary = Encoding.UTF8.GetBytes(Canary);
        foreach (var entry in files) Assert.True(File.ReadAllBytes(Path.Combine(artifact, entry.RelativePath)).AsSpan().IndexOf(canary) < 0);
        FixtureInstanceSnapshot.VerifyFiles(artifact, files);
        File.WriteAllBytes(Path.Combine(source.Root, "snapshot-proof.json"), JsonSerializer.SerializeToUtf8Bytes(new
        {
            status = "PASS", files, sourceInstanceId = source.Instance.InstanceId, restoredInstanceId = restored.InstanceId,
            restoredJournalId = journal.JournalId, restoredIsolationCount = index.Current.Isolations.Count,
            scope = "SQLite backup API plus immutable files; explicit fixture-only path rebinding; no Host launched and no external recovery executed",
            productionBackupAndRestore = "NOT_IMPLEMENTED"
        }));
    }

    [Fact(DisplayName = "Damaged Snapshot Bytes Are Rejected Before Creating A Restored Instance")]
    public async Task RejectsDamagedSnapshots()
    {
        var source = await CreateSource();
        using var lease = InstanceLease.Acquire(source.Instance, new ContractReader());
        var artifact = Path.Combine(source.Root, "snapshot");
        var files = FixtureInstanceSnapshot.Create(source.Instance, source.Database, artifact);
        var entry = files.First(item => item.RelativePath.StartsWith("secrets/", StringComparison.Ordinal));
        var path = Path.Combine(artifact, entry.RelativePath);
        var damaged = File.ReadAllBytes(path);
        damaged[0] ^= 1;
        File.WriteAllBytes(path, damaged);
        var target = Path.Combine(source.Root, "must-not-exist");
        Assert.Throws<IOException>(() => FixtureInstanceSnapshot.Restore(artifact, target, files));
        Assert.False(Directory.Exists(target));
        Assert.Equal(damaged, File.ReadAllBytes(path));
    }

    private static async Task<Source> CreateSource()
    {
        var parent = Environment.GetEnvironmentVariable("NEXUS_TEST_ROOT") ?? throw new IOException("Missing external test root");
        var root = Path.Combine(parent, "snapshot-" + Guid.NewGuid().ToString("N"));
        var contracts = new ContractReader();
        var instance = new InstanceRoot(contracts).OpenOrCreate(Path.Combine(root, "source-runtime"));
        var database = StateDatabase.CreateFresh(instance);
        var key = new SecretFieldKey("fixture-extension", Guid.NewGuid(), "credential");
        using var secret = new SecretValue(Canary);
        await new SecretFieldStore(instance, database).ApplyAsync(Guid.NewGuid(), key, 0, SecretDisposition.Set, secret, default);
        var target = Path.Combine(root, "target.config");
        var candidate = Path.Combine(root, "candidate.config");
        File.WriteAllBytes(target, [18, 0, 1]);
        File.WriteAllBytes(candidate, [18, 0, 2]);
        var archive = new FileTreeArchive(new(SnapshotBudget.Default), new(instance, contracts));
        var plan = new FrozenConfigurationExchange(instance.InstanceId, Guid.NewGuid(), Guid.NewGuid(), JournalKind.ConfigEdit, target,
            PhysicalObjectIdentity.Read(root), archive.Capture(target, default), archive.Capture(candidate, default));
        using var frozen = new ConfigurationPlanCodec(instance, contracts, new(instance, contracts)).Freeze(plan);
        var journal = new ExecutionJournal { Format = "nexus.execution.journal", Version = 1, InstanceId = instance.InstanceId.ToString("D"),
            OperationId = plan.OperationId.ToString("D"), JournalId = plan.JournalId.ToString("D"), OperationKind = "configEdit", Phase = "prepared",
            Revision = 1, PlanPath = frozen.Reference.RelativePath, PlanDigest = frozen.Reference.Sha256,
            ResourceKeys = ["configuration:snapshot-fixture"], UpdatedAt = DateTimeOffset.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'"), Participants = [], Receipts = [] };
        new RecoveryJournalRegistry(database).AcceptPrepared(new(plan.OperationId, "snapshot-fixture", frozen.Reference.Sha256, "configuration.edit"),
            new(plan.JournalId, plan.OperationId, plan.Kind, JournalPhase.Prepared, 1, frozen.Reference.RelativePath, frozen.Reference.Sha256, journal.ResourceKeys),
            [new(plan.JournalId, "install-candidate", FileEffectAction.InstallCandidate, FileEffectState.Pending, frozen.Reference.Sha256, null)], DateTimeOffset.UtcNow);
        new JournalStore(instance, contracts).Create(JsonSerializer.SerializeToUtf8Bytes(journal));
        return new(root, instance, database, journal, key);
    }

}
