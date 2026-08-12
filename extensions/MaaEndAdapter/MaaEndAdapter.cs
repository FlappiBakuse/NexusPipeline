using NexusPipeline.Plugins;

namespace MaaEndAdapter;

/// <summary>
/// MaaEnd（明日方舟：终末地自动化，MXU 客户端 + agent/go-service + MaaFramework）专项脚本适配：
/// 主程序 MaaEnd.exe（install.yml 将 MXU 的 mxu.exe 重命名为 MaaEnd.exe），启动参数 --autostart --quit-after-run
/// （自启动模式触发自动执行；任务运行完成时进程自动退出，进程退出即运行结束信号），配置目录 config/（便携式），
/// 日志 debug/YYYY-MM-DD-&lt;n&gt;.log（前端写入，当天每次启动 n 自增、启动时自动清理旧日志）。
/// </summary>
public sealed class MaaEndAdapterPlugin : ISpecializedScriptPlugin
{
    public string Name => "maaend";

    public string DisplayName => "MaaEnd";

    public string GameName => "明日方舟：终末地";

    public string Description => "MaaEnd 专项脚本实例配置接管（自动推导主程序、启动参数、配置与日志路径）";

    public string Version => "0.1.0";

    public bool IsBuiltIn => false;

    public void Initialize(PluginContext context)
    {
        context.Log("MaaEnd 专项脚本适配已就绪。");
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
        string exe = Path.Combine(rootPath, "MaaEnd.exe");
        if (!File.Exists(exe))
        {
            return null;
        }
        return new ScriptProfile
        {
            MainExe = exe,
            Args = "--autostart --quit-after-run",
            ConfigPath = Path.Combine(rootPath, "config"),
            // MXU 前端日志文件名 YYYY-MM-DD-<n>.log（首启也带 -1，当天每次启动 n 自增），通配 * 取最新修改 = 当前会话文件；
            // 启动时（autoClearLogsOnLaunch=true）自动删除旧日志文件 → 重试轮新文件不污染旧尝试。
            // 不提供 SuccessMarkers 与 ConfigTemplate：完成判定完全由判断脚本驱动；配置目录型 + MXU 首次启动自动生成完整配置。
            LogPath = Path.Combine(rootPath, "debug", "{YYYY-MM-DD}-*.log"),
            JudgeScript = DefaultJudgeScript,
        };
    }

    /// <summary>
    /// MaaEnd 默认判断脚本（v0.6.1，JavaScript/Jint），基于 MXU 源码 + 7 天真实日志 + 真实配置三重调研：
    /// 1. 读取配置 mxu-MaaEnd.json（input.files 中 Root=config）→ 按 settings.autoStartInstanceId 定位实例
    ///    （按实例 id 匹配，与 MXU --autostart 语义一致）；配置缺失/解析失败/实例定位失败 → 保守无输出
    ///    （宿主超时/进程退出判 failed，防误改配置）。
    /// 2. 已启用任务 = tasks[] 中 enabled === true（与 MXU 运行分发 startTasksForInstance 一致——运行只按 enabled
    ///    过滤，enabledByController 仅 UI 缓存不参与分发，切勿用其过滤，否则与真实执行顺序错位）。
    /// 3. 运行完成判定：最后一个启用任务出现「任务完成: &lt;显示名&gt;」或「任务失败: &lt;显示名&gt;」判定行
    ///    （MXU 按 tasks[] 顺序串行执行；日志行 = [YYYY-MM-DD HH:mm:ss.fff] 任务开始/完成/失败: &lt;显示名&gt;，
    ///    显示名 = customName || interface 任务 label（zh-CN）|| taskName）。
    /// 4. 完成后提取全部「任务失败: X」行（X 为显示名）→ 映射回任务项：
    ///    - 无失败 → success；
    ///    - 有失败且全部可映射 → 改写配置：该实例 tasks[] 全部 enabled=false、失败任务 enabled=true
    ///      （保留 id/customName/optionValues/enabledByController 原样）→ failed + replaceConfigs 触发重试
    ///      （MXU 无运行记录机制、无天然选择性补做，重试只会执行失败任务，运行结束由宿主还原配置）；
    ///    - 有失败但存在无法映射的显示名 → failed 不改写（保守，防误改配置）。
    /// 已知边界：RealTimeTask（🤖实时开荒辅助）永不结束，若为最后一个启用任务会导致运行超时（宿主兜底）；
    /// 映射表为 zh-CN（用户 settings.language=zh-CN）；「跳过任务」仍记「任务完成」（周期不在当天时重试可能直接判成功）；
    /// --quit-after-run 在未实际触发自动执行时可能不退出 → 宿主 stall 超时兜底；与手动运行的 MaaEnd 并发会互相干扰
    /// （同 config/debug 目录，MXU 无并发保护）；同一任务名多条（如多个 ProtocolSpace）无 customName 时显示名相同，
    /// 失败任务映射可能错位 → 保守场景请用 customName 区分。
    /// </summary>
    private const string DefaultJudgeScript = """
        const input = JSON.parse(__NEXUS_INPUT__);
        const log = input.log || "";

        // 任务显示名映射表（zh-CN）：interface.json 任务 label，日志「任务开始/完成/失败: X」的 X 即此显示名
        //（customName 优先于 label）。含旧名别名（CreditShoppingN2/Weapon/SimpleProductionBatchStart，用户配置实际所用）。
        const LABELS = {
          "SellProduct": "🛒售卖产品",
          "AutoStockpile": "📦自动囤货",
          "AutoStockStaple": "🏪购买稳定物资",
          "AutoSell": "💰售卖弹性物资",
          "DijiangRewards": "🎁基建任务",
          "GiftOperator": "🎁赠送干员礼物",
          "VisitFriends": "🤝拜访好友",
          "CreditShopping": "🛍️信用点购物",
          "CreditShoppingN2": "🛍️信用点购物",
          "AutoUseSpMedication": "💊应急理智加强剂",
          "ProtocolSpace": "⚔️协议空间",
          "DailyRewards": "📅日常奖励领取",
          "EnvironmentMonitoring": "🌿环境监测",
          "SeizeDeliveryJobs": "🏍️抢委托送货",
          "DeliveryJobs": "🚚转交委托",
          "GearAssembly": "🔧装备制造",
          "WeaponUpgrade": "🔫升级武器",
          "Weapon": "🔫升级武器",
          "BatchUseDetector": "🧭批量探测器",
          "EssenceFilter": "🔒基质筛选锁定",
          "ResourceRecycleStation": "🦉资源回收站",
          "AutoCollect": "🧺自动采集",
          "AutoEcoFarm": "🌾生态农场",
          "PuzzleSolver": "🧩解拼图",
          "SwitchTeam": "🔄切换编队",
          "ImportBluePrints": "📐一键导入蓝图",
          "AeroSalvage": "🎈浮空回收",
          "AutoEssence": "🎱基质刷取",
          "ClaimSimulationRewards": "📦领取模拟空间奖励",
          "TrialOfSwordmancy": "🗡️选剑演武",
          "PullCountCalculator": "🧮抽数计算",
          "BakerEntry": "💬会话消息嘴替",
          "ReadAllWiki": "📖百科已读",
          "AccountSwitch": "🔑自动切换账号",
          "AndroidOpenGame": "🎮打开游戏",
          "CloseGame": "❌关闭游戏（安卓端）",
          "ItemTransfer": "🐌库存转移",
          "Crafting": "🧪简易制作",
          "SimpleProductionBatch": "🔨批量简易制作",
          "SimpleProductionBatchStart": "🔨批量简易制作",
          "ReceiveProdManual": "🌾简制手册领取",
          "StashBackpack": "🎒存放背包",
          "RealTimeTask": "🤖实时开荒辅助",
          "WebEvent202605": "🎁自动共贺庆典网页活动",
          "BatchAddFriends": "👥批量添加好友",
          "DevTest": "🧪开发测试"
        };

        function displayNameOf(task) {
          return (task.customName || "").trim() || LABELS[task.taskName] || task.taskName;
        }

        // 1. 读取 mxu-MaaEnd.json（Root=config）
        let cfg = null;
        const files = input.files || [];
        for (const f of files) {
          if (f.Path === "mxu-MaaEnd.json" && f.Root === "config") {
            try { cfg = JSON.parse(nexus.readFile(f.Abs)); } catch (e) { cfg = null; }
            break;
          }
        }
        if (!cfg || !cfg.settings || !Array.isArray(cfg.instances)) {
          // 配置缺失/解析失败：保守无输出（宿主超时/进程退出判 failed）
        } else {
          // 2. 按 settings.autoStartInstanceId 定位实例（MXU --autostart 按实例 id 匹配）
          const inst = cfg.instances.find(i => i.id === cfg.settings.autoStartInstanceId);
          if (!inst || !Array.isArray(inst.tasks)) {
            // 实例定位失败：保守无输出
          } else {
            // 3. 已启用任务（与 MXU 运行分发一致：只按 enabled 过滤）
            const enabledTasks = inst.tasks.filter(t => t.enabled === true);
            if (enabledTasks.length === 0) {
              // 无启用任务：保守无输出
            } else {
              // 4. 运行完成判定：最后一个启用任务出现「任务完成/失败: <显示名>」判定行
              const lastName = displayNameOf(enabledTasks[enabledTasks.length - 1]);
              let done = false;
              const lines = log.split(/\r?\n/);
              for (const line of lines) {
                if (line.indexOf("任务完成: " + lastName) >= 0 || line.indexOf("任务失败: " + lastName) >= 0) {
                  done = true;
                  break;
                }
              }
              if (!done) {
                // 尚未运行完成，持续等待（中途崩溃/停止 → 进程退出最终触发无判定 → 宿主判 failed）
              } else {
                // 5. 提取全部失败任务（行扫描「任务失败: X」，X 为显示名）
                const failed = [];
                for (const line of lines) {
                  const pos = line.indexOf("任务失败: ");
                  if (pos >= 0) {
                    const name = line.slice(pos + "任务失败: ".length).trim();
                    if (name && failed.indexOf(name) < 0) failed.push(name);
                  }
                }
                if (failed.length === 0) {
                  console.log(JSON.stringify({ status: "success", reason: "全部任务执行成功" }));
                } else {
                  // 显示名 → 任务项（同一 taskName 多条时需 customName 区分）
                  const failedTasks = [];
                  const unknown = [];
                  for (const name of failed) {
                    const t = enabledTasks.find(x => displayNameOf(x) === name);
                    if (t) failedTasks.push(t); else unknown.push(name);
                  }
                  if (unknown.length > 0) {
                    console.log(JSON.stringify({ status: "failed", reason: "无法识别的失败任务：" + unknown.join("、") + "，为避免误改配置未调整重试" }));
                  } else {
                    // 选择性重试：全部 enabled=false，仅失败任务 enabled=true（其余字段原样保留）
                    const failedIds = failedTasks.map(t => t.id);
                    for (const t of inst.tasks) {
                      t.enabled = failedIds.indexOf(t.id) >= 0;
                    }
                    nexus.writeFile("mxu-MaaEnd.json", JSON.stringify(cfg, null, 2));
                    console.log(JSON.stringify({
                      status: "failed",
                      reason: "任务失败：" + failed.join("、") + "，已调整为仅重试失败任务",
                      replaceConfigs: ["mxu-MaaEnd.json"]
                    }));
                  }
                }
              }
            }
          }
        }
        """;
}
