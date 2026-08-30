using NexusPipeline.Models;
using NexusPipeline.Services;
using Xunit;

namespace NexusPipeline.Tests;

/// <summary>模型规则校验：用户名合法性与队列模式/完成操作枚举。</summary>
public class RuleTests
{
    [Theory]
    [InlineData("默认")]
    [InlineData("甲")]
    [InlineData("user_1")]
    [InlineData("A-B")]
    public void ScriptUserRule_ValidNames(string name)
    {
        Assert.True(ScriptUserRule.IsValidName(name));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(".")]
    [InlineData("..")]
    [InlineData("a/b")]
    [InlineData("a\\b")]
    [InlineData("a:b")]
    [InlineData("A.")]
    [InlineData("A ")]
    [InlineData("CON")]
    [InlineData("CON.txt")]
    [InlineData("COM1")]
    public void ScriptUserRule_InvalidNames(string name)
    {
        Assert.False(ScriptUserRule.IsValidName(name));
    }

    [Theory]
    [InlineData("startup")]
    [InlineData("scheduled")]
    [InlineData("none")]
    public void QueueRule_ValidAutoRunModes(string mode)
    {
        Assert.True(QueueRule.IsValidAutoRunMode(mode));
    }

    [Theory]
    [InlineData("")]
    [InlineData("daily")]
    [InlineData("start")]
    public void QueueRule_InvalidAutoRunModes(string mode)
    {
        Assert.False(QueueRule.IsValidAutoRunMode(mode));
    }

    [Theory]
    [InlineData("none")]
    [InlineData("exit")]
    [InlineData("sleep")]
    [InlineData("reboot")]
    [InlineData("shutdown")]
    public void QueueRule_ValidCompletionActions(string action)
    {
        Assert.True(QueueRule.IsValidCompletionAction(action));
    }

    [Fact]
    public void QueueRule_CompletionActionDesc()
    {
        Assert.Equal("退出软件", QueueRule.CompletionActionDesc("exit"));
        Assert.Equal("休眠", QueueRule.CompletionActionDesc("sleep"));
        Assert.Equal("重启", QueueRule.CompletionActionDesc("reboot"));
        Assert.Equal("关机", QueueRule.CompletionActionDesc("shutdown"));
        Assert.Equal("无操作", QueueRule.CompletionActionDesc("whatever"));
    }

    [Fact]
    public void ScriptInstance_Clone_CopiesFields()
    {
        var original = new ScriptInstance
        {
            Name = "克隆测试",
            MainExe = "C:\\a.exe",
            Args = "-x",
            SuccessKeywords = "完成",
            Users = { new ScriptUser { Name = "甲", Enabled = true } },
        };

        var clone = original.Clone();

        Assert.Equal(original.Name, clone.Name);
        Assert.Equal(original.MainExe, clone.MainExe);
        Assert.Equal(original.Args, clone.Args);
        Assert.Equal(original.SuccessKeywords, clone.SuccessKeywords);
        Assert.Single(clone.Users);
        Assert.NotSame(original.Users[0], clone.Users[0]);
        Assert.NotSame(original, clone);
    }

    [Fact]
    public void ScriptInstance_IsLongRunning_WhenStallTimeoutIsMinusOne()
    {
        Assert.False(new ScriptInstance().IsLongRunning);
        Assert.True(new ScriptInstance { LogStallTimeoutMinutes = -1, TotalTimeoutMinutes = 120 }.IsLongRunning);
        Assert.False(new ScriptInstance { TotalTimeoutMinutes = -1 }.IsLongRunning);
        Assert.True(new ScriptInstance { LogStallTimeoutMinutes = -1, TotalTimeoutMinutes = -1 }.IsLongRunning);
    }

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
