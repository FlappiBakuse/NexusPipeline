using System.Text.Json;
using NexusPipeline.Core.Configuration;
using NexusPipeline.Platform.Processes;
using NexusPipeline.Platform.Serialization;
using NexusPipeline.Platform.Storage;
using NexusPipeline.Platform.Storage.Journals;
using NexusPipeline.Platform.Storage.Sqlite;
using NexusPipeline.StorageTests;

internal static class StartupJournalFixture
{
    internal static async Task<int> OwnRecoveryAsync(string[] args)
    {
        if (args is not [var runtime, var report]) return 2;
        OwnedFixtureEvidence.RequirePath(runtime);
        OwnedFixtureEvidence.RequirePath(report);
        var instance = new InstanceRoot(new()).OpenExisting(runtime);
        using var lease = InstanceLease.Acquire(instance, new());
        var database = StateDatabase.OpenExisting(instance, Path.Combine(runtime, "diagnostics", "recovery-owner"), new JournalStartupAdmission(instance, new()));
        var journals = new RecoveryJournalRegistry(database).ReadSnapshot().Journals;
        using var ownership = new JournalRecoveryOwnership(database).Begin(journals);
        OwnedFixtureEvidence.WriteReport(report, new { status = "faultReady", processId = Environment.ProcessId,
            faultPoint = "recovery-owner-recorded-before-first-journal-write" });
        await Task.Delay(Timeout.InfiniteTimeSpan);
        return 0;
    }

    internal static async Task<int> RunAsync(string[] args)
    {
        if (args is not [var root, var mode, var report] || mode is not ("host" or "job" or "six" or "six-unknown")) return 2;
        OwnedFixtureEvidence.RequirePath(root);
        OwnedFixtureEvidence.RequirePath(report);
        if (mode is "six" or "six-unknown")
        {
            var seed = MixedRecoveryFixture.Create(mode == "six-unknown");
            using var seedLease = InstanceLease.Acquire(seed.Attempt.Instance, new());
            OwnedFixtureEvidence.WriteReport(report, new { status = "faultReady", instanceId = seed.Attempt.Instance.InstanceId,
                runtimeRoot = seed.Attempt.Instance.RuntimeRoot, DatabasePath = seed.Attempt.Database.DatabasePath,
                journalId = seed.Attempt.JournalId, target = seed.Attempt.Configuration.TargetPath,
                hostPayloadRoot = seed.Roots.HostPayloadRoot, extensionPayloadRoot = seed.Roots.ExtensionPayloadRoot });
            await Task.Delay(Timeout.InfiniteTimeSpan);
            return 0;
        }
        var contracts = new ContractReader();
        var instance = new InstanceRoot(contracts).OpenOrCreate(Path.Combine(root, "runtime"));
        using var lease = InstanceLease.Acquire(instance, contracts);
        var database = StateDatabase.CreateFresh(instance);
        var target = Path.Combine(root, "target.config");
        var candidate = Path.Combine(root, "candidate.config");
        File.WriteAllBytes(target, [1, 2, 3]);
        File.WriteAllBytes(candidate, [4, 5]);
        var blobs = new ImmutableBlobStore(instance, contracts);
        var archive = new FileTreeArchive(new(SnapshotBudget.Default), blobs);
        var plan = new FrozenConfigurationExchange(instance.InstanceId, Guid.NewGuid(), Guid.NewGuid(), JournalKind.ConfigExchange,
            target, PhysicalObjectIdentity.Read(root), archive.Capture(target, default), archive.Capture(candidate, default));
        using var frozen = new ConfigurationPlanCodec(instance, contracts, blobs).Freeze(plan);
        var journal = new RegisteredJournal(plan.JournalId, plan.OperationId, plan.Kind, JournalPhase.Prepared, 1,
            frozen.Reference.RelativePath, frozen.Reference.Sha256, ["configuration:fixture"]);
        new RecoveryJournalRegistry(database).AcceptPrepared(new(plan.OperationId, "fixture", frozen.Reference.Sha256, "configuration.exchange"), journal,
            [new(plan.JournalId, "install-candidate", FileEffectAction.InstallCandidate, FileEffectState.Pending, frozen.Reference.Sha256, null)], DateTimeOffset.UtcNow);
        using var job = mode == "job" ? new OwnedWorkerJob() : null;
        if (job is not null)
        {
            new JournalWriterStore(database).RegisterJob(plan.JournalId, job);
            job.Start(Environment.ProcessPath!, [Path.Combine(root, "child.ready.json"), "startup-writer-child"], root, instance.InstanceId,
                plan.JournalId, identity => OwnedFixtureEvidence.WriteReport(Path.Combine(root, "child.identity.json"), identity));
        }
        OwnedFixtureEvidence.WriteReport(report, new { status = "faultReady", instanceId = instance.InstanceId, runtimeRoot = instance.RuntimeRoot,
            database.DatabasePath, journalId = plan.JournalId, target, jobName = job?.Name });
        await Task.Delay(Timeout.InfiniteTimeSpan);
        return 0;
    }
}
