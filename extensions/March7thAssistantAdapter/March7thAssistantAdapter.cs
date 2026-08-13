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
            JudgeScript = DefaultJudgeScript,
        };
    }

    /// <summary>
    /// March7th Assistant 默认判断脚本（v0.6.1，JavaScript/Jint），基于配置/日志调研 + 源码验证：
    /// - 运行结束关键字「游戏终止：StarRail」（LocalGameController.stop_game 在完整运行收尾时输出）；
    ///   marker 先行判定，天然免疫收尾良性的「发生错误 / 截图失败：没有找到游戏窗口」（game.stop 之后顶层异常处理，
    ///   每天固定出现，均紧跟 marker 之后，不影响判定）。
    /// - marker 出现后扫描任务级失败提示（通知模板经 send_notification_with_screenshot 以 INFO 行写入日志，
    ///   如 2026-08-05 真机的「每日实训未完成」）：命中 → failed 触发宿主重试——未完成任务的 *_timestamp
    ///   仅在达标时保存（Daily.run 500 分达标才记录 last_run_timestamp），重试会自然选择性补做失败任务。
    /// - 未出现 marker 时，匹配「| ERROR | 发生错误」（main.py 顶层 except 的 ErrorOccurred 模板，日志行以该模式开头；
    ///   行内错误如"尝试启动游戏时发生错误："有前缀词，不匹配）→ 快速判定失败，
    ///   跳过无日志 stall 等待；进程退出后由宿主最终触发兜底。
    /// - 已知边界：after_finish 配置为 None 时脚本收尾不调用 shutdown、无 marker，运行会走宿主无日志超时判失败
    ///   （真机 10 天日志 marker 100% 出现，Exit/其他 after_finish 配置无此问题）。
    /// </summary>
    private const string DefaultJudgeScript = """
        const input = JSON.parse(__NEXUS_INPUT__);
        const log = input.log || "";
        const DONE_MARKER = "游戏终止：StarRail";
        const FAILURE_PATTERNS = [
          "每日实训未完成",
          "清体力未完成",
          "模拟宇宙未完成",
          "锄大地未完成",
          "遗器背包已满",
          "领取星琼失败"
        ];

        if (log.indexOf(DONE_MARKER) >= 0) {
          const failedLines = [];
          for (const pattern of FAILURE_PATTERNS) {
            if (log.indexOf(pattern) >= 0) failedLines.push(pattern);
          }
          if (failedLines.length === 0) {
            console.log(JSON.stringify({ status: "success", reason: "全部任务执行成功" }));
          } else {
            console.log(JSON.stringify({ status: "failed", reason: "任务未完成：" + failedLines.join("、") }));
          }
        } else if (/ \| ERROR \| 发生错误/.test(log)) {
          console.log(JSON.stringify({ status: "failed", reason: "运行发生错误，任务未完成" }));
        }
        """;

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
