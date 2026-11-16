using System.Runtime.Versioning;
using NexusPipeline.Core.Configuration;
using NexusPipeline.Core.Maintenance;
using NexusPipeline.Core.Operations;
using NexusPipeline.Core.Records;
using NexusPipeline.Platform.Storage;
using NexusPipeline.Platform.Storage.Execution;
using NexusPipeline.Platform.Storage.Journals;
using NexusPipeline.Platform.Storage.Records;
using NexusPipeline.Platform.Storage.Sqlite;
using Xunit;

namespace NexusPipeline.StorageTests;

[SupportedOSPlatform("windows")]
public sealed class RetirementPayloadTests
{
    [Theory(DisplayName = "Historical Content Retires Beside Actual Extension And Update Payload Journals With Their Bound Roots")]
    [InlineData(JournalKind.ExtensionApply)]
    [InlineData(JournalKind.UpdateApply)]
    public async Task RetiresBesidePayloadHistory(JournalKind kind)
    {
        using var fixture = await RecordRetirementFixture.CreateAsync();
        var source = fixture.Source;
        var roots = source.PayloadRoots;
        var payloadId = kind == JournalKind.ExtensionApply ? await InstallExtension(fixture, roots) : AcceptUpdate(fixture, roots);
        var registry = new RecoveryJournalRegistry(source.Database);
        var payload = registry.ReadJournal(payloadId);
        Assert.Equal(JournalPhase.Committed, payload.Phase);
        var payloadBytes = new JournalStore(source.Instance, new()).Read(payloadId).Export();
        var ready = new RecoveryCoordinator(registry,
            new SnapshotCompletionEvidence(source.Instance, source.Database, new(), roots), fixture.Hooks.Base.Index).VerifyAndPublish();
        Assert.True(ready.IsReady); Assert.Empty(ready.Isolations);
        var original = new JournalStore(source.Instance, new()).Read(fixture.Receipt.JournalId).Export();
        var page = Assert.IsType<ContentRetirementPage>(fixture.Retirement.RetireLogsPage(
            Guid.NewGuid(), "retirement-payload", fixture.Receipt.Facts.RunId));
        var plan = fixture.Plan(page);
        var missingRoots = new BlobReferenceIndex(source.Instance, new(), source.Database, new(source.Instance, new()));
        Assert.Equal("blob_reference_payload_roots_unavailable", Assert.Throws<IOException>(() =>
            missingRoots.ReadReferenced(plan.Items.Select(item => item.Blob).ToHashSet())).Message);
        var foreignRoots = new PayloadApplyRoots(Path.Combine(source.Root, "foreign-host"), Path.Combine(source.Root, "foreign-extensions"));
        Assert.Throws<IOException>(() => new BlobReferenceIndex(source.Instance, new(), source.Database,
            new(source.Instance, new()), foreignRoots).ReadReferenced(plan.Items.Select(item => item.Blob).ToHashSet()));
        Assert.True(fixture.Retirement.CompleteCleanup(page));
        Assert.Equal(JournalPhase.Committed, registry.ReadJournal(page.CleanupJournalId).Phase);
        Assert.All(plan.Items, item => Assert.False(File.Exists(fixture.PathOf(item.Blob))));
        Assert.All(fixture.Items, item => Assert.Equal(RecordContentAvailability.Retired, item.ContentAvailability));
        Assert.Equal(payloadBytes, new JournalStore(source.Instance, new()).Read(payloadId).Export());
        Assert.Empty(fixture.Hooks.Base.Index.Current.Isolations);
        fixture.RequireOriginalMetadata(original);
    }

    private static async Task<Guid> InstallExtension(RecordRetirementFixture fixture, PayloadApplyRoots roots)
    {
        using var installed = new InstallRuntimeFixture(savedEnabled: false,
            catalog: fixture.Hooks.Base.CreateCatalog(), payloadRoots: roots);
        _ = await installed.Session().ApplyAsync(installed.Prepared, default);
        return installed.Accepted.JournalId;
    }

    private static Guid AcceptUpdate(RecordRetirementFixture fixture, PayloadApplyRoots roots)
    {
        var source = fixture.Source;
        Directory.CreateDirectory(roots.HostPayloadRoot);
        File.WriteAllText(Path.Combine(roots.HostPayloadRoot, "README.md"), "original host");
        var candidate = Path.Combine(source.Root, "update-candidate"); Directory.CreateDirectory(candidate);
        File.WriteAllText(Path.Combine(candidate, "README.md"), "candidate host");
        var archive = new FileTreeArchive(new(SnapshotBudget.Default), new(source.Instance, new()));
        var plan = new FrozenPayloadApply(source.Instance.InstanceId, Guid.NewGuid(), Guid.NewGuid(), JournalKind.UpdateApply,
            PayloadAction.Upgrade, null, roots.HostPayloadRoot, PhysicalObjectIdentity.Read(Path.GetDirectoryName(roots.HostPayloadRoot)!),
            archive.Capture(roots.HostPayloadRoot, default), archive.Capture(candidate, default), ["maintenance:host-payload"]);
        plan = UpdateSourceSeed.Bind(source.Instance, source.Database, plan, candidate);
        _ = new PayloadApplyAcceptance(source.Instance, new(), source.Database, roots).Accept(
            new OperationIdentity(plan.OperationId, "retirement-payload", new string('a', 64), "fixture.payload"), plan, DateTimeOffset.UtcNow);
        var writers = new JournalWriterStore(source.Database);
        PayloadWriterObservation Observe(Guid id)
        {
            var actual = writers.Observe(id, writers.RequireCurrentExecutor(id));
            return new(source.Instance.InstanceId, id, actual.IsComplete, actual.State);
        }
        Assert.Equal(JournalRecoveryResult.NotExecuted, new PreparedPayloadHandler(source.Instance, new(), source.Database,
            roots, JournalKind.UpdateApply, Observe).Prepare(new RecoveryJournalRegistry(source.Database).ReadJournal(plan.JournalId)).Execute());
        return plan.JournalId;
    }
}
