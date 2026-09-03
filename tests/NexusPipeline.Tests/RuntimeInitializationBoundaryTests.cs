using Xunit;

namespace NexusPipeline.Tests;

/// <summary>启动所有权边界：常规设置读取发生在互斥体前，实体修复发生在互斥体后。</summary>
public sealed class RuntimeInitializationBoundaryTests
{
    [Fact]
    public void HostedEntityInitializationOccursAfterSingleInstanceAcquisition()
    {
        string root = FindProjectRoot();
        string source = File.ReadAllText(Path.Combine(root, "src", "Application", "StartupPipeline.cs"));
        int mutex = source.IndexOf("AcquireSingleInstanceMutex()", StringComparison.Ordinal);
        int hosted = source.IndexOf("HostedRuntimeInitializer.Initialize", StringComparison.Ordinal);

        Assert.True(mutex >= 0);
        Assert.True(hosted > mutex);
    }

    [Fact]
    public void StartServices_DoesNotReloadSettingsAfterHostedInitialization()
    {
        string root = FindProjectRoot();
        string source = File.ReadAllText(Path.Combine(root, "src", "Bootstrap.cs"));
        Assert.DoesNotContain("ReloadSettings", source);
    }

    [Fact]
    public void ConfigLoadModes_KeepReadOnlyAndRepairSemanticsExplicit()
    {
        string root = FindProjectRoot();
        string source = File.ReadAllText(Path.Combine(root, "src", "Persistence", "ConfigStore.cs"));
        Assert.Contains("ConfigLoadMode mode", source);
        Assert.Contains("mode == ConfigLoadMode.Repair", source);
        Assert.Contains("只读启动不修改原文件", source);
        Assert.Contains("Logger.ConfigureLevel(settings.LogLevel)", source);
    }

    private static string FindProjectRoot()
    {
        DirectoryInfo? current = new(AppContext.BaseDirectory);
        while (current is not null)
        {
            if (File.Exists(Path.Combine(current.FullName, "src", "NexusPipeline.csproj")))
            {
                return current.FullName;
            }
            current = current.Parent;
        }
        throw new InvalidOperationException("无法定位 NexusPipeline 项目根目录");
    }
}
