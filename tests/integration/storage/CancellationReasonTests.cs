using System.Runtime.Versioning;
using NexusPipeline.Platform.Storage.Execution;
using NexusPipeline.Platform.Storage.Journals;
using Xunit;

namespace NexusPipeline.StorageTests;

[SupportedOSPlatform("windows")]
public sealed class CancellationReasonTests
{
    [Theory(DisplayName = "Every Supported Internal Close Reopens Its Exact Durable Identity And Keeps Dispatch Closed")]
    [InlineData(DispatchIntentCodec.CompletionReason)]
    [InlineData(DispatchIntentCodec.InterruptionReason)]
    [InlineData(DispatchIntentCodec.BudgetReason)]
    [InlineData(DispatchIntentCodec.TargetReason)]
    public async Task PreservesCloseIdentity(string reason)
    {
        using var fixture = new StepWriterFixture();
        var source = fixture.Base.Source;
        fixture.Scope.EnterApplying();
        var store = new ExecutionCancellationStore(source.Instance, source.Database, new(), source.Lease);
        var accepted = store.Close(fixture.JournalId, reason);
        Assert.True(accepted.Acceptance.IsNew);
        Assert.True(accepted.IsProjected);
        var actual = fixture.Scope.VerifyReadOnly();
        Assert.True(actual.IsDispatchClosed);
        Assert.Equal(reason, actual.Cancellation!.ReasonCode);
        Assert.Equal("completed", actual.Cancellation.Status);
        Assert.Equal(accepted.Acceptance.OperationId, actual.Cancellation.Identity.OperationId);
        Assert.Single(actual.Latest.Root.GetProperty("participants").EnumerateArray(), DispatchIntentCodec.IsParticipant);
        var replay = new ExecutionCancellationStore(source.Instance, source.Database, new(), source.Lease).Close(fixture.JournalId, reason);
        Assert.False(replay.Acceptance.IsNew);
        Assert.Equal(actual.Cancellation.Identity.OperationId, replay.Acceptance.OperationId);
        Assert.Throws<IOException>(() => fixture.Writer.Start());
        fixture.AssertClosed(await fixture.Writer.StopAsync(TimeSpan.FromSeconds(10)), executed: false);
    }
}
