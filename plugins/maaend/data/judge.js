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
  // 2. 按 settings.autoStartInstanceId 定位实例（MXU --autostart 按实例 id 匹配）；
  // v0.7.5（台账外）：模板直出配置无 autoStartInstanceId 字段（MXU 运行分发用），回退 lastActiveInstanceId，
  // 再回退「唯一实例」（模板为单实例场景兜底）；多实例且无标记时保持保守无输出。
  let inst = null;
  const autoId = cfg.settings.autoStartInstanceId;
  const lastId = cfg.settings.lastActiveInstanceId;
  if (autoId) {
    inst = cfg.instances.find(i => i.id === autoId) || null;
  }
  if (!inst && lastId) {
    inst = cfg.instances.find(i => i.id === lastId) || null;
  }
  if (!inst && cfg.instances.length === 1) {
    inst = cfg.instances[0];
  }
  if (!inst || !Array.isArray(inst.tasks)) {
    // 实例定位失败：保守无输出
  } else {
    // v0.7.6：首次触发时提取初始任务启停映射写 config-restore.json（宿主收尾按描述还原启停后再同步快照，
    // 插队文件以「初始启停 + 运行后计数/其他字段」写入 store）；跨尝试只写一次（script 目录运行期间不清空）。
    const restoreExists = nexus.listFiles().some(p => p.toLowerCase().endsWith("config-restore.json"));
    if (!restoreExists) {
      const initial = {};
      for (const t of inst.tasks) {
        if (t.id) initial[t.id] = t.enabled === true;
      }
      nexus.writeFile("config-restore.json", JSON.stringify({
        files: [{
          file: "mxu-MaaEnd.json",
          toggles: [{
            type: "array",
            path: "instances[id=" + inst.id + "].tasks",
            keyField: "id",
            enabledField: "enabled",
            initial: initial
          }]
        }]
      }));
    }
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
