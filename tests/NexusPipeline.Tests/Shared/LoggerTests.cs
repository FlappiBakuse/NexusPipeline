using Xunit;
using NexusPipeline.Host.Composition;
using NexusPipeline.Shared.Logging;

namespace NexusPipeline.Tests.Shared;

/// <summary>日志阈值由设置加载/保存显式配置，Utilities 不再反向读取 HostCompositionRoot。</summary>
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
