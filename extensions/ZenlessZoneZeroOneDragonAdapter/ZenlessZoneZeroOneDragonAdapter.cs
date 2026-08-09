using NexusPipeline.Plugins;

namespace ZenlessZoneZeroOneDragonAdapter;

/// <summary>
/// Zenless Zone Zero OneDragon（绝区零·青龙脚本）专项脚本适配：
/// 主程序为 OneDragon-Launcher.exe（启动参数 -o -c），配置目录 config/，运行日志 .log/log.txt。
/// </summary>
public sealed class ZenlessZoneZeroOneDragonAdapterPlugin : ISpecializedScriptPlugin
{
    public string Name => "zzzonedragon";

    public string DisplayName => "ZenlessZoneZeroOneDragon";

    public string Description => "Zenless Zone Zero OneDragon 专项脚本实例配置接管（自动推导主程序、启动参数、配置与日志路径）";

    public string Version => "1.0.0";

    public bool IsBuiltIn => false;

    public void Initialize(PluginContext context)
    {
        context.Log("Zenless Zone Zero OneDragon 专项脚本适配已就绪。");
    }

    public void Shutdown()
    {
    }

    public ScriptProfile? Resolve(string rootPath)
    {
        if (string.IsNullOrWhiteSpace(rootPath))
        {
            return null;
        }
        string exe = Path.Combine(rootPath, "OneDragon-Launcher.exe");
        if (!File.Exists(exe))
        {
            return null;
        }
        return new ScriptProfile
        {
            MainExe = exe,
            Args = "-o -c",
            ConfigPath = Path.Combine(rootPath, "config"),
            LogPath = Path.Combine(rootPath, ".log", "log.txt"),
            SuccessMarkers = "关闭游戏成功",
        };
    }
}
