using NexusPipeline.Plugins;

namespace March7thAssistantAdapter;

/// <summary>
/// March7th Assistant（崩坏：星穹铁道自动化）专项脚本适配。
/// 管理端/执行端分离：March7th Launcher.exe 仅用于编辑配置（不可传参、不可自启脚本）；
/// 真正的自动化主程序是 March7th Assistant.exe。本插件推导：
/// MainExe = Launcher（编辑配置用）、Args 首项 = Assistant 的显式相对路径（运行时启动目标）、ConfigPath/LogPath 按官方目录结构。
/// </summary>
public sealed class March7thAssistantAdapterPlugin : ISpecializedScriptPlugin
{
    public string Name => "march7th";

    public string DisplayName => "March7thAssistant";

    public string GameName => "崩坏：星穹铁道";

    public string Description => "March7th Assistant 专项脚本实例配置接管（Launcher 编辑配置 / Assistant 运行脚本，自动推导主程序、启动目标、配置与日志路径）";

    public string Version => "0.1.0";

    public bool IsBuiltIn => false;

    public void Initialize(PluginContext context)
    {
        context.Log("March7th Assistant 专项脚本适配已就绪。");
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
        string launcher = Path.Combine(rootPath, "March7th Launcher.exe");
        string assistant = Path.Combine(rootPath, "March7th Assistant.exe");
        if (File.Exists(assistant))
        {
            return Build(rootPath, File.Exists(launcher) ? launcher : assistant, ".\\" + Path.GetFileName(assistant));
        }
        string? parentAssistant = FindAssistantUpward(rootPath);
        if (parentAssistant is not null && File.Exists(launcher))
        {
            return Build(rootPath, launcher, MakeRelativePath(rootPath, parentAssistant));
        }
        return null;
    }

    private static ScriptProfile Build(string rootPath, string mainExe, string args)
    {
        return new ScriptProfile
        {
            MainExe = mainExe,
            Args = args,
            ConfigPath = Path.Combine(rootPath, "config.yaml"),
            LogPath = Path.Combine(rootPath, "logs", "{YYYY-MM-DD}.log"),
            SuccessMarkers = "游戏终止：StarRail",
        };
    }

    private static string? FindAssistantUpward(string rootPath)
    {
        string? dir = Directory.GetParent(rootPath)?.FullName;
        for (int depth = 0; dir is not null && depth < 4; depth++)
        {
            string candidate = Path.Combine(dir, "March7th Assistant.exe");
            if (File.Exists(candidate))
            {
                return candidate;
            }
            dir = Directory.GetParent(dir)?.FullName;
        }
        return null;
    }

    private static string MakeRelativePath(string fromDir, string toFile)
    {
        string from = fromDir.EndsWith("\\", StringComparison.Ordinal) ? fromDir : fromDir + "\\";
        string rel = Uri.UnescapeDataString(new Uri(from).MakeRelativeUri(new Uri(toFile)).ToString()).Replace('/', '\\');
        return rel.StartsWith(".\\", StringComparison.Ordinal) || rel.StartsWith("..\\", StringComparison.Ordinal) ? rel : ".\\" + rel;
    }
}
