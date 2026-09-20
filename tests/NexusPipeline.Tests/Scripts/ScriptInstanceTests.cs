using Xunit;
using NexusPipeline.Modules.Scripts;
namespace NexusPipeline.Tests.Scripts;


/// <summary>模型规则校验：用户名合法性与队列模式/完成操作枚举。</summary>
public class ScriptInstanceTests
{

    [Fact]
    public void ScriptInstance_Clone_CopiesFields()
    {
        var original = new ScriptInstance
        {
            Name = "克隆测试",
            MainExe = "C:\\a.exe",
            Args = "-x",
            SuccessKeywords = "完成",
            PluginType = "test-plugin",
        };

        var clone = original.Clone();

        Assert.Equal(original.Name, clone.Name);
        Assert.Equal(original.MainExe, clone.MainExe);
        Assert.Equal(original.Args, clone.Args);
        Assert.Equal(original.SuccessKeywords, clone.SuccessKeywords);
        Assert.Equal(original.PluginType, clone.PluginType);
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
}
