using System.Runtime.Versioning;
using NexusPipeline.Core.Configuration;
using NexusPipeline.Core.Execution;
using NexusPipeline.Platform.Processes;
using NexusPipeline.Platform.Storage;
using NexusPipeline.Platform.Storage.Execution;
using NexusPipeline.Platform.Storage.Journals;
using NexusPipeline.Platform.Storage.Sqlite;
using Xunit;

namespace NexusPipeline.StorageTests;

[SupportedOSPlatform("windows")]
public sealed class ProviderInterruptionTests
{
    [Theory(DisplayName = "Actual Worker Channel Loss Restores Configuration And Only Fully Authorized Runs Receive A New Attempt In The Same Lease")]
    [InlineData(true, true)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public async Task RecoversExitedWorker(bool declared, bool approved)
    {
        await using var fixture = await ProviderRuntimeFixture.CreateAsync("retry-channel-loss", configuration: true,
            retrySafe: declared, retryApproved: approved, maximumAttempts: 2);
        var original = File.ReadAllBytes(fixture.Source.Configuration);
        var first = await fixture.RunAsync();
        Assert.Equal(LifecycleOutcome.Interrupted, first.Facts.Lifecycle);
        Assert.Equal(EngineOutcome.Unknown, first.Facts.Engine);
        Assert.Equal(RecoveryOutcome.Restored, first.Facts.Recovery);
        Assert.Equal(original, File.ReadAllBytes(fixture.Source.Configuration));
        RequireStopped(fixture, first);
        var journal = new JournalStore(fixture.Source.Instance, new()).Read(first.JournalId).Export();
        var retries = new ExecutionRetryService(fixture.Source.Instance, fixture.Source.Database, new(), fixture.Source.Lease, fixture.Service);
        retries.Tick(DateTimeOffset.UtcNow);
        if (!declared || !approved)
        {
            Assert.Equal(0, fixture.Base.Inbox.PendingCount);
            Assert.True(new ExecutionRunFinality(fixture.Source.Instance, fixture.Source.Database, new()).IsFinal(first));
            Assert.Single(File.ReadAllLines(fixture.Marker + ".attempts"));
        }
        else
        {
            Assert.Equal(1, fixture.Base.Inbox.PendingCount);
            var second = Assert.IsType<AttemptReceipt>(await fixture.Consumer.RunNextAsync());
            Assert.Equal(first.Facts.RunId, second.Facts.RunId);
            Assert.NotEqual(first.Facts.AttemptId, second.Facts.AttemptId);
            Assert.Equal(2, second.Facts.Ordinal);
            Assert.Equal(LifecycleOutcome.Completed, second.Facts.Lifecycle);
            Assert.Equal(EngineOutcome.Succeeded, second.Facts.Engine);
            Assert.Equal(RecoveryOutcome.Restored, second.Facts.Recovery);
            RequireStopped(fixture, second);
            var calls = File.ReadAllLines(fixture.Marker + ".attempts").Select(line => line.Split('|')).ToArray();
            Assert.Equal(2, calls.Length);
            Assert.NotEqual(calls[0][0], calls[1][0]);
            Assert.Equal(calls[0][2], calls[1][2]);
            Assert.Single(Directory.GetFiles(fixture.Marker + ".effects"));
            retries.Tick(DateTimeOffset.UtcNow);
            Assert.Equal(0, fixture.Base.Inbox.PendingCount);
            Assert.True(new ExecutionRunFinality(fixture.Source.Instance, fixture.Source.Database, new()).IsFinal(second));
        }
        Assert.Equal(first, new AttemptReceiptStore(fixture.Source.Database).Read(first.Facts.AttemptId));
        Assert.Equal(journal, new JournalStore(fixture.Source.Instance, new()).Read(first.JournalId).Export());
        Assert.Equal(original, File.ReadAllBytes(fixture.Source.Configuration));
        Assert.Equal(0, fixture.Base.Count("resource_isolations"));
        Assert.True(fixture.Base.Index.Current.IsReady);
    }

    private static void RequireStopped(ProviderRuntimeFixture fixture, AttemptReceipt receipt)
    {
        var state = AttemptWriterState.Read(fixture.Source.Instance, fixture.Source.Database, new(), receipt.JournalId);
        foreach (var job in state.AllJobs)
        {
            var closure = new JobClosureStore(fixture.Source.Instance, fixture.Source.Database, new())
                .ReadForExecutionEvidence(receipt.JournalId, job.WriterId, job.JobName, job.SessionId);
            Assert.NotNull(closure);
            Assert.True(closure.WasExecuted);
            Assert.NotEmpty(closure.Family);
            Assert.All(closure.Family, member => Assert.Equal(WriterState.Stopped, WriterStopEvidence.ObserveProcess(member)));
        }
    }
}
