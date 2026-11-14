using System.Diagnostics;
using System.Runtime.Versioning;
using NexusPipeline.Core.Configuration;
using NexusPipeline.Core.Execution;
using NexusPipeline.Platform.Storage;
using NexusPipeline.Platform.Storage.Configuration;
using NexusPipeline.Platform.Storage.Execution;
using NexusPipeline.Platform.Storage.Sqlite;
using NexusPipeline.Platform.Storage.Journals;
using Xunit;

namespace NexusPipeline.StorageTests;

[SupportedOSPlatform("windows")]
public sealed class RetryConfigurationTests
{
    [Theory(DisplayName = "A New Attempt Uses The Original Candidate And The Previous Published Cursor With A New Before Scene")]
    [InlineData("retry-config-interrupted")]
    public async Task UsesOriginalCandidates(string mode)
    {
        await using var fixture = await Create(mode);
        var original = File.ReadAllBytes(fixture.Source.Configuration);
        var identity = PhysicalObjectIdentity.Read(fixture.Source.Configuration);
        using var stop = new CancellationTokenSource();
        var running = fixture.Consumer.RunNextAsync(stop.Token);
        if (mode == "retry-config-interrupted")
        {
            await WaitFile(fixture.Marker + ".configuration-1"); stop.Cancel();
        }
        var first = Assert.IsType<AttemptReceipt>(await running);
        Assert.Equal(LifecycleOutcome.Interrupted, first.Facts.Lifecycle);
        Assert.Equal(RecoveryOutcome.Restored, first.Facts.Recovery);
        var revisions = new ConfigurationRevisionStore(fixture.Source.Instance, fixture.Source.Database, new());
        var published = Assert.IsType<ConfigurationRevision>(revisions.Read(fixture.Address!));
        Assert.Equal(2, published.Revision);
        Assert.Equal(original, File.ReadAllBytes(fixture.Source.Configuration));
        Assert.Equal(identity, PhysicalObjectIdentity.Read(fixture.Source.Configuration));
        var prior = Child(fixture, first.JournalId);
        var retries = Retry(fixture);
        using var pending = Assert.IsType<PreparedExecutionRetry>(retries.Prepare(first.Facts.RunId, first.Facts.AttemptId, DateTimeOffset.UtcNow));
        var accepted = Assert.IsType<AttemptAcceptanceResult>(retries.Start(pending, DateTimeOffset.UtcNow));
        var next = Child(fixture, accepted.JournalId);
        Assert.NotEqual(prior.Plan.OperationId, next.Plan.OperationId);
        Assert.NotEqual(prior.Plan.JournalId, next.Plan.JournalId);
        Assert.Equal(prior.State.Source.Revision, next.State.Source.Revision);
        Assert.Equal(1, next.State.Source.Revision);
        Assert.Equal(2, next.State.InitialPublicationRevision);
        Assert.Equal(prior.State.SnapshotReference, next.State.SnapshotReference);
        Assert.Equal(prior.Plan.Candidate.Snapshot.Entries, next.Plan.Candidate.Snapshot.Entries);
        Assert.Equal(prior.Plan.Before.Snapshot.Entries, next.Plan.Before.Snapshot.Entries);
        Assert.False(next.State.SyncInitially); Assert.True(next.State.SyncAtFinish);
        var second = Assert.IsType<AttemptReceipt>(await fixture.Consumer.RunNextAsync());
        Assert.Equal(EngineOutcome.Succeeded, second.Facts.Engine); Assert.Equal(RecoveryOutcome.Restored, second.Facts.Recovery);
        Assert.Equal(3, revisions.Read(fixture.Address!)!.Revision);
        Assert.Equal(first, new AttemptReceiptStore(fixture.Source.Database).Read(first.Facts.AttemptId));
        Assert.Equal(original, File.ReadAllBytes(fixture.Source.Configuration));
        Assert.Equal(identity, PhysicalObjectIdentity.Read(fixture.Source.Configuration));
        Assert.Equal(2, File.ReadAllLines(fixture.Marker + ".attempts").Length);
        Assert.Single(Directory.GetFiles(fixture.Marker + ".effects"));
        Assert.Equal(0, fixture.Base.Count("resource_isolations"));
        ConfigurationRunInventory.VerifyReadOnly(fixture.Source.Instance, fixture.Source.Database, new());
        StartSourceRows.VerifyInventory(fixture.Source.Instance, fixture.Source.Database, new());
    }

    [Fact(DisplayName = "An External Save After Retry Preparation Rejects The New Attempt Without Replacing Its Frozen Candidate")]
    public async Task RejectsAdvancedSources()
    {
        await using var fixture = await Create("retry-config-interrupted");
        var first = await fixture.RunInterruptedAsync(); var retries = Retry(fixture);
        using var pending = Assert.IsType<PreparedExecutionRetry>(retries.Prepare(first.Facts.RunId, first.Facts.AttemptId, DateTimeOffset.UtcNow));
        var scene = File.ReadAllBytes(fixture.Source.Configuration); var identity = PhysicalObjectIdentity.Read(fixture.Source.Configuration);
        var saved = SaveExternal(fixture, "{\"fixture\":true,\"external\":true,\"count\":999}");
        var count = fixture.Base.Count("operations"); var journals = fixture.Base.Count("operation_journals");
        Assert.ThrowsAny<Exception>(() => retries.Start(pending, DateTimeOffset.UtcNow));
        Assert.Equal(count, fixture.Base.Count("operations")); Assert.Equal(journals, fixture.Base.Count("operation_journals"));
        var current = new ConfigurationRevisionStore(fixture.Source.Instance, fixture.Source.Database, new()).Read(fixture.Address!)!;
        Assert.Equal(saved.Revision, current.Revision); Assert.Equal(saved.Tree.Entries, current.Tree.Entries);
        Assert.Equal(scene, File.ReadAllBytes(fixture.Source.Configuration));
        Assert.Equal(identity, PhysicalObjectIdentity.Read(fixture.Source.Configuration));
        Assert.Single(File.ReadAllLines(fixture.Marker + ".attempts"));
        retries.Tick(DateTimeOffset.UtcNow);
        Assert.Equal("retry_sources_unavailable", new ExecutionRunFinality(fixture.Source.Instance, fixture.Source.Database, new())
            .ReadDecision(first).DeclineReasonCode);
    }

    [Fact(DisplayName = "A Real Acceptance Sql Failure Rolls Back The New Operation And Its Publication Cursor Without Running Another Worker")]
    public async Task RevertsAcceptanceFailure()
    {
        await using var fixture = await Create("retry-config-interrupted");
        var first = await fixture.RunInterruptedAsync(); var retries = Retry(fixture);
        using var pending = Assert.IsType<PreparedExecutionRetry>(retries.Prepare(first.Facts.RunId, first.Facts.AttemptId, DateTimeOffset.UtcNow));
        var operations = fixture.Base.Count("operations"); var journals = fixture.Base.Count("operation_journals");
        var scene = File.ReadAllBytes(fixture.Source.Configuration);
        fixture.Base.Execute("CREATE TRIGGER main.reject_retry_source BEFORE INSERT ON execution_start_sources WHEN NEW.attempt_ordinal=2 " +
            "BEGIN SELECT RAISE(ABORT,'retry_source_fault'); END;");
        Assert.ThrowsAny<Exception>(() => retries.Start(pending, DateTimeOffset.UtcNow));
        Assert.Equal(operations, fixture.Base.Count("operations")); Assert.Equal(journals, fixture.Base.Count("operation_journals"));
        Assert.Single(File.ReadAllLines(fixture.Marker + ".attempts"));
        Assert.Equal(2, new ConfigurationRevisionStore(fixture.Source.Instance, fixture.Source.Database, new()).Read(fixture.Address!)!.Revision);
        Assert.Equal(scene, File.ReadAllBytes(fixture.Source.Configuration));
        fixture.Base.Execute("DROP TRIGGER main.reject_retry_source;");
        Assert.IsType<AttemptAcceptanceResult>(retries.Start(pending, DateTimeOffset.UtcNow));
        Assert.Equal(2, Assert.IsType<AttemptReceipt>(await fixture.Consumer.RunNextAsync()).Facts.Ordinal);
    }

    [Fact(DisplayName = "An Accepted Configuration Retry Replays After A Later Save And Missing Live Files Without Reading A New Candidate")]
    public async Task ReplaysFrozenRetry()
    {
        await using var fixture = await Create("retry-config-interrupted");
        var first = await fixture.RunInterruptedAsync(); var retries = Retry(fixture);
        var pending = Assert.IsType<PreparedExecutionRetry>(retries.Prepare(first.Facts.RunId, first.Facts.AttemptId, DateTimeOffset.UtcNow));
        var accepted = Assert.IsType<AttemptAcceptanceResult>(retries.Start(pending, DateTimeOffset.UtcNow));
        _ = await fixture.Consumer.RunNextAsync();
        SaveExternal(fixture, "{\"fixture\":true,\"external\":true,\"count\":999}");
        pending.Dispose(); File.Delete(fixture.Source.Configuration);
        var operations = fixture.Base.Count("operations");
        var replay = Assert.IsType<AttemptAcceptanceResult>(retries.Start(pending, DateTimeOffset.UtcNow));
        Assert.False(replay.Acceptance.IsNew); Assert.Equal(accepted.Plan, replay.Plan); Assert.Equal(accepted.JournalId, replay.JournalId);
        Assert.Equal(operations, fixture.Base.Count("operations")); Assert.False(File.Exists(fixture.Source.Configuration));
        ConfigurationRunInventory.VerifyReadOnly(fixture.Source.Instance, fixture.Source.Database, new());
    }

    [Theory(DisplayName = "Read Only Inventory Rejects Damaged Historical Publication Binding Without Repair")]
    [InlineData("previous-publication")]
    [InlineData("initial-publication")]
    public async Task RejectsDamagedHistory(string damage)
    {
        await using var fixture = await Create("retry-config-interrupted");
        var first = await fixture.RunInterruptedAsync(); var retries = Retry(fixture);
        using var pending = Assert.IsType<PreparedExecutionRetry>(retries.Prepare(first.Facts.RunId, first.Facts.AttemptId, DateTimeOffset.UtcNow));
        var accepted = Assert.IsType<AttemptAcceptanceResult>(retries.Start(pending, DateTimeOffset.UtcNow));
        _ = await fixture.Consumer.RunNextAsync();
        fixture.Base.Execute(damage == "previous-publication"
            ? "UPDATE configuration.configuration_run_resources SET current_revision=1 WHERE session_id IN (SELECT session_id FROM configuration.configuration_run_sessions WHERE parent_journal_id='" + first.JournalId.ToString("D") + "');"
            : "UPDATE configuration.configuration_run_resources SET expected_revision=1 WHERE session_id IN (SELECT session_id FROM configuration.configuration_run_sessions WHERE parent_journal_id='" + accepted.JournalId.ToString("D") + "');");
        var path = fixture.Source.Database.DatabasePath; var bytes = File.ReadAllBytes(path);
        var wal = File.Exists(path + "-wal") ? File.ReadAllBytes(path + "-wal") : null;
        Assert.ThrowsAny<Exception>(() => StateDatabase.OpenExisting(fixture.Source.Instance,
            Path.Combine(fixture.Source.Root, "retry-configuration-diagnostic"), new JournalStartupAdmission(fixture.Source.Instance, new())));
        Assert.Equal(bytes, File.ReadAllBytes(path));
        Assert.Equal(wal, File.Exists(path + "-wal") ? File.ReadAllBytes(path + "-wal") : null);
    }

    private static Task<ProviderRuntimeFixture> Create(string mode) =>
        ProviderRuntimeFixture.CreateAsync(mode, configuration: true, retrySafe: true, retryApproved: true, maximumAttempts: 2);
    private static ExecutionRetryService Retry(ProviderRuntimeFixture fixture) =>
        new(fixture.Source.Instance, fixture.Source.Database, new(), fixture.Source.Lease, fixture.Service);

    private static (FrozenConfigurationExchange Plan, FrozenRunState State) Child(ProviderRuntimeFixture fixture, Guid journal)
    {
        var state = AttemptWriterState.Read(fixture.Source.Instance, fixture.Source.Database, new(), journal);
        var reference = AttemptPlanCodec.Reference(Assert.Single(state.Attempt.ConfigurationPlans).Plan);
        var plan = new ConfigurationPlanCodec(fixture.Source.Instance, new(), new(fixture.Source.Instance, new())).ReadStructure(reference);
        return (plan, new RunStateCodec(fixture.Source.Instance, new(), new(fixture.Source.Instance, new())).ReadStructure(plan.RunState!));
    }

    private static ConfigurationRevision SaveExternal(ProviderRuntimeFixture fixture, string content)
    {
        var revisions = new ConfigurationRevisionStore(fixture.Source.Instance, fixture.Source.Database, new());
        var saved = revisions.Read(fixture.Address!)!; var original = File.ReadAllBytes(fixture.Source.Configuration);
        FileTreeSnapshot tree;
        try
        {
            File.WriteAllText(fixture.Source.Configuration, content);
            tree = new FileTreeArchive(new(SnapshotBudget.Default), new(fixture.Source.Instance, new()))
                .Capture(fixture.Source.Configuration, default).Snapshot;
        }
        finally { File.WriteAllBytes(fixture.Source.Configuration, original); }
        var binding = fixture.Source.Definitions.Read().Bindings.Single(item => item.Id == fixture.Source.OtherBinding);
        var change = new ConfigurationRevisionChange(fixture.Address!, saved.Revision, binding.Revision, saved.Contract, tree, ConfigurationUpdateKind.Manual);
        return revisions.Commit(ConfigurationRevisionStore.Identity(Guid.NewGuid(), "provider-runtime-fixture", change),
            change, DateTimeOffset.UtcNow).Saved;
    }

    private static async Task WaitFile(string path)
    {
        var watch = Stopwatch.StartNew();
        while (!File.Exists(path) && watch.Elapsed < TimeSpan.FromSeconds(30)) await Task.Delay(20);
        Assert.True(File.Exists(path), path);
    }
}
