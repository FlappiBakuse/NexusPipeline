const input = JSON.parse(__NEXUS_INPUT__);
const log = input.log || "";
const DONE_MARKER = "一条龙和配置组任务结束";

if (log.indexOf(DONE_MARKER) < 0) {
  // 尚未运行完成（未出现运行结束关键字），持续等待
} else {
  const ALIAS = { "前往冒险家协会领取奖励": "领取每日奖励" };

  function extractFailedNames(text) {
    const names = [];
    const re = /^(.+?)执行(?:失败|异常)/gm;
    let m;
    while ((m = re.exec(text)) !== null) {
      const raw = (m[1] || "").trim();
      if (raw) names.push(ALIAS[raw] || raw);
    }
    return names;
  }

  const failed = extractFailedNames(log);
  if (failed.length === 0) {
    console.log(JSON.stringify({ status: "success", reason: "全部任务执行成功" }));
  } else {
    const cfgText = nexus.readFile(input.configPath);
    let cfg = null;
    try { cfg = cfgText ? JSON.parse(cfgText) : null; } catch (e) { cfg = null; }
    if (!cfg) {
      console.log(JSON.stringify({ status: "failed", reason: "无法读取或解析 BetterGI 配置文件，已终止重试" }));
    } else {
      // v0.7.6：首次触发时提取初始任务启停映射（TaskEnabledList 全量）写 config-restore.json
      //（宿主收尾按描述还原启停后再同步快照）；跨尝试只写一次（script 目录运行期间不清空）。
      const restoreExists = nexus.listFiles().some(p => p.toLowerCase().endsWith("config-restore.json"));
      if (!restoreExists) {
        const initial = {};
        const enabledMap = cfg.TaskEnabledList || {};
        for (const guid of Object.keys(enabledMap)) initial[guid] = enabledMap[guid] === true;
        nexus.writeFile("config-restore.json", JSON.stringify({
          files: [{
            file: "NexusPipeline.json",
            toggles: [{
              type: "map",
              path: "TaskEnabledList",
              initial: initial
            }]
          }]
        }));
      }
      const defs = cfg.TaskDefinitions || {};
      const nameToGuid = {};
      for (const guid of Object.keys(defs)) nameToGuid[defs[guid]] = guid;
      const failedGuids = [];
      const unknown = [];
      for (const name of failed) {
        const guid = nameToGuid[name];
        if (guid) failedGuids.push(guid); else unknown.push(name);
      }
      if (unknown.length > 0) {
        console.log(JSON.stringify({ status: "failed", reason: "无法识别失败任务：" + unknown.join("、") + "，为避免误改配置已终止重试" }));
      } else {
        const enabled = cfg.TaskEnabledList || {};
        for (const guid of Object.keys(enabled)) enabled[guid] = failedGuids.indexOf(guid) >= 0;
        cfg.NextTaskId = "";
        nexus.writeFile("NexusPipeline.json", JSON.stringify(cfg, null, 2));
        console.log(JSON.stringify({ status: "failed", reason: "任务执行失败：" + failed.join("、") + "，已调整为仅重试失败任务", replaceConfigs: ["NexusPipeline.json"] }));
      }
    }
  }
}
