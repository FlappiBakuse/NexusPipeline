using NexusPipeline.Models;
using NexusPipeline.Services.Execution;
using Xunit;

namespace NexusPipeline.Tests;

public sealed class ExecutionUserEligibilityTests
{
    [Theory]
    [InlineData(-1, 999, false)]
    [InlineData(1, 0, false)]
    [InlineData(1, 1, true)]
    [InlineData(1, 2, true)]
    public void DailySuccessCapUsesTheBindingLimit(int maximum, int successfulRunsToday, bool expected)
    {
        var user = new NexusPipeline.App.Abstractions.ResolvedScriptUser(
            "user-1",
            "测试用户",
            new UserScriptBinding { MaxSuccessfulRunsPerDay = maximum });

        Assert.Equal(expected, ExecutionUserEligibility.HasReachedDailySuccessCap(user, successfulRunsToday));
    }

    [Fact]
    public void DailySuccessCapReasonIsStable()
    {
        Assert.Equal(
            "当天已成功运行 3/3 次，达到最多成功运行次数",
            ExecutionUserEligibility.DailySuccessCapReason(3, 3));
    }
}
