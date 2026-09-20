using Xunit;
using NexusPipeline.Modules.Settings.Validation;
namespace NexusPipeline.Tests.Settings;


/// <summary>模型规则校验：用户名合法性与队列模式/完成操作枚举。</summary>
public class ScriptTimeoutLimitsTests
{

    [Fact]
    public void Limits_CheckStallMinutes_AcceptsMinusOne()
    {
        Assert.Null(Limits.CheckStallMinutes(-1));
        Assert.Null(Limits.CheckStallMinutes(5));
        Assert.NotNull(Limits.CheckStallMinutes(0));
        Assert.NotNull(Limits.CheckStallMinutes(61));
    }

    [Fact]
    public void Limits_CheckTotalMinutes_AcceptsMinusOne()
    {
        Assert.Null(Limits.CheckTotalMinutes(-1));
        Assert.Null(Limits.CheckTotalMinutes(120));
        Assert.NotNull(Limits.CheckTotalMinutes(4));
        Assert.NotNull(Limits.CheckTotalMinutes(721));
    }

    [Fact]
    public void Limits_CheckScriptTimeouts_AllowsFiniteTotalForLongScript()
    {
        Assert.Null(Limits.CheckScriptTimeouts(-1, -1));
        Assert.Null(Limits.CheckScriptTimeouts(-1, 120));
        Assert.Null(Limits.CheckScriptTimeouts(5, 120));
        Assert.NotNull(Limits.CheckScriptTimeouts(5, -1));
        Assert.NotNull(Limits.CheckScriptTimeouts(0, 0));
    }
}
