using NexusPipeline.Plugins;

namespace BetterGIAdapter;

/// <summary>BetterGenshinImpact 专项脚本适配：根据脚本根目录推导主程序/配置/日志/自启动参数。</summary>
public sealed class BetterGenshinImpactAdapter : ISpecializedScriptPlugin
{
    public string Name => "bettergi";

    public string DisplayName => "BetterGI";

    public string Description => "BetterGenshinImpact 专项脚本实例配置接管（自动推导主程序、配置、日志路径与自启动参数）";

    public string Version => "1.0.0";

    public bool IsBuiltIn => false;

    public void Initialize(PluginContext context)
    {
        context.Log("BetterGI 专项脚本适配已就绪。");
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
        string exe = Path.Combine(rootPath, "BetterGI.exe");
        if (!File.Exists(exe))
        {
            return null;
        }
        return new ScriptProfile
        {
            MainExe = exe,
            Args = "--startOneDragon",
            ConfigPath = Path.Combine(rootPath, "User", "OneDragon", "默认配置.json"),
            LogPath = Path.Combine(rootPath, "log", "better-genshin-impact{YYYYMMDD}.log"),
            SuccessMarkers = "一条龙和配置组任务结束",
        };
    }
}
