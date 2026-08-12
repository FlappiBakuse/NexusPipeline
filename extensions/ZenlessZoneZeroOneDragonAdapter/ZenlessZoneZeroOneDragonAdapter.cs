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

    public string GameName => "绝区零";

    public string Description => "Zenless Zone Zero OneDragon 专项脚本实例配置接管（自动推导主程序、启动参数、配置与日志路径）";

    public string Version => "0.1.0";

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
            JudgeScript = DefaultJudgeScript,
        };
    }

    /// <summary>
    /// ZenlessZoneZeroOneDragon 默认判断脚本（v0.6.1，JavaScript/Jint），基于配置/日志调研 + 源码验证：
    /// - 运行结束关键字「关闭游戏成功」（pc_controller_base.close_game，一条龙收尾按 one_dragon.yml 的
    ///   after_done=关闭游戏 执行）或「暂停运行」（application_run_context 运行上下文收尾必现，兜底
    ///   after_done 非关闭游戏的配置）→ 判定运行结束。
    /// - 结束出现后提取全部「指令[ X ] 执行失败 返回状态 Y」（operation.py 677 ERROR 行）并去重，
    ///   过滤已知良性噪声（等待大世界画面=瞬时重试、通知=推送通道环境噪声，真机日志每天出现）：
    ///   无失败 → success；有失败 → success + notifyText 提示失败应用（应用级失败不中断一条龙流程，
    ///   宿主 FinalStatus 因日志含「失败」自动落 partial，历史页可见）。
    /// - 未出现结束关键字时静默等待，由宿主无日志超时/进程退出兜底判失败。
    /// </summary>
    private const string DefaultJudgeScript = """
        const input = JSON.parse(__NEXUS_INPUT__);
        const log = input.log || "";
        const DONE_MARKERS = ["关闭游戏成功", "暂停运行"];
        const IGNORE_APPS = ["等待大世界画面", "通知"];

        let done = false;
        for (const marker of DONE_MARKERS) {
          if (log.indexOf(marker) >= 0) {
            done = true;
            break;
          }
        }
        if (!done) {
          // 尚未运行结束（未出现结束关键字），持续等待
        } else {
          const failed = [];
          const re = /指令\[ (.+?) \] 执行失败 返回状态/g;
          let m;
          while ((m = re.exec(log)) !== null) {
            const name = (m[1] || "").trim();
            if (name && IGNORE_APPS.indexOf(name) < 0 && failed.indexOf(name) < 0) {
              failed.push(name);
            }
          }
          if (failed.length === 0) {
            console.log(JSON.stringify({ status: "success", reason: "全部应用执行成功" }));
          } else {
            console.log(JSON.stringify({
              status: "success",
              reason: "一条龙运行完成，但部分应用执行失败",
              notifyText: "本次运行有应用执行失败：" + failed.join("、")
            }));
          }
        }
        """;
}
