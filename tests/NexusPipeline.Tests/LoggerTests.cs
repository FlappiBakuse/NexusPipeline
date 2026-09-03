using NexusPipeline.Utilities;
using Xunit;

namespace NexusPipeline.Tests;

/// <summary>日志阈值由设置加载/保存显式配置，Utilities 不再反向读取 RuntimeContext。</summary>
public sealed class LoggerTests
{
    [Fact]
    public void ConfigureLevel_ChangesThresholdWithoutRuntimeContextLookup()
    {
        Logger.ConfigureLevel("error");
        try
        {
            Assert.False(Logger.IsEnabled(LogLevel.Warn));
            Assert.True(Logger.IsEnabled(LogLevel.Error));
        }
        finally
        {
            Logger.ConfigureLevel("info");
        }
    }

    [Fact]
    public void ConfigureLevel_InvalidValueFallsBackToInfo()
    {
        Logger.ConfigureLevel("not-a-level");
        try
        {
            Assert.True(Logger.IsEnabled(LogLevel.Info));
            Assert.False(Logger.IsEnabled(LogLevel.Debug));
        }
        finally
        {
            Logger.ConfigureLevel("info");
        }
    }
}
