using System.Diagnostics;
using System.Runtime.Versioning;
using NexusPipeline.Core.Automation;
using NexusPipeline.Core.Execution;
using NexusPipeline.Platform.Storage.Execution;
using NexusPipeline.Platform.Storage.Sqlite;
using Xunit;

namespace NexusPipeline.StorageTests;

[SupportedOSPlatform("windows")]
public sealed class ExecutionCompletionTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task WaitsForChild(bool configuration, bool cancel)
    {
        using var fixture = new ExecutionRuntimeFixture(configuration, mode: "child-complete",
            judgement: new(AutomationJudgementLanguage.Keywords, [], [], null));
        var running = fixture.Consumer.RunNextAsync();
        await fixture.Ready(".child");
        using (var connection = DatabaseConnections.Open(fixture.Source.Database.DatabasePath, readOnly: true))
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT process_id FROM journal_writer_job_roots WHERE journal_id=$journal LIMIT 1;";
            command.Parameters.AddWithValue("$journal", fixture.Accepted.JournalId.ToString("D"));
            var pid = checked((int)(long)command.ExecuteScalar()!);
            try { using var root = Process.GetProcessById(pid); await root.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5)); }
            catch (ArgumentException) { }
        }
        Assert.False(running.IsCompleted);
        if (cancel) fixture.Cancel();
        else File.WriteAllText(fixture.Marker + ".child.release", "release");
        var receipt = Assert.IsType<AttemptReceipt>(await running.WaitAsync(TimeSpan.FromSeconds(45)));
        Assert.Equal(cancel ? LifecycleOutcome.Cancelled : LifecycleOutcome.Completed, receipt.Facts.Lifecycle);
        if (!cancel) Assert.Equal(AttemptOutcome.Succeeded, RetryDecision.Classify(receipt.Facts));
        Assert.Null(fixture.Consumer.FailureCode);
        Assert.True(fixture.Base.Index.Current.IsReady);
    }

    [Theory]
    [InlineData("first-failed")]
    [InlineData("first-silent-timeout")]
    public async Task RetriesControlledFailure(string mode)
    {
        using var fixture = new ExecutionRuntimeFixture(configuration: true, mode: mode,
            judgement: new(AutomationJudgementLanguage.Keywords, ["next"], ["failed"], null), limits: new(2, 1, 30), captureFiles: false);
        var receipt = Assert.IsType<AttemptReceipt>(await fixture.Consumer.RunNextAsync().WaitAsync(TimeSpan.FromSeconds(60)));
        Assert.Equal(LifecycleOutcome.Completed, receipt.Facts.Lifecycle);
        Assert.Equal(AttemptOutcome.Succeeded, RetryDecision.Classify(receipt.Facts));
        Assert.Equal(2, ExecutionInvocationCodec.Ordinal(fixture.State.Latest.Root, fixture.State.Total.Steps[0].StepId));
        Assert.Null(fixture.Consumer.FailureCode);
        Assert.True(fixture.Base.Index.Current.IsReady);
    }

    [Fact]
    public async Task KeepsNaturalSuccess()
    {
        using var fixture = new ExecutionRuntimeFixture(judgement: new(AutomationJudgementLanguage.Keywords, [], [], null));
        var receipt = Assert.IsType<AttemptReceipt>(await fixture.Consumer.RunNextAsync());
        Assert.Equal(EngineOutcome.Failed, receipt.Facts.Engine);
        Assert.Equal(BusinessOutcome.NotApplicable, receipt.Facts.Business);
        Assert.Equal(AttemptOutcome.Succeeded, RetryDecision.Classify(receipt.Facts));
    }
}
