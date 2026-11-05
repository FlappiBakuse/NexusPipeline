using System.Runtime.Versioning;
using NexusPipeline.Core.Execution;
using NexusPipeline.Core.Records;
using NexusPipeline.Platform.Storage.Execution;
using NexusPipeline.Platform.Storage.Sqlite;
using NexusPipeline.Platform.Storage.Records;
using NexusPipeline.Core.Automation;
using NexusPipeline.Platform.Storage.Automation;
using Xunit;

namespace NexusPipeline.StorageTests;

[SupportedOSPlatform("windows")]
public sealed class AttemptHistoryTests
{
    [Fact(DisplayName = "A Proven Unentered Attempt Reconciles History Without Inventing A Measured Terminal Boundary")]
    public void ReconcilesUnenteredAttempt()
    {
        using var fixture = new ExecutionAdmissionFixture(); var source = fixture.Source;
        var operation = Guid.NewGuid();
        var accepted = fixture.Service.Start(operation, "history-recovery", source.Request(new BindingTarget(source.OtherBinding)), DateTimeOffset.UtcNow);
        fixture.Take(operation).Dispose();
        var recovery = new PreparedRecoverySession(source.Instance, accepted.JournalId, new(), source.Database, () =>
        {
            var observed = new JournalWriterStore(source.Database).ObserveForCurrentExecutor(accepted.JournalId);
            return new(source.Instance.InstanceId, accepted.JournalId, observed.IsComplete, observed.State);
        });
        Assert.Equal(NexusPipeline.Core.Configuration.JournalRecoveryResult.NotExecuted, new PreparedAttemptRecovery(recovery).Recover());
        var state = AttemptWriterState.Read(source.Instance, source.Database, new(), accepted.JournalId);
        var receipt = Assert.IsType<AttemptReceipt>(new AttemptReceiptStore(source.Database).Read(Guid.Parse(state.Attempt.AttemptId)));
        var finality = new ExecutionRunFinality(source.Instance, source.Database, new());
        Assert.False(finality.IsFinal(receipt));
        Assert.Throws<IOException>(() => finality.IsFinal(receipt with { SourceReceiptDigest = new string('0', 64) }));
        var records = new ExecutionRecordProjection(source.Instance, source.Database, new(), source.Lease, finality);
        records.ReconcileStopped(receipt); Assert.Null(records.ReconcileStoppedPage());
        var record = Assert.IsType<RecordDisplay>(new RecordDisplayReader(source.Instance, source.Database, new()).Read(receipt.Facts.RunId));
        Assert.Equal(receipt.Facts.AttemptId, Assert.Single(record.Attempts).AttemptId);
        Assert.Equal(LifecycleOutcome.Blocked, receipt.Facts.Lifecycle);
        Assert.Equal(receipt, new AttemptReceiptStore(source.Database).Read(receipt.Facts.AttemptId));
    }

    [Fact(DisplayName = "Historical Display Uses Verified Frozen Names And Budget Timing After Catalog Names Change")]
    public async Task DisplaysFrozenRecord()
    {
        await using var fixture = await ProviderRuntimeFixture.CreateAsync("success");
        var receipt = await fixture.RunAsync();
        var source = fixture.Source;
        new ExecutionRecordProjection(source.Instance, source.Database, new(), source.Lease,
            new(source.Instance, source.Database, new())).ReconcileStopped(receipt);
        var reader = new RecordDisplayReader(source.Instance, source.Database, new());
        var before = Assert.IsType<RecordDisplay>(reader.Read(receipt.Facts.RunId));
        Assert.Equal("Other", before.TargetName);
        Assert.Equal("manual", before.Mode);
        Assert.Equal(1, before.MaximumAttempts);
        Assert.NotNull(before.StartedAt);
        var timing = Assert.Single(before.Attempts);
        Assert.Equal(receipt.Facts.AttemptId, timing.AttemptId);
        Assert.NotNull(timing.EndedAt);
        Assert.NotNull(timing.DurationMilliseconds);
        Assert.True(timing.StartedAt <= timing.EndedAt);
        var reports = new ExecutionLogReader(source.Instance, source.Database, new());
        var invocation = Assert.Single(Assert.Single(reports.ReadReport(receipt.Facts.RunId, receipt.Facts.AttemptId).Steps).Invocations);
        Assert.NotNull(invocation.StartedAt);
        Assert.NotNull(invocation.EndedAt);
        Assert.True(invocation.StartedAt <= invocation.EndedAt);
        Assert.True(invocation.DurationMilliseconds >= 0);
        var catalog = source.Definitions.Read();
        var script = catalog.Scripts.Single(item => item.Id == source.OtherScriptId);
        var changes = new AutomationChanges(catalog.Revision, [], [script with { Name = "Renamed after run" }], [], [], [], [], []);
        source.Definitions.Commit(AutomationDefinitionStore.Identity(Guid.NewGuid(), "record-display-fixture", changes),
            changes, DateTimeOffset.UtcNow);
        var after = Assert.IsType<RecordDisplay>(reader.Read(receipt.Facts.RunId));
        Assert.Equal(before.TargetName, after.TargetName);
        Assert.Equal(before.UserName, after.UserName);
        Assert.Equal(before.StartedAt, after.StartedAt);
        Assert.Equal(before.Attempts, after.Attempts);
        Assert.Equal(invocation, Assert.Single(Assert.Single(reports.ReadReport(receipt.Facts.RunId, receipt.Facts.AttemptId).Steps).Invocations));
        Assert.Null(reader.Read(Guid.NewGuid()));
    }

    [Fact(DisplayName = "A Real Successful Retry Keeps The Interrupted Attempt Visible In Stable Ordinal Pages")]
    public async Task KeepsInterruptedAttempts()
    {
        await using var fixture = await ProviderRuntimeFixture.CreateAsync("retry-interrupted",
            retrySafe: true, retryApproved: true, maximumAttempts: 2);
        var first = await fixture.RunInterruptedAsync();
        var history = new AttemptHistoryReader(fixture.Source.Database);
        var before = history.ReadPage(new(first.Facts.RunId, Limit: 1));
        Assert.Equal(first, Assert.Single(before.Items)); Assert.Null(before.NextOrdinal);
        var retries = new ExecutionRetryService(fixture.Source.Instance, fixture.Source.Database, new(), fixture.Source.Lease, fixture.Service);
        using var pending = Assert.IsType<PreparedExecutionRetry>(retries.Prepare(first.Facts.RunId, first.Facts.AttemptId, DateTimeOffset.UtcNow));
        _ = retries.Start(pending, DateTimeOffset.UtcNow);
        var second = Assert.IsType<AttemptReceipt>(await fixture.Consumer.RunNextAsync());
        var page = history.ReadPage(new(first.Facts.RunId, Limit: 1));
        Assert.Equal(first, Assert.Single(page.Items)); Assert.Equal(1, page.NextOrdinal);
        var next = history.ReadPage(new(first.Facts.RunId, page.NextOrdinal!.Value, 1));
        Assert.Equal(second, Assert.Single(next.Items)); Assert.Null(next.NextOrdinal);
        Assert.Equal(LifecycleOutcome.Interrupted, page.Items[0].Facts.Lifecycle);
        Assert.Equal(EngineOutcome.Succeeded, next.Items[0].Facts.Engine);
        Assert.Equal(first, Assert.Single(before.Items));
        Assert.Empty(history.ReadPage(new(Guid.NewGuid())).Items);
        Assert.Empty(history.ReadPage(new(first.Facts.RunId, AfterOrdinal: 99)).Items);
    }
}
