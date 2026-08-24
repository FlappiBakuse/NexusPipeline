import { spawn } from "node:child_process";
import fs from "node:fs";
import path from "node:path";
import { test, expect } from "@playwright/test";
import { baseUrl, PING_GAME, runtimeDir, runtimeExe, makeScriptDir, api, waitFor, localDate, restartService, stopService, startService, waitForService, ensureService } from "./helpers.mjs";

await ensureService();

test("约束体系：API 默认值 + 数量上限", async () => {
  const limits = await (await fetch(baseUrl + "api/limits")).json();
  expect(limits.limits.maxScripts === 25 && limits.limits.maxUsersPerScript === 10, "limits API 默认值（脚本 25 / 用户 10）").toBeTruthy();
  expect(limits.limits.maxQueues === 10 && limits.limits.maxTimeSetsPerQueue === 10, "limits API 默认值（队列 10 / 定时 10）").toBeTruthy();
  expect(limits.limits.maxAttempts === 10 && limits.limits.maxTotalMinutes === 720, "limits API 默认值（尝试 10 / 总时长 720）").toBeTruthy();
  expect(limits.warnings.length === 0, "默认配置无警告").toBeTruthy();

  const ids = [];
  const xDir = makeScriptDir("limits");
  const limitBase = { rootPath: xDir.root, mainExe: xDir.main, configPath: xDir.cfg, logPath: xDir.log, gameExe: PING_GAME };
  for (let i = 0; i < 25; i++) {
    const r = await api("POST", "/api/scripts", { name: "约束脚本" + String(i).padStart(2, "0"), ...limitBase, maxAttempts: 1, logStallTimeoutMinutes: 5, totalTimeoutMinutes: 120 });
    ids.push((await r.json()).id);
  }
  const r26 = await api("POST", "/api/scripts", { name: "超限脚本", ...limitBase, maxAttempts: 1, logStallTimeoutMinutes: 5, totalTimeoutMinutes: 120 });
  expect(r26.status === 400, "第 26 个脚本被拒（400）").toBeTruthy();

  for (let i = 0; i < 10; i++) {
    await api("POST", "/api/scripts/" + ids[0] + "/users", { name: "用户" + i, enabled: true });
  }
  const r11 = await api("POST", "/api/scripts/" + ids[0] + "/users", { name: "用户11", enabled: true });
  expect(r11.status === 400, "第 11 个用户被拒（400）").toBeTruthy();

  const qids = [];
  for (let i = 0; i < 10; i++) {
    const r = await api("POST", "/api/queues", { name: "约束队列" + i, autoRunMode: "scheduled", completionAction: "none", timeSets: [{ id: "", enabled: true, days: [1], time: "23:59" }], tasks: [{ id: "", index: 0, scriptInstanceId: ids[1] }] });
    qids.push((await r.json()).id);
  }
  const q11 = await api("POST", "/api/queues", { name: "超限队列", autoRunMode: "scheduled", completionAction: "none", timeSets: [], tasks: [{ id: "", index: 0, scriptInstanceId: ids[1] }] });
  expect(q11.status === 400, "第 11 个队列被拒（400）").toBeTruthy();

  const t11 = await api("PUT", "/api/queues/" + qids[0], { name: "约束队列0", autoRunMode: "scheduled", completionAction: "none", timeSets: Array.from({ length: 11 }, (_, i) => ({ id: "", enabled: true, days: [1], time: "23:" + String(40 + i) })), tasks: [{ id: "", index: 0, scriptInstanceId: ids[1] }] });
  expect(t11.status === 400, "第 11 个定时被拒（400）").toBeTruthy();

  for (const qid of qids) await api("DELETE", "/api/queues/" + qid);
  for (const id of ids) await api("DELETE", "/api/scripts/" + id);
});

test("约束体系：名称字节 / 数值区间 / 任务总用户", async () => {
  const fDir = makeScriptDir("fields");
  const base = { rootPath: fDir.root, mainExe: fDir.main, configPath: fDir.cfg, logPath: fDir.log, gameExe: PING_GAME, maxAttempts: 1, logStallTimeoutMinutes: 5, totalTimeoutMinutes: 120 };
  const postScript = body => api("POST", "/api/scripts", body);

  const longName = await postScript({ ...base, name: "长".repeat(43) });
  expect(longName.status === 400, "脚本名 129 字节被拒（400）").toBeTruthy();
  const okName = await postScript({ ...base, name: "长".repeat(42) });
  expect(okName.ok, "脚本名 126 字节通过").toBeTruthy();
  const sid = (await okName.json()).id;

  expect((await postScript({ ...base, name: "attempts11", maxAttempts: 11 })).status === 400, "attempts=11 被拒").toBeTruthy();
  expect((await postScript({ ...base, name: "attempts0", maxAttempts: 0 })).status === 400, "attempts=0 被拒").toBeTruthy();
  expect((await postScript({ ...base, name: "stall61", logStallTimeoutMinutes: 61 })).status === 400, "无更新超时 61 分钟被拒").toBeTruthy();
  expect((await postScript({ ...base, name: "total4", totalTimeoutMinutes: 4 })).status === 400, "总时长 4 分钟被拒").toBeTruthy();
  expect((await postScript({ ...base, name: "total721", totalTimeoutMinutes: 721 })).status === 400, "总时长 721 分钟被拒").toBeTruthy();

  const qLong = await api("POST", "/api/queues", { name: "队".repeat(43), autoRunMode: "scheduled", completionAction: "none", timeSets: [], tasks: [{ id: "", index: 0, scriptInstanceId: sid }] });
  expect(qLong.status === 400, "队列名 129 字节被拒（400）").toBeTruthy();

  for (let i = 0; i < 10; i++) {
    await api("POST", "/api/scripts/" + sid + "/users", { name: "任务用户" + i, enabled: true });
  }
  const tasks5 = Array.from({ length: 5 }, (_, i) => ({ id: "", index: i, scriptInstanceId: sid }));
  const q5 = await api("POST", "/api/queues", { name: "任务50队列", autoRunMode: "scheduled", completionAction: "none", timeSets: [], tasks: tasks5 });
  expect(q5.ok, "任务启用用户总和 50 通过").toBeTruthy();
  const qid = (await q5.json()).id;
  const q6 = await api("POST", "/api/queues", { name: "任务60队列", autoRunMode: "scheduled", completionAction: "none", timeSets: [], tasks: [...tasks5, { id: "", index: 5, scriptInstanceId: sid }] });
  expect(q6.status === 400, "任务启用用户总和 60 被拒（400）").toBeTruthy();

  await api("DELETE", "/api/queues/" + qid);
  await api("DELETE", "/api/scripts/" + sid);
});

test("分页：脚本列表前端分页 + 达上限禁用 + 历史 API 分页", async ({ page }) => {
  const ids = [];
  const pgDir = makeScriptDir("pager");
  for (let i = 0; i < 25; i++) {
    const r = await api("POST", "/api/scripts", { name: "分页脚本" + String(i).padStart(2, "0"), rootPath: pgDir.root, mainExe: pgDir.main, configPath: pgDir.cfg, logPath: pgDir.log, gameExe: PING_GAME, maxAttempts: 1, logStallTimeoutMinutes: 5, totalTimeoutMinutes: 120 });
    ids.push((await r.json()).id);
  }
  await page.goto(baseUrl + "#/scripts", { waitUntil: "domcontentloaded" });
  await page.waitForSelector("[data-testid='pager-scripts']", { timeout: 10000 });
  const rows1 = await page.$$eval("#view .script-card", els => els.length);
  expect(rows1 === 20, "脚本分页第一页 20 张卡片（实际 " + rows1 + "）").toBeTruthy();
  const info1 = await page.textContent("[data-testid='pager-scripts'] .pager-info");
  expect(info1.includes("共 25 条"), "分页条显示共 25 条").toBeTruthy();
  await page.click("[data-testid='pager-scripts'] [data-action='pager-next']");
  await page.waitForFunction(() => document.querySelectorAll("#view .script-card").length === 5, null, { timeout: 5000 });
  expect(true, "翻页后第二页 5 张卡片").toBeTruthy();
  const newBtn = await page.$eval("[data-testid='new-script']", el => el.disabled);
  expect(newBtn === true, "脚本达上限新建按钮禁用").toBeTruthy();

  const hp = await (await fetch(baseUrl + "api/history?days=7&offset=0&limit=5")).json();
  expect(typeof hp.total === "number" && Array.isArray(hp.records) && hp.records.length <= 5, "历史 API 服务端分页返回 {total, records}").toBeTruthy();

  for (const id of ids) await api("DELETE", "/api/scripts/" + id);
});

test("约束警告：非法配置告警 + 前端卡片（知道了/不再提醒）", async ({ page }) => {
  const logFile = path.join(runtimeDir, "logs", "nexus-pipeline-" + localDate() + ".log");
  const readLog = () => fs.existsSync(logFile) ? fs.readFileSync(logFile, "utf8").replace(/^\uFEFF/, "") : "";
  const limitsFile = path.join(runtimeDir, "config", "limits.json");

  fs.mkdirSync(path.dirname(limitsFile), { recursive: true });
  fs.writeFileSync(limitsFile, '{"MaxScripts": 30}');
  await restartService();
  expect(readLog().includes("[警告] 约束配置 [MaxScripts"), "启动日志含约束警告").toBeTruthy();
  const l = await (await fetch(baseUrl + "api/limits")).json();
  expect(l.limits.maxScripts === 30, "警告级配置已生效（maxScripts=30）").toBeTruthy();
  expect(l.warnings.length === 1, "limits API 返回 1 条警告").toBeTruthy();

  await page.goto(baseUrl, { waitUntil: "domcontentloaded" });
  await page.waitForSelector("#limits-warning", { timeout: 10000 });
  const cardText = await page.textContent("#limits-warning");
  expect(cardText.includes("知道了") && cardText.includes("不再提醒"), "警告卡片含「知道了」「不再提醒」按钮").toBeTruthy();

  await page.click('[data-action="limits-dismiss-once"]');
  await page.waitForSelector("#limits-warning", { state: "detached", timeout: 5000 });
  expect(true, "点击「知道了」卡片关闭").toBeTruthy();
  await page.goto(baseUrl, { waitUntil: "domcontentloaded" });
  await page.waitForSelector("#limits-warning", { timeout: 10000 });
  expect(true, "重载后警告卡片再次出现").toBeTruthy();

  await page.click('[data-action="limits-dismiss-forever"]');
  await page.waitForSelector("#limits-warning", { state: "detached", timeout: 5000 });
  await page.goto(baseUrl, { waitUntil: "domcontentloaded" });
  await page.waitForTimeout(800);
  expect(!(await page.$("#limits-warning")), "点击「不再提醒」后重载不再出现").toBeTruthy();
  expect(readLog().includes("[警告] 约束配置 [MaxScripts"), "日志仍含约束警告（不受不再提醒影响）").toBeTruthy();

  fs.writeFileSync(limitsFile, '{"MaxScripts": 40}');
  await restartService();
  await page.goto(baseUrl, { waitUntil: "domcontentloaded" });
  await page.waitForSelector("#limits-warning", { timeout: 10000 });
  expect(true, "警告内容变化后重新提醒").toBeTruthy();
  await page.click('[data-action="limits-dismiss-forever"]');

  fs.rmSync(limitsFile, { force: true });
  await restartService();
  const l2 = await (await fetch(baseUrl + "api/limits")).json();
  expect(l2.warnings.length === 0 && l2.limits.maxScripts === 25, "恢复默认配置后无警告").toBeTruthy();
});

test("Web 端口占用自动 +1 重试（HttpListener 复用崩溃修复验证）", async () => {
  test.skip(process.env.NEXUS_ELEVATED_SERVICE === "1", "提权隔离宿主无法直接 spawn 非提权 web 子进程；标准 CI 保留端口 +1 重试断言");
  // v0.6.6+：web 模式抢单实例互斥（常驻服务在跑时直接退出），先停服务、用 node 监听占 58731 验证端口 +1。
  await stopService();
  await new Promise(r => setTimeout(r, 400));
  const blocker = spawn(process.execPath, ["-e", "require('net').createServer().listen(58731, '127.0.0.1')"], { stdio: "ignore" });
  await new Promise(r => setTimeout(r, 800));
  const second = spawn(runtimeExe, ["web"], { cwd: runtimeDir, stdio: ["pipe", "ignore", "ignore"] });
  const ok = await waitFor(async () => {
    try {
      const res = await fetch("http://127.0.0.1:58732/api/status");
      return res.ok;
    } catch {
      return false;
    }
  }, 20000, 300);
  expect(ok, "端口 58731 被占用时自动重试到 58732（/api/status 可达）").toBeTruthy();
  expect(second.exitCode === null, "重试成功且进程未崩溃（exitCode=null）").toBeTruthy();
  second.kill();
  blocker.kill();
  await new Promise(r => setTimeout(r, 600));
  await startService();
  await waitForService();
  expect(true, "已恢复常驻服务").toBeTruthy();
});

test("约束 FATAL：致命配置拒绝启动", async () => {
  const logFile = path.join(runtimeDir, "logs", "nexus-pipeline-" + localDate() + ".log");
  const readLog = () => fs.existsSync(logFile) ? fs.readFileSync(logFile, "utf8").replace(/^\uFEFF/, "") : "";
  const limitsFile = path.join(runtimeDir, "config", "limits.json");

  fs.mkdirSync(path.dirname(limitsFile), { recursive: true });
  fs.writeFileSync(limitsFile, '{"MaxScripts": 60}');
  await stopService();
  await new Promise(r => setTimeout(r, 400));
  startService();
  let started = true;
  try { await waitForService(5000); } catch { started = false; }
  expect(!started, "超警告区间（MaxScripts=60）服务拒绝启动").toBeTruthy();
  await new Promise(r => setTimeout(r, 500));
  expect(readLog().includes("[FATAL] 约束配置 [MaxScripts"), "启动日志含 FATAL 约束记录").toBeTruthy();

  fs.writeFileSync(limitsFile, '{"MinAttempts": 8, "MaxAttempts": 3}');
  await stopService();
  await new Promise(r => setTimeout(r, 400));
  startService();
  started = true;
  try { await waitForService(5000); } catch { started = false; }
  expect(!started, "Min>Max 区间矛盾配置服务拒绝启动").toBeTruthy();
  await new Promise(r => setTimeout(r, 500));
  expect(readLog().includes("[FATAL]") && readLog().includes("区间矛盾"), "日志含区间矛盾 FATAL").toBeTruthy();

  fs.rmSync(limitsFile, { force: true });
  await restartService();
  expect(true, "恢复默认配置后服务正常启动").toBeTruthy();
});
