using NexusPipeline.Models;
using NexusPipeline.Services;
using Xunit;

namespace NexusPipeline.Tests;

/// <summary>约束规则组件测试：把原先依赖 HTTP/浏览器才能观察的边界收敛到确定性调用。</summary>
public sealed class LimitsTests
{
    [Fact]
    public void DefaultLimitsExposeThePublishedSafeBoundaries()
    {
        Assert.Equal(50, Limits.Current.MaxScripts);
        Assert.Equal(50, Limits.Current.MaxUsersPerScript);
        Assert.Equal(50, Limits.Current.MaxUsers);
        Assert.Equal(50, Limits.Current.MaxQueues);
        Assert.Equal(50, Limits.Current.MaxQueueTotalUsers);
        Assert.Equal(10, Limits.Current.MaxTimeSetsPerQueue);
        Assert.Equal(1, Limits.Current.MinAttempts);
        Assert.Equal(10, Limits.Current.MaxAttempts);
        Assert.Equal(1, Limits.Current.MinStallMinutes);
        Assert.Equal(60, Limits.Current.MaxStallMinutes);
        Assert.Equal(5, Limits.Current.MinTotalMinutes);
        Assert.Equal(720, Limits.Current.MaxTotalMinutes);
        Assert.Equal(64, AppFixedLimits.MaxEntityNameBytes);
        Assert.Equal(512, AppFixedLimits.MaxUserRemarkBytes);
        Assert.Equal(180, AppFixedLimits.HistoryRetentionDaysMax);
    }

    [Theory]
    [InlineData("脚本", 128, false)]
    [InlineData("长", 3, false)]
    [InlineData("长", 2, true)]
    [InlineData("abc", 3, false)]
    [InlineData("abc", 2, true)]
    public void CheckNameBytesUsesUtf8ByteLength(string name, int limit, bool rejected)
    {
        Assert.Equal(rejected, Limits.CheckNameBytes(name, limit, "名称") is not null);
    }

    [Theory]
    [InlineData(0, true)]
    [InlineData(1, false)]
    [InlineData(10, false)]
    [InlineData(11, true)]
    public void CheckAttemptsUsesConfiguredRange(int value, bool rejected)
    {
        Assert.Equal(rejected, Limits.CheckAttempts(value) is not null);
    }

    [Theory]
    [InlineData(0, true)]
    [InlineData(5, false)]
    [InlineData(720, false)]
    [InlineData(721, true)]
    [InlineData(-1, false)]
    public void CheckTotalMinutesSupportsUnlimitedSentinel(int value, bool rejected)
    {
        Assert.Equal(rejected, Limits.CheckTotalMinutes(value) is not null);
    }

    [Fact]
    public void CheckQueueMixRejectsOnlyMixedLongAndNormalScripts()
    {
        var longScript = new ScriptInstance { Id = "long", LogStallTimeoutMinutes = -1, TotalTimeoutMinutes = 120 };
        var normalScript = new ScriptInstance { Id = "normal", LogStallTimeoutMinutes = 5, TotalTimeoutMinutes = 120 };
        var queue = new DispatchQueue
        {
            Tasks =
            [
                new QueueTask { ScriptInstanceId = longScript.Id, Index = 0 },
                new QueueTask { ScriptInstanceId = normalScript.Id, Index = 1 },
            ],
        };

        Assert.NotNull(Limits.CheckQueueMix([longScript, normalScript], queue));
        var singleQueue = new DispatchQueue
        {
            Tasks = [new QueueTask { ScriptInstanceId = longScript.Id }],
        };
        Assert.Null(Limits.CheckQueueMix([longScript], singleQueue));
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(10, false)]
    [InlineData(11, true)]
    public void CheckTimeSetsUsesAnUpperBound(int count, bool rejected)
    {
        Assert.Equal(rejected, Limits.CheckTimeSets(count) is not null);
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(50, false)]
    [InlineData(51, true)]
    public void CheckQueueTotalUsersUsesAnUpperBound(int count, bool rejected)
    {
        Assert.Equal(rejected, Limits.CheckQueueTotalUsers(count) is not null);
    }
}
