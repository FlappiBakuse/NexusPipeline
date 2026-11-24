using System.Runtime.InteropServices;
using NexusPipeline.Core.Configuration;
using NexusPipeline.Core.Maintenance;
using NexusPipeline.Core.Operations;
using NexusPipeline.Platform.Serialization;
using NexusPipeline.Platform.Storage;
using NexusPipeline.Platform.Storage.Configuration;
using NexusPipeline.Platform.Storage.Journals;
using NexusPipeline.Platform.Storage.Sqlite;
using Xunit;
using Xunit.Abstractions;

namespace NexusPipeline.StorageTests;

public sealed class CleanupReferenceTests(ITestOutputHelper output)
{
    [Fact(DisplayName = "The Actual Cleanup Executor Bounds Shared Snapshot Protection Across Historical Owners")]
    public void BoundsSharedSnapshotInventory()
    {
        var root = Path.Combine(Environment.GetEnvironmentVariable("NEXUS_TEST_ROOT")!, "cleanup-reference-" + Guid.NewGuid().ToString("N"));
        var contracts = new ContractReader();
        var instance = new InstanceRoot(contracts).OpenOrCreate(Path.Combine(root, "runtime"));
        _ = StateDatabase.CreateFresh(instance);
        using var lease = InstanceLease.Acquire(instance, contracts);
        var database = StateDatabase.OpenCurrent(instance, lease);
        var blobs = new ImmutableBlobStore(instance, contracts);
        var registry = new RecoveryJournalRegistry(database);
        var sourceRoot = Path.Combine(root, "source"); Directory.CreateDirectory(sourceRoot);
        var sourcePath = "";
        for (var item = 0; item < 13; item++)
        {
            var path = Path.Combine(sourceRoot, new string('x', 130) + item.ToString("D2") + ".json");
            File.WriteAllText(path, "{\"value\":7}");
            if (item == 0) sourcePath = path;
        }
        var target = Path.Combine(root, "target.json"); File.WriteAllText(target, "{\"value\":1}");
        var archive = new FileTreeArchive(new(SnapshotBudget.Default), blobs);
        var sourceTree = archive.Capture(sourceRoot, default);
        var snapshot = new ConfigurationSnapshotCodec(blobs).Write(sourceTree.Snapshot);
        Assert.InRange(snapshot.SizeBytes, 3000, 4096);
        var before = archive.Capture(target, default);
        var source = new ConfigurationRevision(new(Guid.NewGuid(), "main"), 3, Guid.NewGuid(), Guid.NewGuid(),
            new(1, new string('a', 64), null, false), sourceTree.Snapshot);
        for (var owner = 0; owner < 160; owner++)
        {
            var operation = Guid.NewGuid();
            using var state = new RunStateCodec(instance, contracts, blobs).Freeze(new(instance.InstanceId,
                Guid.NewGuid(), operation, source, 2, false, []));
            var plan = new FrozenConfigurationExchange(instance.InstanceId, operation, Guid.NewGuid(), JournalKind.ConfigExchange,
                target, PhysicalObjectIdentity.Read(root), before, sourceTree) { RunState = state.Reference };
            using var prepared = new ConfigurationPlanCodec(instance, contracts, blobs).Freeze(plan);
            var registered = new RegisteredJournal(plan.JournalId, operation, plan.Kind, JournalPhase.Prepared, 1,
                prepared.Reference.RelativePath, prepared.Reference.Sha256, ["configuration:reference-fixture"]);
            _ = registry.AcceptPrepared(new(operation, "cleanup-reference", prepared.Reference.Sha256, "configuration.exchange"),
                registered, [], DateTimeOffset.UtcNow);
            registry.Advance(registered with { Phase = JournalPhase.Committed, Revision = 2 }, 1, new(true, false, false, false));
        }
        var index = new RecoveryIndex();
        var ready = new RecoveryCoordinator(registry, new JournalRecoveryEvidence(instance, new(instance, contracts), database), index).VerifyAndPublish();
        Assert.True(ready.IsReady); Assert.Empty(ready.Isolations);
        var resources = new ResourceLeaseManager(new(index, TimeProvider.System));
        var content = blobs.PersistContent(new byte[] { 9, 8, 7, 6 }, default);
        var cleanup = new CleanupPlanCodec(instance, contracts, blobs).Capture(Guid.NewGuid(), Guid.NewGuid(), [content]);
        var accepted = new FileCleanupAcceptance(instance, contracts, database).Accept(
            new(cleanup.OperationId, "cleanup-reference", content.Sha256, "file.cleanup"), cleanup, DateTimeOffset.UtcNow);
        using var held = resources.TryAcquire(cleanup.OperationId, FileCleanupItems.Claims(cleanup)).Lease;
        Assert.NotNull(held);
        var roots = new PayloadApplyRoots(AppContext.BaseDirectory, Path.Combine(instance.RuntimeRoot, "extensions"));
        var start = ReadBytes();
        var result = new OwnedCleanupExecutor(instance, contracts, database, index).Execute(accepted.JournalId, lease, held!, roots);
        var readBytes = ReadBytes() - start;
        output.WriteLine(System.Text.Json.JsonSerializer.Serialize(new { phase = "executor-reference-inventory", owners = 160,
            snapshotBytes = snapshot.SizeBytes, maximumReadBytes = 4320948, actualReadBytes = readBytes }));
        Assert.Equal(FileCleanupResult.Cleaned, result);
        Assert.False(File.Exists(Path.Combine(instance.RuntimeRoot, content.RelativePath)));
        Assert.Equal(JournalPhase.Committed, registry.ReadJournal(accepted.JournalId).Phase);
        var effect = Assert.Single(new FileEffectOutbox(database).Read(accepted.JournalId));
        Assert.Equal(accepted.JournalId, effect.JournalId);
        Assert.Equal(Assert.Single(cleanup.Items).StepId, effect.StepId);
        Assert.Equal(FileEffectState.Completed, effect.State);
        Assert.Equal("{\"value\":7}", File.ReadAllText(sourcePath));
        Assert.Equal("{\"value\":1}", File.ReadAllText(target));
        Assert.InRange(readBytes, 0UL, 4320948UL);
    }

    private static ulong ReadBytes()
    {
        if (!GetProcessIoCounters(new IntPtr(-1), out var counters)) throw new IOException("native_process_io_unavailable");
        return counters.ReadBytes;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IoCounters
    {
        public ulong ReadOperations, WriteOperations, OtherOperations, ReadBytes, WriteBytes, OtherBytes;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetProcessIoCounters(IntPtr process, out IoCounters counters);
}
