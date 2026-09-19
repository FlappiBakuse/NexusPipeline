using Xunit;
using NexusPipeline.Modules.Users;
namespace NexusPipeline.Tests.Users;


/// <summary>模型规则校验：用户名合法性与队列模式/完成操作枚举。</summary>
public class UserNameRuleTests
{
    [Theory]
    [InlineData("默认")]
    [InlineData("甲")]
    [InlineData("user_1")]
    [InlineData("A-B")]
    public void UserNameRule_ValidNames(string name)
    {
        Assert.True(UserNameRule.IsValidName(name));
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
    public void UserNameRule_InvalidNames(string name)
    {
        Assert.False(UserNameRule.IsValidName(name));
    }
}
