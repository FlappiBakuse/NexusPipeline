import fs from "node:fs";
import path from "node:path";
import { test, expect } from "@playwright/test";
import { baseUrl, PING_GAME, runtimeDir, makeScriptDir, createScript, api, waitFor, waitNoRunning, waitAbsent, latestHistoryDay, localDate } from "./helpers.mjs";

function scriptHistoryLog(scriptId) {
  const historyRoot = path.join(runtimeDir, "history");
  if (!fs.existsSync(historyRoot)) return "";
  const dirs = fs.readdirSync(historyRoot).filter(d => /^\d{4}-\d{2}-\d{2}$/.test(d)).sort().reverse();
  for (const dir of dirs) {
    const files = fs.readdirSync(path.join(historyRoot, dir)).filter(f => f.endsWith(".json")).sort();
    for (const f of files) {
      const rec = JSON.parse(fs.readFileSync(path.join(historyRoot, dir, f), "utf8").replace(/^\uFEFF/, ""));
      if (rec.ScriptInstanceId === scriptId) {
        const logFile = path.join(historyRoot, dir, f.replace(".json", "-1.log"));
        return fs.existsSync(logFile) ? fs.readFileSync(logFile, "utf8").replace(/^\uFEFF/, "") : "";
      }
    }
  }
  return "";
}

test("日志路径格式：严格匹配 / 无条目超时失败 / 已有日志忽略 / 通配轮换", async () => {
  const logRoot = path.join(runtimeDir, "lp-logs");
  fs.rmSync(logRoot, { recursive: true, force: true });
  fs.mkdirSync(path.join(logRoot, "b"), { recursive: true });
  fs.mkdirSync(path.join(logRoot, "c"), { recursive: true });

  const ping = "C:\\Windows\\System32\\PING.EXE";
  const lpCfg = path.join(runtimeDir, "lp-cfg");
  fs.rmSync(lpCfg, { recursive: true, force: true });
  fs.mkdirSync(lpCfg, { recursive: true });

  const a = await api("POST", "/api/scripts", {
    name: "无条目脚本", rootPath: runtimeDir, mainExe: ping, args: "-n 90 127.0.0.1",
    configPath: lpCfg, logPath: path.join(logRoot, "a", "run-{YYYY-MM-DD}.log").replace(/\\/g, "\\\\"), gameExe: PING_GAME,
    maxAttempts: 1, logStallTimeoutMinutes: 1, totalTimeoutMinutes: 10,
  });
  const aid = (await a.json()).id;
  await api("POST", `/api/scripts/${aid}/users`, { name: "默认", enabled: true });
  await api("POST", "/api/dispatch/script", { scriptId: aid, mode: "manual" });
  expect(await waitNoRunning(120000), "无条目脚本运行结束（无日志条目超时失败，真实档约 1 分钟/加速档 1 秒）").toBeTruthy();
  const hist = await (await fetch(baseUrl + "api/history?days=7")).json();
  const rec = hist.filter(h => h.scriptInstanceId === aid).at(-1);
  expect(rec && rec.finalStatus === "failed" && (rec.resultDetail || "").includes("未产生日志条目"), "启动后无日志条目等待无更新超时后失败（FinalStatus=failed）").toBeTruthy();
  await api("DELETE", "/api/scripts/" + aid);

  const bLog = path.join(logRoot, "b", "run-" + localDate() + ".log");
  fs.writeFileSync(bLog, "OLD-CONTENT-PREEXISTING\r\n");
  const bBat = path.join(runtimeDir, "lp-b.bat");
  fs.writeFileSync(bBat, "@echo off\r\necho [SCRIPT] NEW-ENTRY-BRANDNEW >> \"" + bLog + "\"\r\necho [SCRIPT] 任务完成 >> \"" + bLog + "\"\r\nexit /b 0\r\n", "ascii");
  const b = await api("POST", "/api/scripts", {
    name: "忽略旧日志脚本", rootPath: runtimeDir, mainExe: bBat.replace(/\\/g, "\\\\"),
    configPath: lpCfg, logPath: path.join(logRoot, "b", "run-{YYYY-MM-DD}.log").replace(/\\/g, "\\\\"), gameExe: PING_GAME,
    maxAttempts: 1, logStallTimeoutMinutes: 5, totalTimeoutMinutes: 10, successKeywords: "任务完成",
  });
  const bid = (await b.json()).id;
  await api("POST", `/api/scripts/${bid}/users`, { name: "默认", enabled: true });
  await api("POST", "/api/dispatch/script", { scriptId: bid, mode: "manual" });
  expect(await waitNoRunning(60000), "忽略旧日志脚本运行结束").toBeTruthy();
  const sl = scriptHistoryLog(bid);
  expect(sl.includes("NEW-ENTRY-BRANDNEW"), "历史日志含运行期间新条目").toBeTruthy();
  expect(!sl.includes("OLD-CONTENT-PREEXISTING"), "历史日志忽略运行前已有内容").toBeTruthy();
  await api("DELETE", "/api/scripts/" + bid);

  const cDir = path.join(logRoot, "c");
  const cOld = path.join(cDir, "run-" + localDate() + "-1.log");
  fs.writeFileSync(cOld, "OLD-ROUND\r\n");
  const cBat = path.join(runtimeDir, "lp-c.bat");
  fs.writeFileSync(cBat, [
    "@echo off",
    "ping 127.0.0.1 -n 3 >nul",
    "del /q \"" + cOld + "\" 2>nul",
    "echo [SCRIPT] ROUND2-NEW >> \"" + path.join(cDir, "run-" + localDate() + "-2.log") + "\"",
    "echo [SCRIPT] 任务完成 >> \"" + path.join(cDir, "run-" + localDate() + "-2.log") + "\"",
    "ping 127.0.0.1 -n 2 >nul",
    "exit /b 0",
  ].join("\r\n"), "ascii");
  const c = await api("POST", "/api/scripts", {
    name: "通配轮换脚本", rootPath: runtimeDir, mainExe: cBat.replace(/\\/g, "\\\\"),
    configPath: lpCfg, logPath: path.join(cDir, "run-{YYYY-MM-DD-*}.log").replace(/\\/g, "\\\\"), gameExe: PING_GAME,
    maxAttempts: 1, logStallTimeoutMinutes: 5, totalTimeoutMinutes: 10, successKeywords: "任务完成",
  });
  const cid = (await c.json()).id;
  await api("POST", `/api/scripts/${cid}/users`, { name: "默认", enabled: true });
  await api("POST", "/api/dispatch/script", { scriptId: cid, mode: "manual" });
  expect(await waitNoRunning(60000), "通配轮换脚本运行结束").toBeTruthy();
  const cl = scriptHistoryLog(cid);
  expect(cl.includes("ROUND2-NEW") && !cl.includes("OLD-ROUND"), "通配格式跟踪到轮换后的新文件（忽略旧轮次内容）").toBeTruthy();
  await api("DELETE", "/api/scripts/" + cid);
  fs.rmSync(logRoot, { recursive: true, force: true });
});

test("调度队列：新建（定时+任务）/ 编辑 / 删除", async ({ page }) => {
  const qDir = makeScriptDir("queue");
  const created = await createScript({ name: "队列用脚本", rootPath: qDir.root, mainExe: qDir.main, configPath: qDir.cfg, logPath: qDir.log, maxAttempts: 3, logStallTimeoutMinutes: 5, totalTimeoutMinutes: 120 });
  expect(created.ok, "通过 API 预创建队列用脚本").toBeTruthy();
  await page.goto(baseUrl + "#/queues", { waitUntil: "domcontentloaded" });
  await page.waitForSelector("h2");
  await page.click("text=新建调度队列");
  await page.waitForSelector(".modal-mask");
  await page.fill("#qm-name", "测试队列A");
  const modeVal = await page.$eval("#qm-mode", el => el.value);
  expect(modeVal === "none", "新建队列默认自动运行方式为「不运行」").toBeTruthy();
  const modeOpts = await page.$$eval("#qm-mode option", els => els.map(e => e.textContent));
  expect(modeOpts.length === 3 && modeOpts[0] === "不运行", "自动运行方式含「不运行」选项且置顶").toBeTruthy();

  const dayState = await page.$eval(".days-btn-grid", el => {
    const inputs = Array.from(el.querySelectorAll("[data-ts-days]"));
    return { count: inputs.length, pressed: inputs.filter(input => input.getAttribute("aria-pressed") === "true").length };
  });
  expect(dayState.count === 7, "执行周期为整体星期按钮组（7 项）").toBeTruthy();
  expect(dayState.pressed === 5, "执行周期默认选中工作日").toBeTruthy();

  await page.click("text=+ 添加任务");
  await page.selectOption('[data-task-idx="0"]', { label: "队列用脚本" });
  await page.click(".modal button:has-text('保存')");
  await page.waitForSelector(".modal-mask", { state: "detached", timeout: 5000 });
  await page.waitForFunction(() => document.body.textContent.includes("测试队列A"), null, { timeout: 5000 });
  expect(true, "新建后卡片显示队列名称").toBeTruthy();
  expect(await page.$(".card .script-grid .queue-card"), "队列卡片位于外层大卡片网格内（与脚本卡片同构）").toBeTruthy();

  const qm = await api("POST", "/api/queues", { name: "缺省模式队列", autoRunMode: "invalid", completionAction: "none", timeSets: [], tasks: [{ id: "", index: 0, scriptInstanceId: created.id }] });
  expect(qm.ok, "POST 非法 autoRunMode 成功（归一化）").toBeTruthy();
  const qmId = (await qm.json()).id;
  const qmList = await (await fetch(baseUrl + "api/queues")).json();
  const qmGot = qmList.find(q => q.id === qmId);
  expect(qmGot && qmGot.autoRunMode === "none", "非法 autoRunMode 归一为 none").toBeTruthy();
  await api("DELETE", "/api/queues/" + qmId);

  await page.click('[data-action="edit-queue"]');
  await page.waitForSelector("#qm-name");
  await page.fill("#qm-name", "测试队列A-改");
  await page.click(".modal button:has-text('保存')");
  await page.waitForSelector(".modal-mask", { state: "detached", timeout: 5000 });
  await page.waitForFunction(() => document.body.textContent.includes("测试队列A-改"), null, { timeout: 5000 });
  expect(true, "编辑后队列名称已更新").toBeTruthy();

  const qCard = await page.$('[data-testid="queue-card"]');
  expect(!!qCard && !!await qCard.$("img.script-ico"), "队列卡片左侧显示首个脚本实例图标").toBeTruthy();
  const qBody = await page.textContent('[data-testid="queue-card"]');
  expect(!qBody.includes("定时：") && !qBody.includes("任务："), "队列卡片移除定时与任务信息行").toBeTruthy();
  expect(!qBody.includes("开始运行"), "不运行队列无运行时间提示").toBeTruthy();

  const sq = await api("POST", "/api/queues", { name: "启动队列", autoRunMode: "startup", completionAction: "none", timeSets: [], tasks: [{ id: "", index: 0, scriptInstanceId: created.id }] });
  expect(sq.ok, "API 创建启动时运行队列").toBeTruthy();
  await page.goto(baseUrl + "#/dashboard", { waitUntil: "domcontentloaded" });
  await page.goto(baseUrl + "#/queues", { waitUntil: "domcontentloaded" });
  await page.waitForFunction(() => document.body.textContent.includes("启动队列"), null, { timeout: 5000 });
  expect((await page.textContent("body")).includes("将在下次启动开始运行"), "启动时运行队列显示「将在下次启动开始运行」").toBeTruthy();
  const sqList = await (await fetch(baseUrl + "api/queues")).json();
  const sqGot = sqList.find(q => q.name === "启动队列");
  expect(sqGot && sqGot.nextTrigger === null, "启动时运行队列 nextTrigger 为 null").toBeTruthy();
  await api("DELETE", "/api/queues/" + sqGot.id);

  const tq = await api("POST", "/api/queues", { name: "定时倒计时队列", autoRunMode: "scheduled", completionAction: "none", timeSets: [{ id: "", enabled: true, days: [0, 1, 2, 3, 4, 5, 6], time: "23:59" }], tasks: [{ id: "", index: 0, scriptInstanceId: created.id }] });
  expect(tq.ok, "API 创建定时运行队列").toBeTruthy();
  await page.goto(baseUrl + "#/dashboard", { waitUntil: "domcontentloaded" });
  await page.goto(baseUrl + "#/queues", { waitUntil: "domcontentloaded" });
  await page.waitForFunction(() => document.body.textContent.includes("定时倒计时队列"), null, { timeout: 5000 });
  await page.waitForFunction(() => { const el = document.querySelector('[data-testid="queue-card"] .queue-next'); return el && /\d{2}:\d{2}:\d{2}后开始/.test(el.textContent); }, null, { timeout: 5000 });
  expect(true, "定时运行队列显示倒计时（xx:xx:xx后开始）").toBeTruthy();
  expect((await page.textContent('[data-testid="queue-card"] [data-testid="queue-notify"]')).includes("队列级通知：关"), "定时队列卡片显示「队列级通知：关」").toBeTruthy();
  const tqList = await (await fetch(baseUrl + "api/queues")).json();
  const tqGot = tqList.find(q => q.name === "定时倒计时队列");
  expect(tqGot && tqGot.nextTrigger, "定时运行队列 API 返回 nextTrigger").toBeTruthy();
  await api("DELETE", "/api/queues/" + tqGot.id);

  await page.click('[data-action="delete-queue"]');
  await page.waitForSelector(".modal-mask .modal", { timeout: 5000 });
  expect((await page.textContent(".modal")).includes("确定删除调度队列"), "删除队列弹出确认卡片（含确定/取消）").toBeTruthy();
  await page.click('[data-action="confirm-delete-queue"]');
  await waitAbsent(page, "测试队列A-改");
  expect(!(await page.textContent("body")).includes("测试队列A-改"), "删除后卡片消失").toBeTruthy();
});

test("定时列表：完全一致合并 toast + 后端兜底去重 + 间隔<10分钟确认卡片", async ({ page }) => {
  const mDir = makeScriptDir("timeset");
  const created = await createScript({ name: "定时合并用脚本", rootPath: mDir.root, mainExe: mDir.main, configPath: mDir.cfg, logPath: mDir.log });
  expect(created.ok, "创建定时合并用脚本").toBeTruthy();
  const sid = created.id;

  const dup = await api("POST", "/api/queues", {
    name: "合并兜底队列", autoRunMode: "scheduled", completionAction: "none",
    timeSets: [
      { id: "", enabled: true, days: [1, 2, 3], time: "08:00" },
      { id: "", enabled: true, days: [3, 1, 2], time: "08:00" },
      { id: "", enabled: false, days: [1, 2, 3], time: "08:00" },
    ],
    tasks: [{ id: "", index: 0, scriptInstanceId: sid }],
  });
  expect(dup.ok, "API 提交含重复定时列表成功").toBeTruthy();
  const dupId = (await dup.json()).id;
  let qList = await (await fetch(baseUrl + "api/queues")).json();
  const dupGot = qList.find(q => q.id === dupId);
  expect(dupGot && dupGot.timeSets.length === 2, "后端兜底去重：完全一致定时合并为 1 条（天数乱序也视为一致，保留启用/禁用各一）").toBeTruthy();
  await api("DELETE", "/api/queues/" + dupId);

  await page.goto(baseUrl + "#/dashboard", { waitUntil: "domcontentloaded" });
  await page.goto(baseUrl + "#/queues", { waitUntil: "domcontentloaded" });
  await page.waitForSelector("h2");
  await page.click("button:has-text('新建调度队列')");
  await page.waitForSelector("#qm-name");
  await page.fill("#qm-name", "合并UI队列");
  await page.click("text=+ 添加任务");
  await page.selectOption('[data-task-idx="0"]', { label: "定时合并用脚本" });
  await page.click("button:has-text('+ 添加定时')");
  expect((await page.$$eval(".timeset-card", els => els.length)) === 2, "弹窗中已有两条完全相同定时列表（默认复制）").toBeTruthy();
  await page.click(".modal button:has-text('保存')");
  await page.waitForSelector(".modal-mask", { state: "detached", timeout: 5000 });
  expect((await page.textContent("#toast")).includes("完全一致的定时列表已被合并"), "保存后弹出合并 toast（完全一致的定时列表已被合并）").toBeTruthy();
  qList = await (await fetch(baseUrl + "api/queues")).json();
  const uiQ = qList.find(q => q.name === "合并UI队列");
  expect(uiQ && uiQ.timeSets.length === 1, "合并后队列仅保留 1 条定时").toBeTruthy();
  await api("DELETE", "/api/queues/" + uiQ.id);

  await page.click("button:has-text('新建调度队列')");
  await page.waitForSelector("#qm-name");
  await page.fill("#qm-name", "间隔确认队列");
  await page.selectOption("#qm-mode", "scheduled");
  await page.click("text=+ 添加任务");
  await page.selectOption('[data-task-idx="0"]', { label: "定时合并用脚本" });
  await page.click("button:has-text('+ 添加定时')");
  await page.fill('[data-ts-time="0"]', "08:00");
  await page.fill('[data-ts-time="1"]', "08:05");
  await page.click(".modal button:has-text('保存')");
  await page.waitForSelector(".modal-mask .modal", { timeout: 5000 });
  const confirmText = await page.textContent(".modal");
  expect(confirmText.includes("间隔低于10分钟") && confirmText.includes("确定"), "间隔<10分钟时弹出确认卡片（含警告文案与确定按钮）").toBeTruthy();
  await page.click('[data-action="cancel-timegap"]');
  await page.waitForFunction(() => document.querySelector("#qm-name"), null, { timeout: 5000 });
  qList = await (await fetch(baseUrl + "api/queues")).json();
  expect(!qList.some(q => q.name === "间隔确认队列"), "确认卡片取消后队列未保存（弹窗保留可继续编辑）").toBeTruthy();
  await page.click(".modal button:has-text('保存')");
  await page.waitForSelector(".modal-mask .modal", { timeout: 5000 });
  await page.click('[data-action="confirm-timegap-save"]');
  await page.waitForSelector(".modal-mask", { state: "detached", timeout: 5000 });
  qList = await (await fetch(baseUrl + "api/queues")).json();
  const gapQ = qList.find(q => q.name === "间隔确认队列");
  expect(gapQ && gapQ.timeSets.length === 2, "确认卡片确定后队列已保存（两条定时保留）").toBeTruthy();
  await api("DELETE", "/api/queues/" + gapQ.id);

  await api("DELETE", "/api/scripts/" + sid);
});

test("调度中心执行 + 历史记录详情", async ({ page }) => {
  const dDir = makeScriptDir("dispatch");
  // 加速档下脚本瞬时退出会使「正在运行任务卡片」窗口小于 dispatch 面板 2 秒轮询周期而观测不到；
  // 改用固定时长伪脚本（两档一致约 3 秒），保证运行窗口内轮询可见（日志目录仍为空 → 未找到日志文件 → 失败详情断言不变）。
  fs.writeFileSync(path.join(dDir.root, "nexustest-dispatch.bat"), "@echo off\r\nping -n 4 127.0.0.1 >nul\r\nexit /b 0\r\n", "ascii");
  const created = await createScript({ name: "跑批脚本", rootPath: dDir.root, mainExe: dDir.main, configPath: dDir.cfg, logPath: dDir.log, maxAttempts: 3, logStallTimeoutMinutes: 5, totalTimeoutMinutes: 120 });
  expect(created.ok, "通过 API 预创建调度中心用脚本").toBeTruthy();

  await page.goto(baseUrl + "#/dispatch", { waitUntil: "domcontentloaded" });
  await page.waitForSelector("#dc-script");
  await page.selectOption("#dc-script", { label: "跑批脚本" });
  await page.click("button:has-text('执行')");
  await page.waitForFunction(() => !!document.querySelector("#dispatch-running .list-item"), null, { timeout: 8000 });
  expect(true, "调度中心出现正在运行的任务卡片（执行已受理）").toBeTruthy();
  // 历史页无轮询（仅进入时加载一次）：先等运行结束（历史已落盘）再进入，避免加载时机竞态。
  expect(await waitNoRunning(60000), "跑批脚本运行结束").toBeTruthy();
  await new Promise(r => setTimeout(r, 500));

  await page.click('nav a[href="#/history"]');
  await page.waitForSelector("h2");
  await page.waitForFunction(() => document.body.textContent.includes("跑批脚本"), null, { timeout: 10000 });
  const body = await page.textContent("body");
  expect(body.includes("跑批脚本"), "历史记录出现新运行记录").toBeTruthy();
  await page.click('[data-action="history-detail"]');
  await page.waitForSelector(".modal-mask", { timeout: 5000 });
  const modalText = await page.textContent(".modal");
  expect(modalText.includes("运行详情") && modalText.includes("失败"), "历史详情弹窗显示运行失败详情").toBeTruthy();
  expect(modalText.includes("脚本日志"), "历史详情弹窗含脚本日志区块").toBeTruthy();
  await page.click(".modal button:has-text('关闭')");
});

test("调度中心：运行中任务实时日志滚动（重试后成功 → 部分失败）", async ({ page }) => {
  const batPath = path.join(runtimeDir, "live.bat");
  const logPath = path.join(runtimeDir, "logs", "live.log");
  const flagPath = path.join(runtimeDir, "logs", "first.done");
  fs.mkdirSync(path.dirname(logPath), { recursive: true });
  fs.rmSync(logPath, { force: true });
  fs.rmSync(flagPath, { force: true });
  fs.writeFileSync(batPath, [
    "@echo off",
    "setlocal EnableDelayedExpansion",
    "set LOG=" + logPath,
    "set FLAG=" + flagPath,
    "echo [SCRIPT] attempt started >> %LOG%",
    "if exist %FLAG% goto success",
    "echo x > %FLAG%",
    "echo [SCRIPT] ERROR something went wrong >> %LOG%",
    "echo [CONSOLE] console: attempt failed",
    "exit /b 1",
    ":success",
    "ping 127.0.0.1 -n 4 >nul",
    "echo [SCRIPT] ALL-DONE-MARKER >> %LOG%",
    "echo [CONSOLE] console: all done",
    "exit /b 0",
  ].join("\r\n"), "ascii");
  const logCfg = path.join(runtimeDir, "log-cfg");
  fs.rmSync(logCfg, { recursive: true, force: true });
  fs.mkdirSync(logCfg, { recursive: true });
  const created = await createScript({ name: "日志脚本", rootPath: runtimeDir, mainExe: batPath, configPath: logCfg, logPath, maxAttempts: 2, logStallTimeoutMinutes: 5, totalTimeoutMinutes: 10, successKeywords: "ALL-DONE-MARKER" });
  expect(created.ok, "创建日志测试脚本").toBeTruthy();

  await page.goto(baseUrl + "#/dispatch", { waitUntil: "domcontentloaded" });
  await page.waitForSelector("#dc-script");
  await page.selectOption("#dc-script", { label: "日志脚本" });
  await page.click("button:has-text('执行')");
  await page.waitForSelector(".run-log", { timeout: 10000 });
  await page.waitForFunction(() => { const el = document.querySelector(".run-log"); return el && el.textContent.includes("SCRIPT"); }, null, { timeout: 10000 });
  const logText = await page.textContent(".run-log");
  expect(logText.includes("SCRIPT"), "日志框实时显示脚本输出（首行：" + (logText.split("\n")[0] || "") + "）").toBeTruthy();
  const scrolled = await page.evaluate(() => {
    const el = document.querySelector(".run-log");
    return el.scrollHeight - el.scrollTop - el.clientHeight < 10;
  });
  expect(scrolled, "日志框自动滚动到底部").toBeTruthy();
  await page.waitForFunction(() => !document.querySelector(".run-log"), null, { timeout: 25000 });
  expect(true, "运行结束后日志框随任务消失").toBeTruthy();
});

test("历史文件夹：.json 纯状态 + 按尝试分批 .log 标号 + 脚本日志与控制台分离 + partial 判定", async () => {
  const historyRoot = path.join(runtimeDir, "history");
  const dayDir = path.join(historyRoot, latestHistoryDay());
  await waitFor(() => fs.existsSync(dayDir), 8000);
  expect(fs.existsSync(dayDir), "history/YYYY-MM-DD 目录存在").toBeTruthy();
  const jsons = fs.existsSync(dayDir) ? fs.readdirSync(dayDir).filter(f => f.endsWith(".json")) : [];
  const logs = fs.existsSync(dayDir) ? fs.readdirSync(dayDir).filter(f => f.endsWith(".log")) : [];
  expect(jsons.length >= 1, "存在 .json 状态文件（" + jsons.length + " 个）").toBeTruthy();
  expect(logs.length >= 1, "存在按尝试分批的 .log 日志文件（" + logs.length + " 个）").toBeTruthy();
  expect(jsons.some(f => logs.includes(f.replace(".json", "-1.log"))), "尝试 1 日志与 .json 配对（-1.log 分批标号）").toBeTruthy();
  expect(jsons.some(f => logs.includes(f.replace(".json", "-2.log"))), "尝试 2 日志与 .json 配对（-2.log 分批标号）").toBeTruthy();
  expect(!logs.some(f => f.endsWith(".console.log")), "不再生成 .console.log（控制台输出不落盘）").toBeTruthy();

  const newestJson = jsons[jsons.length - 1];
  const readText = p => fs.readFileSync(p, "utf8").replace(/^\uFEFF/, "");
  const record = JSON.parse(readText(path.join(dayDir, newestJson)));
  expect(record.FinalStatus === "partial", "重试后成功判定为部分失败（FinalStatus=" + record.FinalStatus + "）").toBeTruthy();
  expect(record.Attempts === 2, "重试次数记录为 2（Attempts=" + record.Attempts + "）").toBeTruthy();
  expect(record.LogFile === newestJson, "json 记录 LogFile 引用").toBeTruthy();
  expect(record.AttemptDetails && record.AttemptDetails.length === 2, "尝试详情 2 条（AttemptDetails 长度）").toBeTruthy();
  expect(record.AttemptDetails[0].LogFile === newestJson.replace(".json", "-1.log"), "尝试 1 记录 LogFile=-1.log").toBeTruthy();
  expect(record.AttemptDetails[1].LogFile === newestJson.replace(".json", "-2.log"), "尝试 2 记录 LogFile=-2.log").toBeTruthy();
  expect(!("LogTail" in record.AttemptDetails[0]) && !("OutputTail" in record.AttemptDetails[0]), "json 尝试详情不含日志内容（纯状态，无 LogTail/OutputTail）").toBeTruthy();

  const attempt1Log = readText(path.join(dayDir, newestJson.replace(".json", "-1.log")));
  expect(attempt1Log.includes("[SCRIPT]"), "尝试 1 日志文件含脚本日志内容").toBeTruthy();
  expect(!attempt1Log.includes("[CONSOLE]"), "尝试 1 日志文件不含控制台输出（分离）").toBeTruthy();
  const attempt2Log = readText(path.join(dayDir, newestJson.replace(".json", "-2.log")));
  expect(attempt2Log.includes("[SCRIPT]"), "尝试 2 日志文件含脚本日志内容").toBeTruthy();
  expect(attempt2Log.includes("ALL-DONE-MARKER"), "尝试 2 日志文件含成功标志行").toBeTruthy();
  expect(!attempt2Log.includes("[CONSOLE]"), "尝试 2 日志文件不含控制台输出（分离）").toBeTruthy();
});

test("完成操作倒计时卡片：队列完成后可取消（shutdown DRYRUN）", async ({ page }) => {
  const saDir = makeScriptDir("sysact");
  const created = await createScript({ name: "倒计时脚本", rootPath: saDir.root, mainExe: saDir.main, configPath: saDir.cfg, logPath: saDir.log });
  expect(created.ok, "创建倒计时用例脚本").toBeTruthy();
  const qr = await api("POST", "/api/queues", {
    name: "倒计时队列", autoRunMode: "none", completionAction: "shutdown", timeSets: [],
    tasks: [{ id: "", index: 0, scriptInstanceId: created.id }],
  });
  expect(qr.ok, "创建完成操作队列（shutdown）").toBeTruthy();
  const qid = (await qr.json()).id;
  try {
    const dispatch = await api("POST", "/api/dispatch/queue", { queueId: qid, mode: "manual" });
    expect(dispatch.ok, "手动执行倒计时队列").toBeTruthy();
    expect(await waitNoRunning(60000), "倒计时队列运行结束").toBeTruthy();
    const status1 = await (await fetch(baseUrl + "api/status")).json();
    expect(status1.systemAction && status1.systemAction.action === "shutdown", "队列完成后 /api/status 出现 systemAction（shutdown）").toBeTruthy();
    expect(status1.systemAction.queueName === "倒计时队列", "systemAction 携带队列名").toBeTruthy();
    expect(status1.systemAction.deadline && new Date(status1.systemAction.deadline).getTime() > Date.now(), "systemAction 携带未来截止时间（倒计时进行中）").toBeTruthy();

    await page.goto(baseUrl + "#/dashboard", { waitUntil: "domcontentloaded" });
    await page.waitForSelector('[data-testid="system-action-card"]', { timeout: 10000 });
    const cardText = await page.textContent('[data-testid="system-action-card"]');
    expect(cardText.includes("倒计时队列") && cardText.includes("秒后将关机"), "卡片显示队列名与倒计时文案").toBeTruthy();
    const cdText = (await page.textContent('[data-testid="system-action-countdown"]') || "").trim();
    expect(/\d+ 秒后将关机/.test(cdText), "倒计时文本为「N 秒后将关机」（" + cdText + "）").toBeTruthy();

    await page.click('[data-action="cancel-system-action"]');
    await page.waitForFunction(() => !document.querySelector('[data-testid="system-action-card"]'), null, { timeout: 10000 });
    expect(true, "取消后卡片消失（状态已重新拉取）").toBeTruthy();
    const status2 = await (await fetch(baseUrl + "api/status")).json();
    expect(status2.systemAction === null, "取消后 /api/status 的 systemAction 为 null").toBeTruthy();
  } finally {
    try { await api("POST", "/api/system-action/cancel"); } catch { /* 兜底清理：断言失败也不留关机倒计时 */ }
    await api("DELETE", "/api/queues/" + qid);
    await api("DELETE", "/api/scripts/" + created.id);
  }
});
