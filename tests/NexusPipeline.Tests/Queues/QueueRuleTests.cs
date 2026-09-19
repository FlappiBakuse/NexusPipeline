using Xunit;
using NexusPipeline.Modules.Queues;
namespace NexusPipeline.Tests.Queues;


/// <summary>模型规则校验：用户名合法性与队列模式/完成操作枚举。</summary>
public class QueueRuleTests
{

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
}
