import fs from "node:fs";
import path from "node:path";
import { test, expect } from "@playwright/test";
import { baseUrl, PING_GAME, runtimeDir, makeScriptDir, createScript, api, waitFor, waitNoRunning, waitAbsent, latestHistoryDay, localDate, ensureService } from "./helpers.mjs";

await ensureService();

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
  expect(qBody.includes("不自动运行"), "手动队列卡片显示「不自动运行」徽章").toBeTruthy();
  expect(!qBody.includes("手动运行") && !qBody.includes("按计划运行") && !qBody.includes("启动时运行"), "队列卡片不再显示运行模式徽章").toBeTruthy();

  const sq = await api("POST", "/api/queues", { name: "启动队列", autoRunMode: "startup", completionAction: "none", timeSets: [], tasks: [{ id: "", index: 0, scriptInstanceId: created.id }] });
  expect(sq.ok, "API 创建启动时运行队列").toBeTruthy();
  await page.goto(baseUrl + "#/dashboard", { waitUntil: "domcontentloaded" });
  await page.goto(baseUrl + "#/queues", { waitUntil: "domcontentloaded" });
  await page.waitForFunction(() => document.body.textContent.includes("启动队列"), null, { timeout: 5000 });
  expect((await page.textContent("body")).includes("将在下次启动开始运行"), "启动时运行队列显示「将在下次启动开始运行」").toBeTruthy();
  expect(!(await page.textContent("body")).includes("启动时运行"), "启动队列卡片不再显示运行模式徽章").toBeTruthy();
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
  expect((await page.textContent('[data-testid="queue-card"] [data-testid="queue-notify"]')).includes("队列通知未开启"), "定时队列卡片显示队列通知状态").toBeTruthy();
  const tqList = await (await fetch(baseUrl + "api/queues")).json();
  const tqGot = tqList.find(q => q.name === "定时倒计时队列");
  expect(tqGot && tqGot.nextTrigger, "定时运行队列 API 返回 nextTrigger").toBeTruthy();
  await api("DELETE", "/api/queues/" + tqGot.id);

  const editedQueueCard = page.locator('[data-testid="queue-card"]').filter({ hasText: "测试队列A-改" }).first();
  await editedQueueCard.locator(".overflow-trigger").click();
  await editedQueueCard.locator('[role="menuitem"][data-action="delete-queue"]').click();
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
  await page.locator(".timeset-card").nth(1).locator("summary").click();
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

test("历史详情：长日志默认尾部显示并可按需加载完整内容", async ({ page }) => {
  const dir = makeScriptDir("history-full-log");
  const logFile = path.join(dir.root, "history-full.log");
  const bat = path.join(dir.root, "history-full.bat");
  fs.writeFileSync(bat, [
    "@echo off",
    `for /L %%i in (1,1,250) do echo LONG-LINE-%%i>>"${logFile}"`,
    "exit /b 0",
  ].join("\r\n"), "ascii");
  const created = await createScript({
    name: "完整日志脚本", rootPath: dir.root, mainExe: bat,
    configPath: dir.cfg, logPath: logFile, maxAttempts: 1,
    logStallTimeoutMinutes: 5, totalTimeoutMinutes: 30, successKeywords: "LONG-LINE-250",
  });
  expect(created.ok, "创建完整日志测试脚本").toBeTruthy();
  try {
    const dispatch = await api("POST", "/api/dispatch/script", { scriptId: created.id, mode: "manual" });
    expect(dispatch.ok, "发起完整日志测试脚本运行").toBeTruthy();
    expect(await waitNoRunning(30000), "完整日志测试脚本运行结束").toBeTruthy();

    await page.goto(baseUrl + "#/history", { waitUntil: "domcontentloaded" });
    const row = page.locator('[data-testid="history-entry"]').filter({ hasText: "完整日志脚本" }).first();
    await row.waitFor({ timeout: 10000 });
    await row.click();
    await page.waitForSelector('[data-action="history-full-log"]', { timeout: 5000 });
    const tail = (await page.locator("[data-history-log-body]").first().textContent()) || "";
    const tailLines = tail.split(/\r?\n/).filter(Boolean);
    expect(tailLines.length === 200 && !tailLines.includes("LONG-LINE-1") && tailLines.includes("LONG-LINE-250"), "长日志详情默认只显示尾部 200 行").toBeTruthy();
    await page.click('[data-action="history-full-log"]');
    await page.waitForSelector('[data-action="history-full-log"]', { state: "detached", timeout: 5000 });
    const full = (await page.locator("[data-history-log-body]").first().textContent()) || "";
    const fullLines = full.split(/\r?\n/).filter(Boolean);
    expect(fullLines.includes("LONG-LINE-1") && fullLines.includes("LONG-LINE-250"), "点击后可加载完整脚本日志").toBeTruthy();
    expect(/\d+ 行/.test((await page.locator("[data-history-log-meta]").first().textContent()) || ""), "完整日志元信息显示总行数").toBeTruthy();
    await page.click(".modal button:has-text('关闭')");
  } finally {
    try { await api("DELETE", "/api/scripts/" + created.id); } catch { /* 清理失败不阻塞 */ }
  }
});

test("历史记录日期索引：仅显示有记录的日期 + 按日取记录 + 详情弹窗（v0.8.7）", async ({ page }) => {
  const io = makeScriptDir("history-dates");
  const today = localDate();
  const pad = n => String(n).padStart(2, "0");
  const shift = days => { const d = new Date(); d.setDate(d.getDate() + days); return `${d.getFullYear()}-${pad(d.getMonth() + 1)}-${pad(d.getDate())}`; };
  const oldDate = shift(-2);
  const absentDate = shift(-1);

  // 造数：前 2 天的历史记录（磁盘 JSON 为 PascalCase + 按尝试 .log，参照既有磁盘格式用例）
  const oldDayDir = path.join(runtimeDir, "history", oldDate);
  fs.mkdirSync(oldDayDir, { recursive: true });
  const oldStart = `${oldDate}T08:30:00`;
  const oldRecord = {
    Id: "history-dates-fake-1", ScriptInstanceId: "fake-script", ScriptName: "历史造数脚本",
    QueueId: "", QueueName: "", Mode: "manual", UserName: "默认",
    StartTime: oldStart, EndTime: `${oldDate}T08:31:00`,
    Attempts: 1, MaxAttempts: 1, Status: "failed", FinalStatus: "failed",
    ResultDetail: "脚本进程超时", LogFile: "08-30-00.json",
    AttemptDetails: [{ Number: 1, StartTime: oldStart, EndTime: `${oldDate}T08:31:00`, Status: "failed", Reason: "日志无更新超时", LogFile: "08-30-00-1.log" }],
  };
  fs.writeFileSync(path.join(oldDayDir, "08-30-00.json"), JSON.stringify(oldRecord, null, 2), "utf8");
  fs.writeFileSync(path.join(oldDayDir, "08-30-00-1.log"), "attempt log line\n", "utf8");

  // 今日真实运行一条
  const created = await createScript({ name: "日期索引脚本", rootPath: io.root, mainExe: io.main, configPath: io.cfg, logPath: io.log, maxAttempts: 1, logStallTimeoutMinutes: 5, totalTimeoutMinutes: 30 });
  expect(created.ok, "创建日期索引测试脚本").toBeTruthy();
  try {
    const dispatch = await api("POST", "/api/dispatch/script", { scriptId: created.id, mode: "manual" });
    expect(dispatch.ok, "发起日期索引运行").toBeTruthy();
    expect(await waitNoRunning(60000), "日期索引运行结束").toBeTruthy();

    // API：dates 仅含有记录的日期且倒序；date 响应形状；非法 date 400
    const datesRes = await (await fetch(baseUrl + "api/history/dates?days=7")).json();
    const dates = datesRes.dates || [];
    expect(dates.length >= 2, "日期索引含多个有记录的日期").toBeTruthy();
    expect(dates[0].date === today, "日期索引最新日期置顶").toBeTruthy();
    expect(dates.some(d => d.date === oldDate) && !dates.some(d => d.date === absentDate), "仅显示有记录的日期（前 2 天显示、前 1 天不显示）").toBeTruthy();
    expect(dates.every((d, i) => i === 0 || dates[i - 1].date > d.date), "日期索引按日期倒序").toBeTruthy();
    expect(dates.find(d => d.date === oldDate)?.count === 1, "日期索引含当日条数").toBeTruthy();
    const dayRes = await (await fetch(baseUrl + "api/history?date=" + oldDate)).json();
    expect(dayRes && dayRes.date === oldDate && Array.isArray(dayRes.records) && dayRes.records.length === 1, "按日期取记录返回当日记录").toBeTruthy();
    expect(typeof dayRes.historyDir === "string" && dayRes.historyDir.length > 0, "按日期取记录返回 historyDir").toBeTruthy();
    expect((await fetch(baseUrl + "api/history?date=bad-date")).status === 400, "非法 date 参数返回 400").toBeTruthy();

    // UI：日期列表默认选中最新日期（今日），今日记录含本用例运行
    await page.goto(baseUrl + "#/history", { waitUntil: "domcontentloaded" });
    await page.waitForSelector('[data-testid="history-panels"]', { timeout: 10000 });
    const optCount = await page.$$eval("#history-days option", els => els.length);
    expect(optCount === 7, "天数范围下拉含 7 个选项（7/15/30/60/90/120/180）").toBeTruthy();
    const dateRows = await page.$$eval('[data-testid="history-date"]', els => els.map(el => el.getAttribute("data-date")));
    expect(dateRows[0] === today, "日期列表默认选中并置顶今日").toBeTruthy();
    expect(dateRows.includes(oldDate) && !dateRows.includes(absentDate), "日期列表仅显示有记录的日期").toBeTruthy();
    await page.waitForFunction(() => document.body.textContent.includes("日期索引脚本"), null, { timeout: 5000 });

    // 切到前 2 天：显示造数记录（失败徽章 + 原因 + 文件路径），点击弹出详情
    await page.click(`[data-testid="history-date"][data-date="${oldDate}"]`);
    await page.waitForFunction(() => Array.from(document.querySelectorAll('[data-testid="history-entry"]')).some(el => el.textContent.includes("历史造数脚本")), null, { timeout: 5000 });
    const entryText = await page.textContent('[data-testid="history-entry"]');
    expect(entryText.includes("08:30:00") && entryText.includes("失败") && entryText.includes("脚本进程超时"), "记录条显示时间、失败徽章与原因").toBeTruthy();
    expect(entryText.includes(oldDate) && entryText.includes("08-30-00.json"), "记录条显示记录文件路径").toBeTruthy();
    await page.click('[data-testid="history-entry"]');
    await page.waitForSelector(".modal-mask", { timeout: 5000 });
    const modalText = await page.textContent(".modal");
    expect(modalText.includes("运行详情") && modalText.includes("历史造数脚本") && modalText.includes("脚本日志"), "点击记录条弹出运行详情（含脚本日志）").toBeTruthy();
    await page.click(".modal button:has-text('关闭')");
  } finally {
    await api("DELETE", "/api/scripts/" + created.id);
  }
});

test("调度中心取消运行：确认卡片可取消/确认，确认后弹窗关闭", async ({ page }) => {
  const dir = makeScriptDir("cancel-confirm");
  const bat = path.join(dir.root, "cancel-confirm.bat");
  fs.writeFileSync(bat, "@echo off\r\nping -n 12 127.0.0.1 >nul\r\nexit /b 0\r\n", "ascii");
  const created = await createScript({ name: "取消确认脚本", rootPath: dir.root, mainExe: bat, configPath: dir.cfg, logPath: dir.log, maxAttempts: 1, logStallTimeoutMinutes: 5, totalTimeoutMinutes: 30 });
  expect(created.ok, "创建取消确认测试脚本").toBeTruthy();
  try {
    await page.goto(baseUrl + "#/dispatch", { waitUntil: "domcontentloaded" });
    await page.waitForSelector("#dc-script");
    await page.selectOption("#dc-script", { label: "取消确认脚本" });
    await page.click("button:has-text('执行')");
    await page.waitForSelector("#dispatch-running .list-item", { timeout: 10000 });
    await page.click('[data-action="cancel-run"]');
    await page.waitForSelector('[data-action="confirm-cancel-run"]', { timeout: 5000 });
    expect((await page.textContent(".modal")).includes("后续任务也不会继续执行"), "取消运行先显示风险确认文案").toBeTruthy();
    await page.click(".modal button:has-text('取消')");
    await page.waitForSelector(".modal-mask", { state: "detached", timeout: 5000 });
    expect(await waitFor(async () => (await (await fetch(baseUrl + "api/status")).json()).running?.length > 0, 5000), "取消确认卡片点取消不会终止运行").toBeTruthy();
    await page.click('[data-action="cancel-run"]');
    await page.click('[data-action="confirm-cancel-run"]');
    await page.waitForSelector(".modal-mask", { state: "detached", timeout: 5000 });
    expect(await waitNoRunning(30000), "确认取消后运行结束且确认弹窗关闭").toBeTruthy();
  } finally {
    try { await api("DELETE", "/api/scripts/" + created.id); } catch { /* 清理失败不阻塞 */ }
  }
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

test("队列防重入：运行中重复触发被拒（KN-03）", async () => {
  // 自建 ping 脚本保证队列运行窗口（~3 秒真实）内可完成第二次触发；
  // 修复前 Register 仅对脚本查重，运行中再次触发队列会双跑（双历史/双通知/双完成操作）。
  const dDir = makeScriptDir("kn03");
  fs.writeFileSync(path.join(dDir.root, "nexustest-kn03.bat"), "@echo off\r\nping -n 4 127.0.0.1 >nul\r\nexit /b 0\r\n", "ascii");
  const created = await api("POST", "/api/scripts", {
    name: "KN03脚本", rootPath: dDir.root, mainExe: path.join(dDir.root, "nexustest-kn03.bat").replace(/\\/g, "\\\\"),
    configPath: dDir.cfg, logPath: dDir.log, gameExe: PING_GAME,
    maxAttempts: 1, logStallTimeoutMinutes: 5, totalTimeoutMinutes: 120,
  });
  const sid = (await created.json()).id;
  await api("POST", `/api/scripts/${sid}/users`, { name: "默认", enabled: true });
  const q = await api("POST", "/api/queues", { name: "KN03队列", autoRunMode: "none", completionAction: "none", timeSets: [], tasks: [{ id: "", index: 0, scriptInstanceId: sid }] });
  const qid = (await q.json()).id;

  const first = await api("POST", "/api/dispatch/queue", { queueId: qid, mode: "manual" });
  expect(first.ok, "第一次触发队列成功").toBeTruthy();
  const inRunning = await waitFor(async () => {
    const status = await (await fetch(baseUrl + "api/status")).json();
    return (status.running || []).some(r => r.kind === "queue" && r.targetId === qid);
  }, 10000, 200);
  expect(inRunning, "队列进入运行中状态").toBeTruthy();

  const second = await api("POST", "/api/dispatch/queue", { queueId: qid, mode: "manual" });
  expect(!second.ok && second.status === 400, "运行中重复触发被拒（HTTP 400，实际 " + second.status + "）").toBeTruthy();
  const errBody = await second.json();
  expect((errBody.error || "").includes("运行"), "拒绝原因含「运行」（" + JSON.stringify(errBody) + "）").toBeTruthy();

  expect(await waitNoRunning(120000), "队列运行结束").toBeTruthy();
  await api("DELETE", "/api/scripts/" + sid);
  await api("DELETE", "/api/queues/" + qid);
});

test("队列卡片拖拽排序：页内拖拽落盘 + 名单校验", async ({ page }) => {
  // 清理先前用例失败残留的队列（防御：残留会导致卡片数断言失准）
  const staleQueues = await (await fetch(baseUrl + "api/queues")).json();
  for (const item of staleQueues) {
    try { await api("DELETE", "/api/queues/" + item.id); } catch { /* 清理失败不阻塞 */ }
  }
  const qDir = makeScriptDir("qdnd");
  const created = await createScript({ name: "队列拖拽用脚本", rootPath: qDir.root, mainExe: qDir.main, configPath: qDir.cfg, logPath: qDir.log });
  expect(created.ok, "创建队列拖拽用脚本").toBeTruthy();
  const qids = [];
  for (const name of ["拖拽队列甲", "拖拽队列乙", "拖拽队列丙"]) {
    const createdQ = await api("POST", "/api/queues", { name, autoRunMode: "none", completionAction: "none", timeSets: [], tasks: [{ id: "", index: 0, scriptInstanceId: created.id }] });
    expect(createdQ.ok, "创建队列 " + name).toBeTruthy();
    qids.push((await createdQ.json()).id);
  }
  try {
    await page.goto(baseUrl + "#/queues", { waitUntil: "domcontentloaded" });
    await page.waitForFunction(() => document.querySelectorAll('[data-testid="queue-card"]').length === 3, null, { timeout: 10000 });
    const dragQueue = async (fromIndex, toBox) => {
      const cards = page.locator('[data-testid="queue-card"]');
      const handle = cards.nth(fromIndex).locator(".drag-handle");
      await handle.waitFor({ timeout: 10000 });
      let box = await handle.boundingBox();
      if (!box) { await page.waitForTimeout(400); box = await handle.boundingBox(); }
      if (!box) throw new Error("拖拽把手不可见");
      await page.mouse.move(box.x + box.width / 2, box.y + box.height / 2);
      await page.mouse.down();
      await page.mouse.move(toBox.x + toBox.width / 2, toBox.y + 2, { steps: 8 });
      await page.mouse.up();
    };
    // 第三张拖到第一张顶部 → 顺序变为 拖拽队列丙,拖拽队列甲,拖拽队列乙
    const boxes = [];
    for (let i = 0; i < 3; i++) boxes.push(await page.locator('[data-testid="queue-card"]').nth(i).boundingBox());
    await dragQueue(2, boxes[0]);
    await page.waitForFunction(() => {
      const cards = Array.from(document.querySelectorAll('[data-testid="queue-card"]'));
      return cards.length === 3 && cards[0].textContent.includes("拖拽队列丙");
    }, null, { timeout: 10000 });
    expect(true, "拖拽后 拖拽队列丙 成为第一张卡片").toBeTruthy();
    // v0.6.10 修复：dnd onDrop 不等待 PUT 落盘完成，立即 fetch 存在竞态（CI 稳定复现）——轮询等待服务端顺序生效
    const orderOk = await waitFor(async () => {
      const l = await (await fetch(baseUrl + "api/queues")).json();
      return l.map(q => q.name).join() === "拖拽队列丙,拖拽队列甲,拖拽队列乙";
    }, 10000);
    expect(orderOk, "拖拽后队列顺序已落盘（拖拽队列丙,拖拽队列甲,拖拽队列乙）").toBeTruthy();

    expect((await api("PUT", "/api/queues/order", { ids: qids.slice(0, 2) })).status === 400, "顺序名单缺项被拒（400）").toBeTruthy();
    expect((await api("PUT", "/api/queues/order", { ids: [...qids, "no-such-id"] })).status === 400, "顺序名单含不存在 id 被拒（400）").toBeTruthy();
    expect((await api("PUT", "/api/queues/order", { ids: [qids[0], qids[0], qids[1]] })).status === 400, "顺序名单含重复 id 被拒（400）").toBeTruthy();
  } finally {
    for (const id of qids) { try { await api("DELETE", "/api/queues/" + id); } catch { /* 清理失败不阻塞 */ } }
    try { await api("DELETE", "/api/scripts/" + created.id); } catch { /* 清理失败不阻塞 */ }
  }
});

test("长时脚本运行：-1 超时不触发日志无更新超时失败", async () => {
  const dir = makeScriptDir("longrun");
  const logFile = path.join(dir.root, "logs", "long.log");
  const longBat = path.join(dir.root, "long-run.bat");
  fs.writeFileSync(longBat, [
    "@echo off",
    "echo [SCRIPT] LONG-START >> \"" + logFile + "\"",
    "ping 127.0.0.1 -n 8 >nul",
    "echo [SCRIPT] LONG-END >> \"" + logFile + "\"",
    "exit /b 0",
  ].join("\r\n"), "ascii");
  const res = await api("POST", "/api/scripts", {
    name: "长时运行脚本", rootPath: dir.root, mainExe: longBat.replace(/\\/g, "\\\\"),
    configPath: dir.cfg, logPath: logFile.replace(/\\/g, "\\\\"), gameExe: PING_GAME,
    maxAttempts: 1, logStallTimeoutMinutes: -1, totalTimeoutMinutes: -1,
  });
  expect(res.ok, "长时脚本（两个超时 -1）保存成功").toBeTruthy();
  const sid = (await res.json()).id;
  try {
    await api("POST", `/api/scripts/${sid}/users`, { name: "默认", enabled: true });
    await api("POST", "/api/dispatch/script", { scriptId: sid, mode: "manual" });
    expect(await waitNoRunning(90000), "长时脚本运行结束（卡住 7 秒超过加速档 stall 6 秒仍不触发无更新超时）").toBeTruthy();
    const hist = await (await fetch(baseUrl + "api/history?days=7")).json();
    const rec = hist.filter(h => h.scriptInstanceId === sid).at(-1);
    expect(rec && rec.finalStatus === "success", "长时脚本卡住超过加速档 stall 时长仍判定成功（-1 不触发日志无更新超时，进程退出判成功）").toBeTruthy();
    expect(rec.attempts === 1, "长时脚本一次尝试成功（无超时重试）").toBeTruthy();
  } finally {
    try { await api("DELETE", "/api/scripts/" + sid); } catch { /* 清理失败不阻塞 */ }
    fs.rmSync(dir.root, { recursive: true, force: true });
  }
});

test("调度队列：长时/普通混排拒绝，纯长时队列通过", async () => {
  const longDir = makeScriptDir("mixlong");
  const normDir = makeScriptDir("mixnorm");
  const longRes = await api("POST", "/api/scripts", {
    name: "混排长时脚本", rootPath: longDir.root, mainExe: longDir.main,
    configPath: longDir.cfg, logPath: longDir.log, gameExe: PING_GAME,
    maxAttempts: 1, logStallTimeoutMinutes: -1, totalTimeoutMinutes: -1,
  });
  const longId = (await longRes.json()).id;
  const normRes = await api("POST", "/api/scripts", {
    name: "混排普通脚本", rootPath: normDir.root, mainExe: normDir.main,
    configPath: normDir.cfg, logPath: normDir.log, gameExe: PING_GAME,
    maxAttempts: 1, logStallTimeoutMinutes: 5, totalTimeoutMinutes: 120,
  });
  const normId = (await normRes.json()).id;
  let mixQueueId = "";
  try {
    const mix = await api("POST", "/api/queues", {
      name: "混排队列", autoRunMode: "none", completionAction: "none", timeSets: [], notifyEnabled: false,
      tasks: [{ id: "", index: 0, scriptInstanceId: longId }, { id: "", index: 1, scriptInstanceId: normId }],
    });
    expect(mix.status === 400, "长时 + 普通混排队列保存被拒（400）").toBeTruthy();
    const mixErr = await mix.json();
    expect((mixErr.error || "").includes("长时"), "混排拒绝提示含长时语义").toBeTruthy();

    const okRes = await api("POST", "/api/queues", {
      name: "纯长时队列", autoRunMode: "none", completionAction: "none", timeSets: [], notifyEnabled: false,
      tasks: [{ id: "", index: 0, scriptInstanceId: longId }],
    });
    expect(okRes.ok, "纯长时队列保存成功").toBeTruthy();
    mixQueueId = (await okRes.json()).id;
  } finally {
    if (mixQueueId) { try { await api("DELETE", "/api/queues/" + mixQueueId); } catch { /* 清理失败不阻塞 */ } }
    try { await api("DELETE", "/api/scripts/" + longId); } catch { /* 清理失败不阻塞 */ }
    try { await api("DELETE", "/api/scripts/" + normId); } catch { /* 清理失败不阻塞 */ }
  }
});

test("队列编辑弹窗：定时列表/任务列表拖拽排序", async ({ page }) => {
  const dir = makeScriptDir("dndq");
  const a = await createScript({ name: "拖拽任务甲", rootPath: dir.root, mainExe: dir.main, configPath: dir.cfg, logPath: dir.log });
  const b = await createScript({ name: "拖拽任务乙", rootPath: dir.root, mainExe: dir.main, configPath: dir.cfg, logPath: dir.log });
  expect(a.ok && b.ok, "创建拖拽用脚本成功").toBeTruthy();
  let qid = "";
  try {
    await page.goto(baseUrl + "#/queues", { waitUntil: "domcontentloaded" });
    await page.waitForSelector("h2");
    await page.click("button:has-text('新建调度队列')");
    await page.waitForSelector("#qm-name");
    await page.fill("#qm-name", "拖拽排序队列");
    await page.click("text=+ 添加任务");
    await page.selectOption('[data-task-idx="0"]', { label: "拖拽任务甲" });
    await page.click("text=+ 添加任务");
    await page.selectOption('[data-task-idx="1"]', { label: "拖拽任务乙" });
    await page.click("button:has-text('+ 添加定时')");
    await page.fill('[data-ts-time="0"]', "08:00");
    await page.locator(".timeset-card").nth(1).locator("summary").click();
    await page.fill('[data-ts-time="1"]', "08:30");
    const dragToTop = async (itemLocator, targetLocator) => {
      const handle = itemLocator.locator(".drag-handle");
      await handle.waitFor({ timeout: 10000 });
      // v0.7.0：弹窗内容较高时把手可能在视口外（Playwright 会把越界坐标 clamp 到视口边缘导致事件落空），先滚入视口
      await handle.scrollIntoViewIfNeeded();
      const scrollBefore = await page.$eval(".modal-body", el => el.scrollTop);
      let box = await handle.boundingBox();
      if (!box) { await page.waitForTimeout(400); box = await handle.boundingBox(); }
      if (!box) throw new Error("拖拽把手不可见");
      await page.mouse.move(box.x + box.width / 2, box.y + box.height / 2);
      await page.mouse.down();
      // 目标行顶部（滚动后动态取，防止坐标失效）
      const target = await targetLocator.boundingBox();
      const tx = target ? target.x + target.width / 2 : box.x;
      const ty = target ? target.y + 2 : box.y - 12;
      await page.mouse.move(tx, ty, { steps: 8 });
      await page.mouse.up();
      return scrollBefore;
    };

    const taskRows = page.locator("#qm-tasks .task-row");
    expect((await taskRows.count()) === 2, "任务列表两条").toBeTruthy();
    const taskDragScrollBefore = await dragToTop(taskRows.nth(1), taskRows.nth(0));
    await page.waitForTimeout(500);
    expect((await taskRows.nth(0).locator("select").inputValue()) !== "", "拖拽重渲染后第一行 select 值保留").toBeTruthy();
    const modalScrollAfterTaskDrag = await page.$eval(".modal-body", el => el.scrollTop);
    expect(Math.abs(modalScrollAfterTaskDrag - taskDragScrollBefore) <= 2, `任务拖拽后弹窗滚动位置保持（${taskDragScrollBefore} → ${modalScrollAfterTaskDrag}）`).toBeTruthy();

    const tsCards = page.locator("#qm-timesets .timeset-card");
    expect((await tsCards.count()) === 2, "定时列表两条").toBeTruthy();
    const timeSetDragScrollBefore = await dragToTop(tsCards.nth(1), tsCards.nth(0));
    await page.waitForTimeout(500);
    const modalScrollAfterTimeSetDrag = await page.$eval(".modal-body", el => el.scrollTop);
    expect(Math.abs(modalScrollAfterTimeSetDrag - timeSetDragScrollBefore) <= 2, `定时拖拽后弹窗滚动位置保持（${timeSetDragScrollBefore} → ${modalScrollAfterTimeSetDrag}）`).toBeTruthy();
    await page.click("button:has-text('+ 添加定时')");
    await page.waitForTimeout(300);
    const modalScrollAfterAdd = await page.$eval(".modal-body", el => el.scrollTop);
    expect(Math.abs(modalScrollAfterAdd - timeSetDragScrollBefore) <= 2, `新增定时后弹窗滚动位置保持（${timeSetDragScrollBefore} → ${modalScrollAfterAdd}）`).toBeTruthy();

    await page.click(".modal button:has-text('保存')");
    await page.waitForSelector(".modal-mask", { state: "detached", timeout: 5000 });
    const qList = await (await fetch(baseUrl + "api/queues")).json();
    const q = qList.find(item => item.name === "拖拽排序队列");
    expect(!!q, "队列已保存").toBeTruthy();
    qid = q.id;
    const scripts = await (await fetch(baseUrl + "api/scripts")).json();
    const nameOf = id => (scripts.find(s => s.id === id) || {}).name || "?";
    const taskNames = q.tasks.slice().sort((x, y) => x.index - y.index).map(t => nameOf(t.scriptInstanceId));
    expect(taskNames.join() === "拖拽任务乙,拖拽任务甲", "任务列表拖拽后顺序已落盘（乙在前，index 重排）").toBeTruthy();
    expect(q.timeSets[0].time === "08:30" && q.timeSets[1].time === "08:00", "定时列表拖拽后顺序已落盘（08:30 在前）").toBeTruthy();
  } finally {
    if (qid) { try { await api("DELETE", "/api/queues/" + qid); } catch { /* 清理失败不阻塞 */ } }
    try { await api("DELETE", "/api/scripts/" + a.id); } catch { /* 清理失败不阻塞 */ }
    try { await api("DELETE", "/api/scripts/" + b.id); } catch { /* 清理失败不阻塞 */ }
  }
});
