using NexusPipeline.Services;
using Xunit;

namespace NexusPipeline.Tests;

public sealed class RunAttemptResultTests
{
    [Fact]
    public void FatalAndCancellationStatusesRemainDistinctFromRecoverableFailure()
    {
        RunAttemptResult fatal = RunAttemptResult.Fatal("超时");
        RunAttemptResult cancelled = RunAttemptResult.Cancelled("已取消");
        RunAttemptResult failed = RunAttemptResult.Failed("普通失败");

        Assert.Equal("failed", fatal.Status);
        Assert.True(fatal.IsFatal);
        Assert.Equal("cancelled", cancelled.Status);
        Assert.True(cancelled.IsFatal);
        Assert.False(failed.IsFatal);
    }
}
