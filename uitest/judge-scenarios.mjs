/**
 * judge-scenarios.mjs — 自定义完成标志稳定性专项测试（独立文件，断言计数见 AGENTS.md 与运行输出）
 *
 * 覆盖内容：
 *  - 用户场景 A：全部任务成功 → 一轮成功 + 通知「所有任务已全部完成」
 *  - 用户场景 B：任务失败 → 替换配置重试（只跑失败任务）→ 成功 + 通知
 *  - 用户场景 C：任务卡住（曾输出日志）→ 周期触发替换 → 再卡 → 达到最大次数失败 + 通知「任务N运行失败」
 *  - 边缘场景：完全无日志卡住（判断脚本零触发，B-1）
 *  - BUG 验证：新文件残留（B-2）/ 路径逃逸（B-3）/ marker 后重复触发（B-4）/ PostRun 覆盖通知文本（B-5）/
 *    API 空代码保存（B-8）/ 判断脚本超时与容错
 *
 * 运行：node uitest\judge-scenarios.mjs   （先跑 build.cmd）
 */
import { spawn } from "node:child_process";
import http from "node:http";
import fs from "node:fs";
import path from "node:path";
import { fileURLToPath } from "node:url";

const __dirname = path.dirname(fileURLToPath(import.meta.url));
const projectRoot = path.resolve(__dirname, "..");
const releaseDir = path.join(projectRoot, "release");
const runtimeDir = path.join(__dirname, "runtime");
const runtimeExe = path.join(runtimeDir, "nexus-pipeline.exe");
const baseUrl = "http://127.0.0.1:58731/";
const JSON_HDR = { "Content-Type": "application/json" };
const PING_GAME = "C:\\Windows\\System32\\PING.EXE";
const HOOK_PORT = 58888;

// 测试时间加速（v0.6.2+，v0.6.4 统一 scale=10）：run-uitest 加速档设置 NEXUS_TIME_SCALE 时，宿主等待（stall/周期触发/宽限/超时）按比例缩放，
// 伪造脚本的墙钟卡住时长与判断脚本内部墙钟常量同步缩放，保持场景语义（卡住时长仍远大于缩放后的周期触发间隔）。
const TIME_SCALE = Number(process.env.NEXUS_TIME_SCALE || "1") || 1;
const FAST = TIME_SCALE > 1;
const STUCK_PINGS = FAST ? 6 : 75;   // 卡住时长：真实 75 秒，加速 6 次 ping ≈ 5s（v0.6.4 scale=10 下周期触发 3s 先于退出、stall 30s 未到；60 档语义保持）
const CRASH_PINGS = FAST ? 3 : 9;    // marker 后继续输出批次（重复触发验证）：真实 8s，加速 ≈ 2s
const RESTART_WAIT_MS = FAST ? 1000 : 4000;   // 服务停止/重启静置等待

let passed = 0;
let failed = 0;
let child = null;
let hookServer = null;
let hookBodies = [];

function localDate() {
  const d = new Date();
  return `${d.getFullYear()}-${String(d.getMonth() + 1).padStart(2, "0")}-${String(d.getDate()).padStart(2, "0")}`;
}

function assert(cond, msg) {
  if (cond) {
    passed++;
    console.log("  [PASS] " + msg);
  } else {
    failed++;
    console.log("  [FAIL] " + msg);
  }
}

const sleep = ms => new Promise(r => setTimeout(r, ms));

async function waitFor(predicate, timeoutMs = 5000, intervalMs = 200) {
  const deadline = Date.now() + timeoutMs;
  while (Date.now() < deadline) {
    if (await predicate()) return true;
    await new Promise(r => setTimeout(r, intervalMs));
  }
  return !!(await predicate());
}

async function waitForService(timeoutMs = 30000) {
  const deadline = Date.now() + timeoutMs;
  while (Date.now() < deadline) {
    try {
      const res = await fetch(baseUrl + "api/status");
      if (res.ok) return;
    } catch { /* retry */ }
    await sleep(500);
  }
  throw new Error("服务未在 " + timeoutMs + "ms 内启动");
}

async function api(method, pathName, body) {
  const options = { method };
  if (body !== undefined) {
    options.headers = JSON_HDR;
    options.body = JSON.stringify(body);
  }
  return fetch(baseUrl + pathName.replace(/^\/+/, ""), options);
}

async function runningCount() {
  const status = await (await fetch(baseUrl + "api/status")).json();
  return (status.running || []).length;
}

async function waitNoRunning(timeoutMs = 120000, intervalMs = 300) {
  return waitFor(async () => (await runningCount()) === 0, timeoutMs, intervalMs);
}

function setupRuntime() {
  // 清理上次残留的测试服务（占用 58731），仅杀 uitest/runtime 目录下的 nexus-pipeline.exe（v0.6.2）
  try {
    spawnSync("powershell", ["-NoProfile", "-NonInteractive", "-Command",
      "$p = Get-CimInstance Win32_Process -Filter \"Name='nexus-pipeline.exe'\" | Where-Object { $_.ExecutablePath -like '*uitest\\runtime\\*' }; $p | ForEach-Object { Stop-Process -Id $_.ProcessId -Force }"],
      { stdio: "ignore" });
  } catch { /* 忽略 */ }
  fs.rmSync(runtimeDir, { recursive: true, force: true });
  fs.mkdirSync(runtimeDir, { recursive: true });
  const sourceExe = path.join(releaseDir, "nexus-pipeline.exe");
  if (!fs.existsSync(sourceExe)) {
    throw new Error("release/nexus-pipeline.exe 不存在，请先运行 build.cmd");
  }
  fs.copyFileSync(sourceExe, runtimeExe);
  fs.cpSync(path.join(releaseDir, "wwwroot"), path.join(runtimeDir, "wwwroot"), { recursive: true });
  if (fs.existsSync(path.join(releaseDir, "plugins"))) {
    fs.cpSync(path.join(releaseDir, "plugins"), path.join(runtimeDir, "plugins"), { recursive: true });
  } else {
    fs.mkdirSync(path.join(runtimeDir, "plugins"), { recursive: true });
  }
}

function startService() {
  child = spawn(runtimeExe, ["web"], { cwd: runtimeDir, stdio: ["pipe", "ignore", "ignore"] });
}

async function stopService() {
  if (child) {
    child.kill();
    await sleep(500);
    child = null;
  }
}

function startHookServer() {
  hookBodies = [];
  hookServer = http.createServer((req, res) => {
    let raw = "";
    req.on("data", chunk => { raw += chunk; });
    req.on("end", () => {
      hookBodies.push(raw);
      res.writeHead(200, { "Content-Type": "application/json" });
      res.end("{}");
    });
  });
  return new Promise(resolve => hookServer.listen(HOOK_PORT, "127.0.0.1", resolve));
}

function stopHookServer() {
  return new Promise(resolve => {
    if (hookServer) {
      hookServer.close(() => { hookServer = null; resolve(); });
    } else {
      resolve();
    }
  });
}

/* ---------------- 伪脚本套件 ---------------- */

/**
 * 多任务伪脚本目录：
 *  - config 目录 tasks.txt：每行 `id|enabled|mode`，mode = success | fail | stuck-silent | stuck-alt | crash-silent | script-crash | game-crash
 *    success       → 输出 TASK {id} DONE
 *    fail          → 输出 TASK {id} FAIL
 *    stuck-silent  → 静默卡住（ping 长延时，无任何输出）
 *    stuck-alt     → 按 %TEMP% 计数文件奇偶：奇数次运行卡住，偶数次 DONE（模拟「首次卡住、重试成功」）
 *    crash-silent  → 无任何日志输出，立即非零退出（模拟脚本直接崩溃）
 *    script-crash  → 输出 START 后运行片刻，脚本自身非零退出（模拟运行中途脚本崩溃）
 *    game-crash    → 输出 START 并启动游戏进程（ping 模拟），游戏进程仍在时脚本退出（模拟运行中途游戏崩溃，宿主失败后强制结束游戏进程）
 *  - 日志 logs/log.txt（主程序负责追加，全部 ASCII 规避 bat 中文编码问题）
 */
const MULTI_TASK_BAT = [
  "@echo off",
  "setlocal enabledelayedexpansion",
  "cd /d \"%~dp0\"",
  "if not exist logs mkdir logs",
  "ping -n 3 127.0.0.1 >nul",
  "for /f \"tokens=1,2,3 delims=|\" %%a in (tasks.txt) do (",
  "  if /i \"%%b\"==\"enabled\" (",
  "    if \"%%c\"==\"crash-silent\" exit /b 1",
  "    if \"%%c\"==\"script-crash\" (",
  "      echo TASK %%a START >> logs\\log.txt",
  "      ping -n 4 127.0.0.1 >nul",
  "      exit /b 1",
  "    )",
    "    if \"%%c\"==\"game-crash\" (",
    "      start \"\" /b ping -n 10 127.0.0.1 >nul",
    "      echo TASK %%a START >> logs\\log.txt",
    "      exit /b 1",
    "    )",
    "    if \"%%c\"==\"success\" echo TASK %%a DONE >> logs\\log.txt",
    "    if \"%%c\"==\"fail\" echo TASK %%a FAIL >> logs\\log.txt",
    "    if \"%%c\"==\"stuck-silent\" ping -n " + STUCK_PINGS + " 127.0.0.1 >nul",
    "    if \"%%c\"==\"stuck-alt\" (",
    "      set /a n=0",
    "      if exist \"%TEMP%\\%~n0-cnt.txt\" set /p n=<\"%TEMP%\\%~n0-cnt.txt\"",
    "      set /a n+=1",
    "      >\"%TEMP%\\%~n0-cnt.txt\" echo !n!",
    "      set /a m=n%%2",
    "      if \"!m!\"==\"1\" ping -n " + STUCK_PINGS + " 127.0.0.1 >nul",
    "      if \"!m!\"==\"0\" echo TASK %%a DONE >> logs\\log.txt",
    "    )",
  "  )",
  ")",
  "exit /b 0",
].join("\r\n") + "\r\n";

function makeMultiTaskDir(label, taskLines) {
  const dir = path.join(runtimeDir, "mt-" + label);
  fs.rmSync(dir, { recursive: true, force: true });
  fs.mkdirSync(path.join(dir, "logs"), { recursive: true });
  fs.writeFileSync(path.join(dir, "tasks.txt"), taskLines.join("\r\n") + "\r\n", "ascii");
  fs.writeFileSync(path.join(dir, `nexusmt-${label}.bat`), MULTI_TASK_BAT, "ascii");
  return dir;
}

/**
 * 默认判断脚本（JS 内置引擎）：
 *  - 读取 config/tasks.txt + 日志全文；
 *  - 全部启用任务均有 DONE → success + notifyText「所有任务已全部完成」；
 *  - 存在 FAIL → 生成新配置（已完成→disabled，失败任务→enabled+success 重试）→ failed + replaceConfigs；
 *  - 存在未完成任务且距上次触发 >20 秒（NEXUS_TIME_SCALE 加速时按 input.timeScale 缩放）→ 判定卡住（保持其模式，重试时 stuck-alt 会成功、stuck-silent 继续卡）→ failed + replaceConfigs；
 *  - tick 文件记录每次触发时间（跨尝试保留，script 目录运行期间不清空）。
 */
function defaultJudgeScript() {
  return `
const input = JSON.parse(__NEXUS_INPUT__);
const now = Date.now();
const scale = input.timeScale || 1;
const files = input.files || [];
const tickFile = files.find(f => f.Root === "script" && f.Path === "tick");
const last = tickFile ? Number(nexus.readFile(tickFile.Abs) || "0") : 0;
nexus.writeFile("tick", String(now));
const log = input.log || "";
const cfgFile = files.find(f => f.Root === "config" && f.Path === "tasks.txt");
const cfgText = cfgFile ? (nexus.readFile(cfgFile.Abs) || "") : "";
const enabled = [];
for (const line of cfgText.split(/\\r?\\n/)) {
  const p = line.split("|");
  if (p.length >= 3 && p[1].trim().toLowerCase() === "enabled") {
    enabled.push({ id: p[0].trim(), mode: p[2].trim() });
  }
}
const doneIds = [...log.matchAll(/TASK\\s+(\\w+)\\s+DONE/g)].map(m => m[1]);
const failIds = [...log.matchAll(/TASK\\s+(\\w+)\\s+FAIL/g)].map(m => m[1]);
const undone = enabled.filter(t => !doneIds.includes(t.id) && !failIds.includes(t.id));
const failedTasks = enabled.filter(t => failIds.includes(t.id) && !doneIds.includes(t.id));
if (enabled.length > 0 && undone.length === 0 && failedTasks.length === 0) {
  console.log(JSON.stringify({ status: "success", reason: "all-tasks-done", notifyText: "所有任务已全部完成" }));
} else if (failedTasks.length > 0) {
  const lines = [];
  for (const t of enabled) {
    if (doneIds.includes(t.id)) lines.push(t.id + "|disabled|" + t.mode);
    else if (failIds.includes(t.id)) lines.push(t.id + "|enabled|success");
    else lines.push(t.id + "|enabled|" + t.mode);
  }
  nexus.writeFile("tasks.txt", lines.join("\\r\\n"));
  console.log(JSON.stringify({ status: "failed", reason: "task-failed-" + failedTasks[0].id, notifyText: "任务" + failedTasks[0].id + "运行失败", replaceConfigs: ["tasks.txt"] }));
} else if (undone.length > 0 && now - last > 20000 / scale) {
  const stuckId = undone[0].id;
  const lines = [];
  for (const t of enabled) {
    if (doneIds.includes(t.id)) lines.push(t.id + "|disabled|" + t.mode);
    else lines.push(t.id + "|enabled|" + t.mode);
  }
  nexus.writeFile("tasks.txt", lines.join("\\r\\n"));
  console.log(JSON.stringify({ status: "failed", reason: "task-stuck-" + stuckId, notifyText: "任务" + stuckId + "运行失败", replaceConfigs: ["tasks.txt"] }));
}`;
}

async function createJudgeScript(extra = {}) {
  const res = await api("POST", "/api/scripts", {
    // 加速档下运行总时间超时也按比例缩小（10 分钟 → 10s），多轮重试场景需放大避免截断
    maxAttempts: 1, logStallTimeoutMinutes: 5, totalTimeoutMinutes: FAST ? 60 : 10, gameExe: PING_GAME,
    autoUpdateConfig: false,
    ...extra,
  });
  if (!res.ok) {
    return { ok: false, id: "", error: await res.text() };
  }
  const script = await res.json();
  const userRes = await api("POST", `/api/scripts/${script.id}/users`, { name: "默认", enabled: true });
  if (!userRes.ok) {
    return { ok: false, id: "", error: "添加默认用户失败" };
  }
  return { ok: true, id: script.id };
}

function userScriptDir(id, user = "默认") {
  return path.join(runtimeDir, "data", id, user, "script");
}

function userBackupDir(id, user = "默认") {
  return path.join(runtimeDir, "data", id, user, "swap-backup");
}

async function runScript(id, userName, timeoutMs = 180000) {
  const body = { scriptId: id, mode: "manual" };
  if (userName) body.userName = userName;
  const dispatch = await api("POST", "/api/dispatch/script", body);
  const ended = await waitNoRunning(timeoutMs);
  const hist = await (await fetch(baseUrl + "api/history?days=7")).json();
  const rec = hist.filter(h => h.scriptInstanceId === id).at(-1);
  return { dispatchOk: dispatch.ok, ended, rec };
}

/* ---------------- 用例 ---------------- */

async function testScenarioA() {
  console.log("[用例] 场景A：全部任务成功 → 一轮成功 + 通知「所有任务已全部完成」");
  const dir = makeMultiTaskDir("a", ["1|enabled|success", "2|enabled|success", "3|disabled|success", "4|enabled|success"]);
  const created = await createJudgeScript({
    name: "场景A全成功", rootPath: dir, mainExe: path.join(dir, "nexusmt-a.bat"),
    configPath: dir, logPath: path.join(dir, "logs\\log.txt"),
    judgeScriptEnabled: true, judgeScriptLanguage: "javascript", judgeScript: defaultJudgeScript(),
    notifyEnabled: true,
  });
  assert(created.ok, "创建场景A脚本");
  const r = await runScript(created.id);
  assert(r.dispatchOk && r.ended, "场景A运行结束");
  assert(r.rec && r.rec.finalStatus === "success", "一轮成功（FinalStatus=success，实际 " + r.rec?.finalStatus + "）");
  assert(r.rec && r.rec.attempts === 1, "尝试次数=1（实际 " + r.rec?.attempts + "）");
  await waitFor(() => hookBodies.some(b => b.includes("所有任务已全部完成")), 8000);
  assert(hookBodies.some(b => b.includes("所有任务已全部完成")), "webhook 收到通知「所有任务已全部完成」（CustomNotifyText 生效）");
  await api("DELETE", "/api/scripts/" + created.id);
}

async function testScenarioB() {
  console.log("[用例] 场景B：任务2失败 → 替换配置重试（只跑失败任务）→ 成功 + 通知");
  const dir = makeMultiTaskDir("b", ["1|enabled|success", "2|enabled|fail", "3|disabled|success", "4|enabled|success"]);
  const created = await createJudgeScript({
    name: "场景B失败重试", rootPath: dir, mainExe: path.join(dir, "nexusmt-b.bat"),
    configPath: dir, logPath: path.join(dir, "logs\\log.txt"),
    maxAttempts: 3, judgeScriptEnabled: true, judgeScriptLanguage: "javascript", judgeScript: defaultJudgeScript(),
    notifyEnabled: true,
  });
  assert(created.ok, "创建场景B脚本");
  const r = await runScript(created.id);
  assert(r.dispatchOk && r.ended, "场景B运行结束");
  assert(r.rec && r.rec.attempts === 2, "失败后重试成功（attempts=2，实际 " + r.rec?.attempts + "）");
  assert(r.rec && r.rec.finalStatus === "partial", "重试>1 → FinalStatus=partial（实际 " + r.rec?.finalStatus + "）");
  const doneDetail = r.rec?.attemptDetails?.some(a => a.status === "success");
  assert(doneDetail, "第二次尝试判定成功");
  await waitFor(() => hookBodies.some(b => b.includes("所有任务已全部完成")), 8000);
  assert(hookBodies.some(b => b.includes("所有任务已全部完成")), "webhook 收到最终通知「所有任务已全部完成」");
  const cfgAfter = fs.readFileSync(path.join(dir, "tasks.txt"), "utf8");
  const restored = cfgAfter.includes("1|enabled|success") && cfgAfter.includes("2|enabled|fail") && cfgAfter.includes("4|enabled|success");
  assert(restored, "运行结束后 config/tasks.txt 还原为启动前状态（实际：" + cfgAfter.split("\r\n").join("; ") + "）");
  assert(!fs.existsSync(userScriptDir(created.id)), "运行结束后 script 目录已清空");
  assert(!fs.existsSync(userBackupDir(created.id)), "运行结束后 swap-backup 已清理");
  await api("DELETE", "/api/scripts/" + created.id);
}

async function testScenarioC() {
  console.log("[用例] 场景C：任务2卡住（曾输出日志）→ 周期触发替换 → 任务4再卡 → 3次失败 + 通知「任务4运行失败」");
  const dir = makeMultiTaskDir("c", ["1|enabled|success", "2|enabled|stuck-alt", "3|disabled|success", "4|enabled|stuck-silent"]);
  const created = await createJudgeScript({
    name: "场景C卡住重试", rootPath: dir, mainExe: path.join(dir, "nexusmt-c.bat"),
    configPath: dir, logPath: path.join(dir, "logs\\log.txt"),
    maxAttempts: 3, judgeScriptEnabled: true, judgeScriptLanguage: "javascript", judgeScript: defaultJudgeScript(),
    notifyEnabled: true,
  });
  assert(created.ok, "创建场景C脚本");
  const t0 = Date.now();
  const r = await runScript(created.id, undefined, 240000);
  assert(r.dispatchOk && r.ended, "场景C运行结束（耗时 " + Math.round((Date.now() - t0) / 1000) + "s）");
  assert(r.rec && r.rec.attempts === 3, "达到最大尝试次数（attempts=3，实际 " + r.rec?.attempts + "）");
  assert(r.rec && r.rec.finalStatus === "failed", "最终失败（FinalStatus=failed，实际 " + r.rec?.finalStatus + "）");
  const reasons = (r.rec?.attemptDetails || []).map(a => a.reason);
  assert(reasons.some(x => /task-stuck-2|任务2|卡住/.test(x)), "尝试1因任务2卡住失败（原因：" + JSON.stringify(reasons) + "）");
  assert(reasons.some(x => /task-stuck-4|任务4|卡住/.test(x)), "尝试2/3因任务4卡住失败");
  await waitFor(() => hookBodies.some(b => b.includes("任务4运行失败")), 8000);
  assert(hookBodies.some(b => b.includes("任务4运行失败")), "webhook 收到通知「任务4运行失败」（最后一次尝试 notifyText）");
  const cfgAfter = fs.readFileSync(path.join(dir, "tasks.txt"), "utf8");
  const restored = cfgAfter.includes("1|enabled|success") && cfgAfter.includes("2|enabled|stuck-alt") && cfgAfter.includes("4|enabled|stuck-silent");
  assert(restored, "运行结束后 tasks.txt 还原为启动前状态（实际：" + cfgAfter.split("\r\n").join("; ") + "）");
  assert(!fs.existsSync(userScriptDir(created.id)), "script 目录已清空");
  assert(!fs.existsSync(userBackupDir(created.id)), "swap-backup 已清理");
  await api("DELETE", "/api/scripts/" + created.id);
}

/* ---------------- MaaEnd 专项判断脚本场景（v0.6.1） ---------------- */

/**
 * MaaEnd 默认判断脚本：从数据化插件目录 plugins/maaend/data/judge.js 读取
 * （v0.6.3 起判断脚本为独立文件，保证测试用的即发布代码）。
 */
function maaendJudgeScript() {
  const file = path.join(projectRoot, "plugins", "maaend", "data", "judge.js");
  if (!fs.existsSync(file)) throw new Error("无法读取 plugins/maaend/data/judge.js");
  return fs.readFileSync(file, "utf8");
}

/**
 * MaaEnd 模拟脚本（node 伪脚本，UTF-8 写日志规避 bat 中文编码问题）：
 *  - retry 模式：奇数次运行输出「售卖产品完成 + 高阶培养四失败 + 环境监测完成」（模拟 MXU 失败不中断收尾），
 *    偶数次只输出「高阶培养四完成」（模拟配置改写后仅失败任务启用重跑）；
 *  - all-ok 模式：全部任务完成；
 *  - unknown 模式：含「任务失败: 未知任务」（无法映射 → 判断脚本保守不改写）。
 * 日志行模拟 MXU 格式：[YYYY-MM-DD HH:mm:ss.fff] 任务开始/完成/失败: <显示名>
 */
const FAKE_MAAEND_JS = `
const fs = require("fs");
const path = require("path");
const mode = process.argv[2] || "retry";
const logFile = path.join(__dirname, "logs", "log.txt");
const cntFile = path.join(__dirname, "cnt.txt");
let n = 0;
try { n = Number(fs.readFileSync(cntFile, "utf8").trim()) || 0; } catch (e) { n = 0; }
n += 1;
fs.writeFileSync(cntFile, String(n));
// v0.7.6：模拟游戏脚本自身向 config 写运行计数（自动更新配置收尾同步应保留该字段到快照）。
const cfgPath = path.join(__dirname, "config", "mxu-MaaEnd.json");
try {
  const cfg = JSON.parse(fs.readFileSync(cfgPath, "utf8"));
  cfg.stats = cfg.stats || { runCount: 0 };
  cfg.stats.runCount = (cfg.stats.runCount || 0) + 1;
  fs.writeFileSync(cfgPath, JSON.stringify(cfg, null, 2), "utf8");
} catch (e) { /* 配置缺失/损坏时跳过计数 */ }
fs.mkdirSync(path.dirname(logFile), { recursive: true });
const odd = [
  "任务开始: 🛒售卖产品", "任务完成: 🛒售卖产品",
  "任务开始: 高阶培养四", "任务失败: 高阶培养四",
  "任务开始: 🌿环境监测", "任务完成: 🌿环境监测"
];
const even = ["任务开始: 高阶培养四", "任务完成: 高阶培养四"];
const allOk = [
  "任务开始: 🛒售卖产品", "任务完成: 🛒售卖产品",
  "任务开始: 🌿环境监测", "任务完成: 🌿环境监测"
];
const unknown = [
  "任务开始: 🛒售卖产品", "任务完成: 🛒售卖产品",
  "任务开始: 未知任务", "任务失败: 未知任务",
  "任务开始: 🌿环境监测", "任务完成: 🌿环境监测"
];
const pick = mode === "all-ok" ? allOk : mode === "unknown" ? unknown : (n % 2 === 1 ? odd : even);
fs.appendFileSync(logFile, pick.map(l => "[2026-08-13 06:00:00.000] " + l).join("\\r\\n") + "\\r\\n", "utf8");
setTimeout(() => process.exit(0), 1200);
`;

/** MaaEnd 模拟配置（真实 mxu-MaaEnd.json 结构：instances[].tasks + settings.autoStartInstanceId）。 */
const MAAEND_CFG = {
  version: "1.0",
  stats: { runCount: 0 },
  instances: [
    {
      id: "automas",
      name: "AUTO-MAS",
      controllerName: "Win32-Front",
      tasks: [
        { id: "t1", taskName: "SellProduct", enabled: true, enabledByController: { "Win32-Front": true }, optionValues: {} },
        { id: "t2", taskName: "ProtocolSpace", customName: "高阶培养四", enabled: true, enabledByController: { "Win32-Front": true }, optionValues: {} },
        { id: "t3", taskName: "EnvironmentMonitoring", enabled: true, enabledByController: { "Win32-Front": true }, optionValues: {} },
      ],
    },
  ],
  settings: { autoStartInstanceId: "automas", language: "zh-CN" },
};

function makeMaaEndDir(label) {
  const dir = path.join(runtimeDir, "mt-maaend-" + label);
  fs.rmSync(dir, { recursive: true, force: true });
  fs.mkdirSync(path.join(dir, "logs"), { recursive: true });
  fs.mkdirSync(path.join(dir, "config"), { recursive: true });
  fs.writeFileSync(path.join(dir, "fake-maaend.js"), FAKE_MAAEND_JS, "utf8");
  // 复制 node.exe 为 fake-node.exe：避免与测试进程（node.exe）同名触发宿主「主程序已在运行」门禁，
  // 且伪脚本可用 node 原生 fs/path 以 UTF-8 写日志（规避 bat 中文编码问题）。
  fs.copyFileSync(process.execPath, path.join(dir, "fake-node.exe"));
  const cfgPath = path.join(dir, "config", "mxu-MaaEnd.json");
  fs.writeFileSync(cfgPath, JSON.stringify(MAAEND_CFG, null, 2), "utf8");
  fs.rmSync(path.join(dir, "cnt.txt"), { force: true });
  fs.rmSync(path.join(dir, "logs", "log.txt"), { force: true });
  return { dir, cfgPath, cfgBefore: fs.readFileSync(cfgPath, "utf8") };
}

async function testMaaEndJudgeScript() {
  console.log("[用例] MaaEnd 判断脚本（v0.6.1）：失败任务选择性重试 + 运行还原 / 全成功单轮 / 未知失败名保守不改写");

  // 子场景 1：失败任务选择性重试（attempt1 失败 → 改写配置 → attempt2 仅重跑失败任务成功）
  const s1 = makeMaaEndDir("retry");
  const c1 = await createJudgeScript({
    name: "MaaEnd失败重试", rootPath: s1.dir,
    mainExe: path.join(s1.dir, "fake-node.exe").replace(/\\/g, "\\\\"), args: "fake-maaend.js retry",
    configPath: path.join(s1.dir, "config"), logPath: path.join(s1.dir, "logs\\log.txt"),
    maxAttempts: 3, judgeScriptEnabled: true, judgeScriptLanguage: "javascript", judgeScript: maaendJudgeScript(),
  });
  assert(c1.ok, "创建 MaaEnd 模拟脚本（node 伪脚本 + 插件提取判断脚本）");
  const r1 = await runScript(c1.id);
  assert(r1.dispatchOk && r1.ended, "MaaEnd 失败重试运行结束");
  assert(r1.rec && r1.rec.attempts === 2, "失败后仅重跑失败任务成功（attempts=2，实际 " + r1.rec?.attempts + "）");
  assert(r1.rec && r1.rec.finalStatus === "partial", "重试>1 → FinalStatus=partial（实际 " + r1.rec?.finalStatus + "）");
  assert((r1.rec?.attemptDetails || [])[0]?.reason.includes("高阶培养四"), "首次尝试原因含失败任务（已调整为仅重试失败任务，原因：" + (r1.rec?.attemptDetails || [])[0]?.reason + "）");
  assert(fs.readFileSync(s1.cfgPath, "utf8") === s1.cfgBefore, "运行结束后 config/mxu-MaaEnd.json 还原为运行前状态");
  assert(!fs.existsSync(userScriptDir(c1.id)), "script 目录已清空");
  assert(!fs.existsSync(userBackupDir(c1.id)), "swap-backup 已清理");
  await api("DELETE", "/api/scripts/" + c1.id);

  // 子场景 2：全部任务成功 → 单轮 success
  const s2 = makeMaaEndDir("ok");
  const c2 = await createJudgeScript({
    name: "MaaEnd全成功", rootPath: s2.dir,
    mainExe: path.join(s2.dir, "fake-node.exe").replace(/\\/g, "\\\\"), args: "fake-maaend.js all-ok",
    configPath: path.join(s2.dir, "config"), logPath: path.join(s2.dir, "logs\\log.txt"),
    maxAttempts: 2, judgeScriptEnabled: true, judgeScriptLanguage: "javascript", judgeScript: maaendJudgeScript(),
  });
  assert(c2.ok, "创建 MaaEnd 全成功模拟脚本");
  const r2 = await runScript(c2.id);
  assert(r2.dispatchOk && r2.ended, "MaaEnd 全成功运行结束");
  assert(r2.rec && r2.rec.attempts === 1, "全成功单轮（attempts=1，实际 " + r2.rec?.attempts + "）");
  assert(r2.rec && r2.rec.finalStatus === "success", "全部任务执行成功（FinalStatus=success，实际 " + r2.rec?.finalStatus + "）");
  await api("DELETE", "/api/scripts/" + c2.id);

  // 子场景 3：未知失败名 → failed 且不改写配置（保守分支）
  const s3 = makeMaaEndDir("unknown");
  const c3 = await createJudgeScript({
    name: "MaaEnd未知失败", rootPath: s3.dir,
    mainExe: path.join(s3.dir, "fake-node.exe").replace(/\\/g, "\\\\"), args: "fake-maaend.js unknown",
    configPath: path.join(s3.dir, "config"), logPath: path.join(s3.dir, "logs\\log.txt"),
    maxAttempts: 1, judgeScriptEnabled: true, judgeScriptLanguage: "javascript", judgeScript: maaendJudgeScript(),
  });
  assert(c3.ok, "创建 MaaEnd 未知失败名模拟脚本");
  const r3 = await runScript(c3.id);
  assert(r3.dispatchOk && r3.ended, "MaaEnd 未知失败名运行结束");
  assert(r3.rec && r3.rec.finalStatus === "failed", "无法映射失败名 → 判定失败（FinalStatus=failed，实际 " + r3.rec?.finalStatus + "）");
  assert(r3.rec && r3.rec.attempts === 1, "保守分支不再重试（attempts=1，实际 " + r3.rec?.attempts + "）");
  assert(fs.readFileSync(s3.cfgPath, "utf8") === s3.cfgBefore, "无法映射时配置未被改写（mxu-MaaEnd.json 原样）");
  await api("DELETE", "/api/scripts/" + c3.id);

  // 子场景 4（v0.7.6）：自动更新配置开——失败重试 + 启停还原后快照同步（enabled=初始、runCount=最终时点）
  const s4 = makeMaaEndDir("autoupdate");
  const c4 = await createJudgeScript({
    name: "MaaEnd自动更新配置", rootPath: s4.dir,
    mainExe: path.join(s4.dir, "fake-node.exe").replace(/\\/g, "\\\\"), args: "fake-maaend.js retry",
    configPath: path.join(s4.dir, "config"), logPath: path.join(s4.dir, "logs\\log.txt"),
    maxAttempts: 3, judgeScriptEnabled: true, judgeScriptLanguage: "javascript", judgeScript: maaendJudgeScript(),
    autoUpdateConfig: true,
  });
  assert(c4.ok, "创建 MaaEnd 自动更新配置脚本");
  const r4 = await runScript(c4.id);
  assert(r4.dispatchOk && r4.ended, "MaaEnd 自动更新配置运行结束");
  assert(r4.rec && r4.rec.attempts === 2, "失败重试（attempts=2，实际 " + r4.rec?.attempts + "）");
  assert(fs.readFileSync(s4.cfgPath, "utf8") === s4.cfgBefore, "运行结束后 config/mxu-MaaEnd.json 还原为运行前状态");
  const store4 = path.join(runtimeDir, "data", c4.id, "默认", "store", "mxu-MaaEnd.json");
  assert(fs.existsSync(store4), "运行结束后 store 快照存在 mxu-MaaEnd.json");
  const stored4 = JSON.parse(fs.readFileSync(store4, "utf8"));
  assert((stored4.instances[0].tasks || []).every(t => t.enabled === true),
    "store 快照启停还原为初始（全部 enabled=true）");
  assert(stored4.stats && stored4.stats.runCount === 2,
    "store 快照保留运行后计数（runCount=2，实际 " + (stored4.stats?.runCount ?? "无") + "）");
  await api("DELETE", "/api/scripts/" + c4.id);
}

/* ---------------- 自动更新配置（v0.7.6） ---------------- */

/** 计数脚本目录：config/count.txt 初始 "0"；batLines 由调用方指定（延迟写计数 / 立即写计数+长卡住）。 */
function makeCounterDir(label, batLines) {
  const dir = path.join(runtimeDir, "mt-au-" + label);
  fs.rmSync(dir, { recursive: true, force: true });
  fs.mkdirSync(path.join(dir, "config"), { recursive: true });
  fs.mkdirSync(path.join(dir, "logs"), { recursive: true });
  fs.writeFileSync(path.join(dir, "config", "count.txt"), "0", "ascii");
  fs.writeFileSync(path.join(dir, "nexusmt-au-" + label + ".bat"), batLines.join("\r\n") + "\r\n", "ascii");
  return dir;
}

async function testAutoUpdateConfig() {
  console.log("[用例] 自动更新配置（v0.7.6）：开=收尾同步（成功/cancelled）/ 关=仅首次同步 / 通用判断脚本插队文件不写快照");

  // 1. 开 + 关键字模式：收尾同步（store 保留运行后计数），config 还原
  //    脚本延迟写计数（先 ping 4s）→ 首次检测（1s）捕获写前状态，收尾同步（~4s）覆盖为最终计数。
  const d1 = makeCounterDir("on", [
    "@echo off", 'cd /d "%~dp0"',
    "ping -n " + (FAST ? 5 : 40) + " 127.0.0.1 >nul",
    "> config\\count.txt echo 1",
    "echo DONE >> logs\\log.txt",
    "exit /b 0",
  ]);
  const c1 = await createJudgeScript({
    name: "自动更新配置开", rootPath: d1,
    mainExe: path.join(d1, "nexusmt-au-on.bat"),
    configPath: path.join(d1, "config"), logPath: path.join(d1, "logs\\log.txt"),
    successKeywords: "DONE", autoUpdateConfig: true,
  });
  assert(c1.ok, "创建自动更新配置开脚本");
  const r1 = await runScript(c1.id);
  assert(r1.dispatchOk && r1.ended, "开：运行结束");
  assert(r1.rec && r1.rec.finalStatus === "success", "开：判定成功（FinalStatus=" + r1.rec?.finalStatus + "）");
  const store1 = path.join(runtimeDir, "data", c1.id, "默认", "store", "count.txt");
  assert(fs.existsSync(store1) && fs.readFileSync(store1, "utf8").trim() === "1",
    "开：收尾同步 → store/count.txt=1（运行后计数入库）");
  assert(fs.readFileSync(path.join(d1, "config", "count.txt"), "utf8") === "0",
    "开：config 还原为运行前（count=0）");
  await api("DELETE", "/api/scripts/" + c1.id);

  // 2. 关 + 关键字模式：收尾不同步（store 保持首次检测后的写前计数 0）
  const d2 = makeCounterDir("off", [
    "@echo off", 'cd /d "%~dp0"',
    "ping -n " + (FAST ? 5 : 40) + " 127.0.0.1 >nul",
    "> config\\count.txt echo 1",
    "echo DONE >> logs\\log.txt",
    "exit /b 0",
  ]);
  const c2 = await createJudgeScript({
    name: "自动更新配置关", rootPath: d2,
    mainExe: path.join(d2, "nexusmt-au-off.bat"),
    configPath: path.join(d2, "config"), logPath: path.join(d2, "logs\\log.txt"),
    successKeywords: "DONE", autoUpdateConfig: false,
  });
  assert(c2.ok, "创建自动更新配置关脚本");
  const r2 = await runScript(c2.id);
  assert(r2.dispatchOk && r2.ended, "关：运行结束");
  assert(r2.rec && r2.rec.finalStatus === "success", "关：判定成功（FinalStatus=" + r2.rec?.finalStatus + "）");
  const store2 = path.join(runtimeDir, "data", c2.id, "默认", "store", "count.txt");
  assert(fs.existsSync(store2) && fs.readFileSync(store2, "utf8") === "0",
    "关：收尾不同步 → store/count.txt 保持 0（首次检测在写计数前，运行计数不入库）");
  await api("DELETE", "/api/scripts/" + c2.id);

  // 3. 开 + cancelled：收尾同步包含取消（脚本先写计数再长卡住，取消后计数入库）
  const d3 = makeCounterDir("cancel", [
    "@echo off", 'cd /d "%~dp0"',
    "> config\\count.txt echo 1",
    "ping -n " + (FAST ? 120 : 600) + " 127.0.0.1 >nul",
    "exit /b 0",
  ]);
  const c3 = await createJudgeScript({
    name: "自动更新配置取消", rootPath: d3,
    mainExe: path.join(d3, "nexusmt-au-cancel.bat"),
    configPath: path.join(d3, "config"), logPath: path.join(d3, "logs\\log.txt"),
    autoUpdateConfig: true,
  });
  assert(c3.ok, "创建自动更新配置取消脚本");
  const dispatch3 = await api("POST", "/api/dispatch/script", { scriptId: c3.id, mode: "manual" });
  assert(dispatch3.ok, "取消用例 dispatch 成功");
  const runId3 = (await dispatch3.json()).runId;
  await waitFor(async () => (await runningCount()) > 0, 15000);
  await sleep(1500);
  const cancel3 = await api("POST", "/api/cancel", { runId: runId3 });
  assert(cancel3.ok, "取消提交成功");
  const ended3 = await waitNoRunning(30000);
  assert(ended3, "取消后运行结束");
  const store3 = path.join(runtimeDir, "data", c3.id, "默认", "store", "count.txt");
  assert(fs.existsSync(store3) && fs.readFileSync(store3, "utf8").trim() === "1",
    "开+cancelled：收尾同步 → store/count.txt=1（含取消）");
  await api("DELETE", "/api/scripts/" + c3.id);

  // 4. 开 + 通用判断脚本插队文件：无还原描述 → 不写快照；非插队文件照常同步
  const d4 = path.join(runtimeDir, "mt-au-swap");
  fs.rmSync(d4, { recursive: true, force: true });
  fs.mkdirSync(path.join(d4, "config"), { recursive: true });
  fs.mkdirSync(path.join(d4, "logs"), { recursive: true });
  fs.writeFileSync(path.join(d4, "config", "cfg.txt"), "ORIG", "ascii");
  fs.writeFileSync(path.join(d4, "config", "other.txt"), "O", "ascii");
  fs.writeFileSync(path.join(d4, "nexusmt-au-swap.bat"),
    ["@echo off", 'cd /d "%~dp0"',
      "> config\\other.txt echo N",
      "echo RUN >> logs\\log.txt",
      "exit /b 0"].join("\r\n") + "\r\n", "ascii");
  const jsSwap = `
nexus.writeFile("cfg.txt", "SWAPPED");
console.log(JSON.stringify({ status: "failed", reason: "swap-requested", replaceConfigs: ["cfg.txt"] }));`;
  const c4 = await createJudgeScript({
    name: "通用插队快照", rootPath: d4,
    mainExe: path.join(d4, "nexusmt-au-swap.bat"),
    configPath: path.join(d4, "config"), logPath: path.join(d4, "logs\\log.txt"),
    judgeScriptEnabled: true, judgeScriptLanguage: "javascript", judgeScript: jsSwap,
    maxAttempts: 1, autoUpdateConfig: true,
  });
  assert(c4.ok, "创建通用插队脚本");
  const r4 = await runScript(c4.id);
  assert(r4.dispatchOk && r4.ended, "通用插队：运行结束");
  const store4 = path.join(runtimeDir, "data", c4.id, "默认", "store");
  assert(fs.readFileSync(path.join(store4, "cfg.txt"), "utf8") === "ORIG",
    "通用插队文件无还原描述 → store/cfg.txt 保持 ORIG（不写插队编排）");
  assert(fs.readFileSync(path.join(store4, "other.txt"), "utf8").trim() === "N",
    "非插队文件照常同步 → store/other.txt=N");
  await api("DELETE", "/api/scripts/" + c4.id);
}

/** v0.7.6 评估（ASSESSMENT 更新）：收尾同步内容有效性守护——单文件 JSON 半写/损坏不入库（保留旧快照），合法 JSON 照常入库。 */
async function testAutoUpdateConfigContentGuard() {
  console.log("[用例] 自动更新配置内容守护：半写 JSON 不入库 / 合法 JSON 照常入库");

  // 1. 脚本写坏 JSON（模拟写入中断半写）→ 收尾同步跳过 → store 保持初始快照
  const d1 = path.join(runtimeDir, "mt-au-badjson");
  fs.rmSync(d1, { recursive: true, force: true });
  fs.mkdirSync(path.join(d1, "config"), { recursive: true });
  fs.mkdirSync(path.join(d1, "logs"), { recursive: true });
  fs.writeFileSync(path.join(d1, "config", "cfg.json"), '{"count":0}', "ascii");
  fs.writeFileSync(path.join(d1, "nexusmt-au-badjson.bat"),
    ["@echo off", 'cd /d "%~dp0"',
      "ping -n " + (FAST ? 5 : 40) + " 127.0.0.1 >nul",
      "> config\\cfg.json echo {\"count\":",
      "echo DONE >> logs\\log.txt",
      "exit /b 0"].join("\r\n") + "\r\n", "ascii");
  const c1 = await createJudgeScript({
    name: "半写JSON不入库", rootPath: d1,
    mainExe: path.join(d1, "nexusmt-au-badjson.bat"),
    configPath: path.join(d1, "config", "cfg.json"), logPath: path.join(d1, "logs\\log.txt"),
    successKeywords: "DONE", autoUpdateConfig: true,
  });
  assert(c1.ok, "创建半写 JSON 脚本");
  const r1 = await runScript(c1.id);
  assert(r1.dispatchOk && r1.ended, "半写：运行结束");
  assert(r1.rec && r1.rec.finalStatus === "success", "半写：判定成功（FinalStatus=" + r1.rec?.finalStatus + "）");
  const store1 = path.join(runtimeDir, "data", c1.id, "默认", "store", "cfg.json");
  assert(fs.existsSync(store1) && fs.readFileSync(store1, "utf8").trim() === '{"count":0}',
    "半写：store/cfg.json 保持初始快照（损坏内容未入库，实际：" + (fs.existsSync(store1) ? fs.readFileSync(store1, "utf8").trim() : "无") + "）");
  assert(fs.readFileSync(path.join(d1, "config", "cfg.json"), "utf8").trim() === '{"count":0}',
    "半写：config 还原为运行前（count=0）");
  await api("DELETE", "/api/scripts/" + c1.id);

  // 2. 脚本写合法 JSON → 收尾同步照常入库（运行后计数保留）
  const d2 = path.join(runtimeDir, "mt-au-goodjson");
  fs.rmSync(d2, { recursive: true, force: true });
  fs.mkdirSync(path.join(d2, "config"), { recursive: true });
  fs.mkdirSync(path.join(d2, "logs"), { recursive: true });
  fs.writeFileSync(path.join(d2, "config", "cfg.json"), '{"count":0}', "ascii");
  fs.writeFileSync(path.join(d2, "nexusmt-au-goodjson.bat"),
    ["@echo off", 'cd /d "%~dp0"',
      "ping -n " + (FAST ? 5 : 40) + " 127.0.0.1 >nul",
      "> config\\cfg.json echo {\"count\":5}",
      "echo DONE >> logs\\log.txt",
      "exit /b 0"].join("\r\n") + "\r\n", "ascii");
  const c2 = await createJudgeScript({
    name: "合法JSON入库", rootPath: d2,
    mainExe: path.join(d2, "nexusmt-au-goodjson.bat"),
    configPath: path.join(d2, "config", "cfg.json"), logPath: path.join(d2, "logs\\log.txt"),
    successKeywords: "DONE", autoUpdateConfig: true,
  });
  assert(c2.ok, "创建合法 JSON 脚本");
  const r2 = await runScript(c2.id);
  assert(r2.dispatchOk && r2.ended, "合法：运行结束");
  assert(r2.rec && r2.rec.finalStatus === "success", "合法：判定成功（FinalStatus=" + r2.rec?.finalStatus + "）");
  const store2 = path.join(runtimeDir, "data", c2.id, "默认", "store", "cfg.json");
  assert(fs.existsSync(store2) && fs.readFileSync(store2, "utf8").trim() === '{"count":5}',
    "合法：store/cfg.json 更新为运行后计数（实际：" + (fs.existsSync(store2) ? fs.readFileSync(store2, "utf8").trim() : "无") + "）");
  assert(fs.readFileSync(path.join(d2, "config", "cfg.json"), "utf8").trim() === '{"count":0}',
    "合法：config 还原为运行前（count=0）");
  await api("DELETE", "/api/scripts/" + c2.id);
}

async function testEdgeNoLogStuck() {
  console.log("[用例] 边缘：完全无日志卡住 → 日志超时后最终触发一次判断脚本");
  const dir = path.join(runtimeDir, "mt-silent");
  fs.rmSync(dir, { recursive: true, force: true });
  fs.mkdirSync(path.join(dir, "logs"), { recursive: true });
  fs.writeFileSync(path.join(dir, "nexusmt-silent.bat"), ["@echo off", "ping -n " + (FAST ? 10 : 200) + " 127.0.0.1 >nul", "exit /b 0"].join("\r\n") + "\r\n", "ascii");
  const countJs = `
const input = JSON.parse(__NEXUS_INPUT__);
const countFile = (input.files || []).find(f => f.Root === "script" && f.Path === "count");
const n = Number(countFile ? nexus.readFile(countFile.Abs) || "0" : "0") + 1;
nexus.writeFile("count", String(n));`;
  const created = await createJudgeScript({
    name: "零日志卡住", rootPath: dir, mainExe: path.join(dir, "nexusmt-silent.bat"),
    configPath: dir, logPath: path.join(dir, "logs\\log.txt"),
    maxAttempts: 2, logStallTimeoutMinutes: 1, totalTimeoutMinutes: FAST ? 30 : 5,
    judgeScriptEnabled: true, judgeScriptLanguage: "javascript", judgeScript: countJs,
  });
  assert(created.ok, "创建零日志卡住脚本（stall=1 分钟）");
  const dispatch = await api("POST", "/api/dispatch/script", { scriptId: created.id, mode: "manual" });
  assert(dispatch.ok, "dispatch 成功");
  const scriptDir = userScriptDir(created.id);
  const countFile = path.join(scriptDir, "count");
  let maxCount = 0;
  // 密集采样探针（v0.6.2 加速档）：每次尝试 stall 触发最终判定写入 count 的存活窗口仅 taskkill 耗时（数百毫秒），
  // 采样间隔必须远小于窗口；running 为 0 时二次确认再 break，避免错过最后一次写入。
  const probeDeadline = Date.now() + 215000;
  while (Date.now() < probeDeadline) {
    if ((await runningCount()) === 0) {
      await sleep(300);
      if ((await runningCount()) === 0) break;
    }
    if (fs.existsSync(countFile)) {
      maxCount = Math.max(maxCount, Number(fs.readFileSync(countFile, "utf8").trim()) || 0);
    }
    await sleep(20);
  }
  const ended = await waitNoRunning(30000);
  const hist = await (await fetch(baseUrl + "api/history?days=7")).json();
  const rec = hist.filter(h => h.scriptInstanceId === created.id).at(-1);
  assert(ended, "运行结束");
  assert(maxCount === (rec?.attempts || 0),
    "每次尝试的日志超时路径都触发一次最终判定（count=" + maxCount + "，attempts=" + rec?.attempts + "，判断脚本有机会应用替换配置）");
  assert(rec && rec.attempts === 2 && (rec.attemptDetails || []).every(a => /未产生日志条目|无更新/.test(a.reason)),
    "判断脚本无判定结果 → 两次尝试均因日志超时失败（attempts=" + rec?.attempts + "，原因：" + JSON.stringify((rec?.attemptDetails || []).map(a => a.reason)) + "）");
  await api("DELETE", "/api/scripts/" + created.id);
}

async function testBugNewFileResidue() {
  console.log("[用例] 修复验证：replaceConfigs 指向 config 中不存在的新文件 → 运行后清理");
  const dir = path.join(runtimeDir, "mt-newfile");
  fs.rmSync(dir, { recursive: true, force: true });
  fs.mkdirSync(path.join(dir, "logs"), { recursive: true });
  fs.writeFileSync(path.join(dir, "nexusmt-newfile.bat"), ["@echo off", "echo HELLO >> logs\\log.txt", "ping -n 3 127.0.0.1 >nul", "exit /b 0"].join("\r\n") + "\r\n", "ascii");
  const jsNew = `
nexus.writeFile("new-cfg.ini", "KEY=1");
console.log(JSON.stringify({ status: "failed", reason: "replace-new-file", replaceConfigs: ["new-cfg.ini"] }));`;
  const created = await createJudgeScript({
    name: "新文件替换", rootPath: dir, mainExe: path.join(dir, "nexusmt-newfile.bat"),
    configPath: dir, logPath: path.join(dir, "logs\\log.txt"),
    judgeScriptEnabled: true, judgeScriptLanguage: "javascript", judgeScript: jsNew,
  });
  assert(created.ok, "创建新文件替换脚本");
  const r = await runScript(created.id);
  assert(r.dispatchOk && r.ended, "运行结束");
  assert(!fs.existsSync(path.join(dir, "new-cfg.ini")), "替换产生的 config 新文件运行后已清理（无残留）");
  assert(r.rec && r.rec.finalStatus === "failed", "判定失败（FinalStatus=" + r.rec?.finalStatus + "）");
  await api("DELETE", "/api/scripts/" + created.id);
}

async function testBugPathEscape() {
  console.log("[用例] 修复验证：nexus.writeFile 相对路径逃逸（../script-evil/x.txt）被拒绝");
  const dir = path.join(runtimeDir, "mt-escape");
  fs.rmSync(dir, { recursive: true, force: true });
  fs.mkdirSync(path.join(dir, "logs"), { recursive: true });
  fs.writeFileSync(path.join(dir, "nexusmt-escape.bat"), ["@echo off", "echo HELLO >> logs\\log.txt", "ping -n 3 127.0.0.1 >nul", "exit /b 0"].join("\r\n") + "\r\n", "ascii");
  const jsEsc = `
const ok = nexus.writeFile("../script-evil/x.txt", "EVIL");
console.log(JSON.stringify({ status: ok ? "failed" : "success", reason: ok ? "escape-succeeded" : "escape-blocked" }));`;
  const created = await createJudgeScript({
    name: "路径逃逸", rootPath: dir, mainExe: path.join(dir, "nexusmt-escape.bat"),
    configPath: dir, logPath: path.join(dir, "logs\\log.txt"),
    judgeScriptEnabled: true, judgeScriptLanguage: "javascript", judgeScript: jsEsc,
  });
  assert(created.ok, "创建路径逃逸脚本");
  const r = await runScript(created.id);
  assert(r.dispatchOk && r.ended, "运行结束");
  const scriptParent = path.join(runtimeDir, "data", created.id, "默认");
  const evil = path.join(scriptParent, "script-evil", "x.txt");
  assert(!fs.existsSync(evil), "越界写入被拒绝（script-evil/x.txt 不存在）");
  assert(r.rec && r.rec.finalStatus === "success", "逃逸被拒绝 → 判断脚本判定 success（FinalStatus=" + r.rec?.finalStatus + "）");
  await api("DELETE", "/api/scripts/" + created.id);
  fs.rmSync(path.join(scriptParent, "script-evil"), { recursive: true, force: true });
}

async function testBugMarkerReTrigger() {
  console.log("[用例] 修复验证：成功判定（marker）后等待退出期间不再触发判断脚本");
  const dir = path.join(runtimeDir, "mt-extra");
  fs.rmSync(dir, { recursive: true, force: true });
  fs.mkdirSync(path.join(dir, "logs"), { recursive: true });
  fs.writeFileSync(path.join(dir, "tasks.txt"), "1|enabled|success\r\n", "ascii");
  fs.writeFileSync(path.join(dir, "nexusmt-extra.bat"), [
    "@echo off",
    "echo TASK 1 DONE >> logs\\log.txt",
    "ping -n " + CRASH_PINGS + " 127.0.0.1 >nul",
    "echo EXTRA 1 >> logs\\log.txt",
    "ping -n " + CRASH_PINGS + " 127.0.0.1 >nul",
    "echo EXTRA 2 >> logs\\log.txt",
    "exit /b 0",
  ].join("\r\n") + "\r\n", "ascii");
  const jsCount = `
const input = JSON.parse(__NEXUS_INPUT__);
const countFile = (input.files || []).find(f => f.Root === "script" && f.Path === "count");
const n = Number(countFile ? nexus.readFile(countFile.Abs) || "0" : "0") + 1;
nexus.writeFile("count", String(n));
console.log(JSON.stringify({ status: "success", reason: "ok" }));`;
  const created = await createJudgeScript({
    name: "marker重复触发", rootPath: dir, mainExe: path.join(dir, "nexusmt-extra.bat"),
    configPath: dir, logPath: path.join(dir, "logs\\log.txt"),
    judgeScriptEnabled: true, judgeScriptLanguage: "javascript", judgeScript: jsCount,
  });
  assert(created.ok, "创建 marker 重复触发脚本（DONE 后继续输出 2 批日志）");
  const dispatch = await api("POST", "/api/dispatch/script", { scriptId: created.id, mode: "manual" });
  assert(dispatch.ok, "dispatch 成功");
  const countFile = path.join(userScriptDir(created.id), "count");
  let maxCount = 0;
  const probeDeadline = Date.now() + 60000;
  while (Date.now() < probeDeadline && (await runningCount()) > 0) {
    if (fs.existsSync(countFile)) {
      maxCount = Math.max(maxCount, Number(fs.readFileSync(countFile, "utf8").trim()) || 0);
    }
    await sleep(150);
  }
  const ended = await waitNoRunning(30000);
  const hist = await (await fetch(baseUrl + "api/history?days=7")).json();
  const rec = hist.filter(h => h.scriptInstanceId === created.id).at(-1);
  assert(ended, "运行结束");
  assert(rec && rec.finalStatus === "success", "判定成功（FinalStatus=" + rec?.finalStatus + "）");
  assert(maxCount === 1, "成功判定后不再触发判断脚本（运行中观测触发次数=" + maxCount + "，期望 1；>1 = marker 后重复触发）");
  await api("DELETE", "/api/scripts/" + created.id);
}

async function testBugNotifyLostOnPostRun() {
  console.log("[用例] 修复验证：判断脚本 notifyText 不被 PostRunScript 失败覆盖（用户场景）");
  const dir = path.join(runtimeDir, "mt-postrun");
  fs.rmSync(dir, { recursive: true, force: true });
  fs.mkdirSync(path.join(dir, "logs"), { recursive: true });
  fs.writeFileSync(path.join(dir, "nexusmt-postrun.bat"), ["@echo off", "echo TASK 1 DONE >> logs\\log.txt", "exit /b 0"].join("\r\n") + "\r\n", "ascii");
  const postFailBat = path.join(runtimeDir, "post-fail.bat");
  fs.writeFileSync(postFailBat, "@echo off\r\nexit /b 1\r\n", "ascii");
  const jsOk = 'console.log(JSON.stringify({ status: "success", reason: "ok", notifyText: "所有任务已全部完成" }));';
  const created = await createJudgeScript({
    name: "PostRun覆盖通知", rootPath: dir, mainExe: path.join(dir, "nexusmt-postrun.bat"),
    configPath: dir, logPath: path.join(dir, "logs\\log.txt"),
    judgeScriptEnabled: true, judgeScriptLanguage: "javascript", judgeScript: jsOk,
    notifyEnabled: true,
  });
  assert(created.ok, "创建 PostRun 通知脚本");
  const addUser = await api("POST", "/api/scripts/" + created.id + "/users", {
    name: "甲", enabled: true, preRunScript: "", preRunOnceOnly: false,
    postRunScript: postFailBat, postRunOnFinalOnly: true,
  });
  assert(addUser.ok, "添加用户甲（PostRun 失败脚本）");
  const r = await runScript(created.id, "甲");
  assert(r.dispatchOk && r.ended, "运行结束");
  assert(r.rec && r.rec.finalStatus === "failed", "PostRun 失败 → 整体失败（FinalStatus=" + r.rec?.finalStatus + "）");
  await waitFor(() => hookBodies.length > 0, 8000);
  const lastHook = hookBodies.at(-1) || "";
  assert(lastHook.includes("所有任务已全部完成"), "通知保留判断脚本 notifyText（实际：" + lastHook.slice(0, 150) + "）");
  await api("DELETE", "/api/scripts/" + created.id);
}

async function testJudgeFaultTolerance() {
  console.log("[用例] 容错：判断脚本死循环超时 / 语法错误 / 非法 JSON 输出");

  const d1 = path.join(runtimeDir, "mt-hang");
  fs.rmSync(d1, { recursive: true, force: true });
  fs.mkdirSync(path.join(d1, "logs"), { recursive: true });
  fs.writeFileSync(path.join(d1, "nexusmt-hang.bat"), ["@echo off", "echo HELLO >> logs\\log.txt", "exit /b 0"].join("\r\n") + "\r\n", "ascii");
  const s1 = await createJudgeScript({
    name: "死循环判断脚本", rootPath: d1, mainExe: path.join(d1, "nexusmt-hang.bat"),
    configPath: d1, logPath: path.join(d1, "logs\\log.txt"),
    judgeScriptEnabled: true, judgeScriptLanguage: "javascript", judgeScript: "while (true) {}",
  });
  assert(s1.ok, "创建死循环判断脚本");
  let r = await runScript(s1.id);
  assert(r.dispatchOk && r.ended, "死循环脚本运行结束（未卡死）");
  assert(r.rec && r.rec.finalStatus === "failed", "死循环超时后判定失败（FinalStatus=" + r.rec?.finalStatus + "）");
  await api("DELETE", "/api/scripts/" + s1.id);

  const d2 = path.join(runtimeDir, "mt-syntax");
  fs.rmSync(d2, { recursive: true, force: true });
  fs.mkdirSync(path.join(d2, "logs"), { recursive: true });
  fs.writeFileSync(path.join(d2, "nexusmt-syntax.bat"), ["@echo off", "echo HELLO >> logs\\log.txt", "exit /b 0"].join("\r\n") + "\r\n", "ascii");
  const s2 = await createJudgeScript({
    name: "语法错误判断脚本", rootPath: d2, mainExe: path.join(d2, "nexusmt-syntax.bat"),
    configPath: d2, logPath: path.join(d2, "logs\\log.txt"),
    judgeScriptEnabled: true, judgeScriptLanguage: "javascript", judgeScript: "const x = ;",
  });
  assert(s2.ok, "创建语法错误判断脚本");
  r = await runScript(s2.id);
  assert(r.rec && r.rec.finalStatus === "failed", "语法错误视为继续运行 → 进程退出判定失败");
  await api("DELETE", "/api/scripts/" + s2.id);

  const d3 = path.join(runtimeDir, "mt-badjson");
  fs.rmSync(d3, { recursive: true, force: true });
  fs.mkdirSync(path.join(d3, "logs"), { recursive: true });
  fs.writeFileSync(path.join(d3, "nexusmt-badjson.bat"), ["@echo off", "echo HELLO >> logs\\log.txt", "exit /b 0"].join("\r\n") + "\r\n", "ascii");
  const s3 = await createJudgeScript({
    name: "非法JSON判断脚本", rootPath: d3, mainExe: path.join(d3, "nexusmt-badjson.bat"),
    configPath: d3, logPath: path.join(d3, "logs\\log.txt"),
    judgeScriptEnabled: true, judgeScriptLanguage: "javascript", judgeScript: 'console.log("hello world");',
  });
  assert(s3.ok, "创建非法 JSON 输出判断脚本");
  r = await runScript(s3.id);
  assert(r.rec && r.rec.finalStatus === "failed", "无合法结果视为继续运行 → 进程退出判定失败");
  await api("DELETE", "/api/scripts/" + s3.id);
}

async function testApiEmptyJudgeScript() {
  console.log("[用例] 修复验证：API 拒绝保存 judgeScriptEnabled=true 且代码为空（与前端一致）");
  const dir = makeMultiTaskDir("apiempty", ["1|enabled|success"]);
  const created = await createJudgeScript({
    name: "API空判断脚本", rootPath: dir, mainExe: path.join(dir, "nexusmt-apiempty.bat"),
    configPath: dir, logPath: path.join(dir, "logs\\log.txt"),
    judgeScriptEnabled: true, judgeScriptLanguage: "javascript", judgeScript: "",
  });
  assert(!created.ok, "API 拒绝空判断脚本（400：" + (created.error || "").slice(0, 60) + "）");
}

async function testBugReplaceMultiRoundRestore() {
  console.log("[用例] 多轮替换还原：两轮替换不同配置后，config 完整还原 + 备份清理");
  const dir = path.join(runtimeDir, "mt-multi");
  fs.rmSync(dir, { recursive: true, force: true });
  fs.mkdirSync(path.join(dir, "logs"), { recursive: true });
  fs.writeFileSync(path.join(dir, "cfg1.txt"), "V1", "utf8");
  fs.writeFileSync(path.join(dir, "cfg2.txt"), "W1", "utf8");
  fs.writeFileSync(path.join(dir, "nexusmt-multi.bat"), ["@echo off", "echo TASK 1 DONE >> logs\\log.txt", "exit /b 0"].join("\r\n") + "\r\n", "ascii");
  const jsMulti = `
const input = JSON.parse(__NEXUS_INPUT__);
const roundFile = (input.files || []).find(f => f.Root === "script" && f.Path === "round");
const n = Number(roundFile ? nexus.readFile(roundFile.Abs) || "0" : "0") + 1;
nexus.writeFile("round", String(n));
nexus.writeFile("cfg1.txt", "V2-R" + n);
if (n >= 2) nexus.writeFile("cfg2.txt", "W2-R" + n);
console.log(JSON.stringify({ status: "failed", reason: "round-" + n, replaceConfigs: n >= 2 ? ["cfg1.txt", "cfg2.txt"] : ["cfg1.txt"] }));`;
  const created = await createJudgeScript({
    name: "多轮替换还原", rootPath: dir, mainExe: path.join(dir, "nexusmt-multi.bat"),
    configPath: dir, logPath: path.join(dir, "logs\\log.txt"),
    maxAttempts: 2, judgeScriptEnabled: true, judgeScriptLanguage: "javascript", judgeScript: jsMulti,
  });
  assert(created.ok, "创建多轮替换脚本");
  const r = await runScript(created.id);
  assert(r.dispatchOk && r.ended, "运行结束");
  assert(r.rec && r.rec.attempts === 2, "两轮替换各失败一次（attempts=2）");
  const cfg1 = fs.readFileSync(path.join(dir, "cfg1.txt"), "utf8").trim();
  const cfg2 = fs.readFileSync(path.join(dir, "cfg2.txt"), "utf8").trim();
  assert(cfg1 === "V1" && cfg2 === "W1", "两轮替换后 config 均还原（cfg1=" + cfg1 + " cfg2=" + cfg2 + "）");
  assert(!fs.existsSync(userScriptDir(created.id)), "script 目录已清空");
  assert(!fs.existsSync(userBackupDir(created.id)), "swap-backup 已清理");
  await api("DELETE", "/api/scripts/" + created.id);
}

async function testSingleFileConfig() {
  console.log("[用例] 修复验证：单文件 config + replaceConfigs（项等于文件名）替换生效并还原");
  const dir = path.join(runtimeDir, "mt-singlecfg");
  fs.rmSync(dir, { recursive: true, force: true });
  fs.mkdirSync(path.join(dir, "logs"), { recursive: true });
  const cfgFile = path.join(dir, "cfg.txt");
  fs.writeFileSync(cfgFile, "ORIG", "utf8");
  fs.writeFileSync(path.join(dir, "nexusmt-singlecfg.bat"), ["@echo off", "echo HELLO >> logs\\log.txt", "ping -n 3 127.0.0.1 >nul", "exit /b 0"].join("\r\n") + "\r\n", "ascii");
  const jsSingle = `
nexus.writeFile("cfg.txt", "NEW");
console.log(JSON.stringify({ status: "failed", reason: "single-cfg", replaceConfigs: ["cfg.txt"] }));`;
  const created = await createJudgeScript({
    name: "单文件config替换", rootPath: dir, mainExe: path.join(dir, "nexusmt-singlecfg.bat"),
    configPath: cfgFile, logPath: path.join(dir, "logs\\log.txt"),
    judgeScriptEnabled: true, judgeScriptLanguage: "javascript", judgeScript: jsSingle,
  });
  assert(created.ok, "创建单文件 config 脚本");
  const dispatch = await api("POST", "/api/dispatch/script", { scriptId: created.id, mode: "manual" });
  assert(dispatch.ok, "dispatch 成功");
  // v0.6.9+（P6 语义）：替换在尝试收尾（杀进程确认退出后）应用、供重试轮使用——「运行中」立即替换的
  // 旧行为已改（替换→Unregister 窗口短于轮询周期，真实计时档稳定错过）；改为服务日志佐证替换发生。
  const ended = await waitNoRunning(30000);
  const logText = (() => {
    try {
      return fs.readFileSync(path.join(runtimeDir, "logs", "nexus-pipeline-" + localDate() + ".log"), "utf8").replace(/^\uFEFF/, "");
    } catch {
      return "";
    }
  })();
  const hist = await (await fetch(baseUrl + "api/history?days=7")).json();
  const rec = hist.filter(h => h.scriptInstanceId === created.id).at(-1);
  assert(ended, "运行结束");
  assert(logText.includes("[配置替换]") && logText.includes("cfg.txt"), "单文件 config 替换已生效（服务日志含替换记录）");
  assert(rec && rec.finalStatus === "failed", "判定失败（FinalStatus=" + rec?.finalStatus + "）");
  assert(fs.readFileSync(cfgFile, "utf8").trim() === "ORIG", "运行结束后单文件 config 已还原（cfg.txt=ORIG）");
  await api("DELETE", "/api/scripts/" + created.id);
}

async function testCrashRecovery() {
  console.log("[用例] 崩溃恢复：运行中强制终止服务 → 重启 → swap-backup 自动还原");
  const dir = path.join(runtimeDir, "mt-crash");
  fs.rmSync(dir, { recursive: true, force: true });
  fs.mkdirSync(path.join(dir, "logs"), { recursive: true });
  fs.writeFileSync(path.join(dir, "cfg.txt"), "ORIG", "utf8");
  fs.writeFileSync(path.join(dir, "nexusmt-crash.bat"), [
    "@echo off", "echo HELLO >> logs\\log.txt", "ping -n 3 127.0.0.1 >nul", "exit /b 0",
  ].join("\r\n") + "\r\n", "ascii");
  const jsCrash = `
nexus.writeFile("cfg.txt", "REPLACED");
console.log(JSON.stringify({ status: "failed", reason: "crash-replace", replaceConfigs: ["cfg.txt"] }));`;
  const created = await createJudgeScript({
    name: "崩溃恢复", rootPath: dir, mainExe: path.join(dir, "nexusmt-crash.bat"),
    configPath: dir, logPath: path.join(dir, "logs\\log.txt"),
    maxAttempts: 3, judgeScriptEnabled: true, judgeScriptLanguage: "javascript", judgeScript: jsCrash,
  });
  assert(created.ok, "创建崩溃恢复脚本");
  const dispatch = await api("POST", "/api/dispatch/script", { scriptId: created.id, mode: "manual" });
  assert(dispatch.ok, "dispatch 成功");
  const replaced = await waitFor(() => {
    try {
      return fs.readFileSync(path.join(dir, "cfg.txt"), "utf8").trim() === "REPLACED";
    } catch {
      return false;
    }
  }, 20000, 300);
  assert(replaced, "配置替换已发生（cfg.txt=REPLACED）");
  assert(fs.existsSync(path.join(userBackupDir(created.id), "cfg.txt")), "swap-backup 已建立");
  await stopService();
  await sleep(RESTART_WAIT_MS);
  startService();
  await waitForService();
  await sleep(FAST ? 300 : 800);
  const cfgPath = path.join(dir, "cfg.txt");
  const cfgAfter = fs.existsSync(cfgPath) ? fs.readFileSync(cfgPath, "utf8").trim() : "(缺失)";
  assert(cfgAfter === "ORIG", "重启后 RecoverInterrupted 自动还原配置（cfg.txt=" + cfgAfter + "）");
  await api("DELETE", "/api/scripts/" + created.id);
}

async function testSwapCrashRecovery() {
  console.log("[用例] 崩溃恢复：配置交换运行中强制终止服务 → 重启 → 现场还原");
  const dir = path.join(runtimeDir, "mt-swapcrash");
  const logDir = path.join(runtimeDir, "mt-swapcrash-log");
  fs.rmSync(dir, { recursive: true, force: true });
  fs.rmSync(logDir, { recursive: true, force: true });
  fs.mkdirSync(dir, { recursive: true });
  fs.mkdirSync(logDir, { recursive: true });
  const cfgFile = path.join(dir, "cfg.txt");
  fs.writeFileSync(cfgFile, "ORIGINAL", "utf8");
  fs.writeFileSync(path.join(dir, "nexusmt-swapcrash.bat"), [
    "@echo off",
    "echo SWAP-RUN >> " + logDir.replace(/\\/g, "\\\\") + "\\log.txt",
    "ping -n 20 127.0.0.1 >nul",
    "exit /b 0",
  ].join("\r\n") + "\r\n", "ascii");
  const created = await createJudgeScript({
    name: "交换崩溃恢复", rootPath: dir, mainExe: path.join(dir, "nexusmt-swapcrash.bat"),
    configPath: dir, logPath: path.join(logDir, "log.txt"),
    maxAttempts: 1, logStallTimeoutMinutes: FAST ? 30 : 5, totalTimeoutMinutes: FAST ? 60 : 10,
    // 不提供判断脚本（通用无判定）：脚本存活期间宿主不结束运行（无 marker 宽限 1 秒提前 kill），
    // 测试在脚本运行中（ping -n 20 约 19 秒）强杀服务 → 两档都走真实崩溃现场路径（v0.6.2 修复加速档假通过）。
  });
  assert(created.ok, "创建交换崩溃脚本（默认用户快照=ORIGINAL）");
  fs.writeFileSync(cfgFile, "MODIFIED", "utf8");
  const dispatch = await api("POST", "/api/dispatch/script", { scriptId: created.id, mode: "manual" });
  assert(dispatch.ok, "dispatch 成功（现场 MODIFIED → cache，快照 ORIGINAL → configPath）");
  const swapped = await waitFor(() => {
    try {
      return fs.readFileSync(cfgFile, "utf8").trim() === "ORIGINAL";
    } catch {
      return false;
    }
  }, 20000, 200);
  assert(swapped, "交换已完成（运行中 configPath=用户快照 ORIGINAL）");
  await stopService();
  await sleep(RESTART_WAIT_MS);
  startService();
  await waitForService();
  const restored = await waitFor(() => {
    try {
      return fs.readFileSync(cfgFile, "utf8").trim() === "MODIFIED";
    } catch {
      return false;
    }
  }, 60000, 500);
  const userDir = path.join(runtimeDir, "data", created.id, "默认");
  // 完整还原判定（v0.6.2）：还原含「cfg 落位 + .session 清除 + original 清空」三步（DoRestore 内部分步执行，
  // 孤儿脚本 cwd 占用 config 目录时部分成功会提前满足 cfg 断言）；waitFor 必须等到完整还原，再断言各残留。
  const fullyRestored = await waitFor(() => {
    let cfgOk = false;
    try {
      cfgOk = fs.readFileSync(cfgFile, "utf8").trim() === "MODIFIED";
    } catch { /* config 目录清理瞬间文件不存在 */ }
    const sessionGone = !fs.existsSync(path.join(userDir, ".session"));
    const originalEmpty = !fs.existsSync(path.join(userDir, "original")) || fs.readdirSync(path.join(userDir, "original")).length === 0;
    return cfgOk && sessionGone && originalEmpty;
  }, 90000, 500);
  assert(fullyRestored, "重启后配置交换完整还原现场（cfg.txt=MODIFIED + .session 清除 + original 清空）");
  const originalDir = path.join(userDir, "original");
  assert(!fs.existsSync(originalDir) || fs.readdirSync(originalDir).length === 0, "恢复后 original 已清空");
  await api("DELETE", "/api/scripts/" + created.id);
}

async function testScenarioD() {
  console.log("[用例] 场景D：极端崩溃（无日志崩溃→游戏崩溃→脚本崩溃）→ 3次失败 + 通知「任务4运行失败」");
  const dir = makeMultiTaskDir("d", ["1|enabled|crash-silent", "2|enabled|game-crash", "3|disabled|success", "4|enabled|script-crash"]);
  const jsCrashJudge = `
const input = JSON.parse(__NEXUS_INPUT__);
const log = input.log || "";
const cfgFile = (input.files || []).find(f => f.Root === "config" && f.Path === "tasks.txt");
const cfgText = cfgFile ? (nexus.readFile(cfgFile.Abs) || "") : "";
const enabled = [];
for (const line of cfgText.split(/\\r?\\n/)) {
  const p = line.split("|");
  if (p.length >= 3 && p[1].trim().toLowerCase() === "enabled") {
    enabled.push({ id: p[0].trim(), mode: p[2].trim() });
  }
}
const doneIds = [...log.matchAll(/TASK\\s+(\\w+)\\s+DONE/g)].map(m => m[1]);
const undone = enabled.filter(t => !doneIds.includes(t.id));
if (enabled.length > 0 && undone.length === 0) {
  console.log(JSON.stringify({ status: "success", reason: "all-done", notifyText: "所有任务已全部完成" }));
} else if (undone.length > 0) {
  const failedId = undone[0].id;
  const lines = [];
  for (const t of enabled) {
    if (t.id === failedId) lines.push(t.id + "|disabled|" + t.mode);
    else lines.push(t.id + "|enabled|" + t.mode);
  }
  nexus.writeFile("tasks.txt", lines.join("\\r\\n"));
  console.log(JSON.stringify({ status: "failed", reason: "task-crash-" + failedId, notifyText: "任务" + failedId + "运行失败", replaceConfigs: ["tasks.txt"] }));
}`;
  const created = await createJudgeScript({
    name: "场景D崩溃重试", rootPath: dir, mainExe: path.join(dir, "nexusmt-d.bat"),
    configPath: dir, logPath: path.join(dir, "logs\\log.txt"),
    maxAttempts: 3, judgeScriptEnabled: true, judgeScriptLanguage: "javascript", judgeScript: jsCrashJudge,
    notifyEnabled: true,
  });
  assert(created.ok, "创建场景D脚本");
  const dispatch = await api("POST", "/api/dispatch/script", { scriptId: created.id, mode: "manual" });
  assert(dispatch.ok, "dispatch 成功");
  const ended = await waitNoRunning(180000);
  const hist = await (await fetch(baseUrl + "api/history?days=7")).json();
  const rec = hist.filter(h => h.scriptInstanceId === created.id).at(-1);
  assert(ended, "场景D运行结束");
  assert(rec && rec.attempts === 3, "达到最大尝试次数（attempts=3，实际 " + rec?.attempts + "）");
  assert(rec && rec.finalStatus === "failed", "最终失败（FinalStatus=" + rec?.finalStatus + "）");
  const reasons = (rec?.attemptDetails || []).map(a => a.reason);
  assert(reasons[0] && /task-crash-1/.test(reasons[0]), "尝试1：任务1 无日志崩溃被判定失败（原因：" + JSON.stringify(reasons) + "）");
  assert(reasons[1] && /task-crash-2/.test(reasons[1]), "尝试2：任务2 游戏崩溃被判定失败（原因：" + JSON.stringify(reasons) + "）");
  assert(reasons[2] && /task-crash-4/.test(reasons[2]), "尝试3：任务4 脚本崩溃被判定失败（原因：" + JSON.stringify(reasons) + "）");
  await waitFor(() => hookBodies.some(b => b.includes("任务4运行失败")), 8000);
  assert(hookBodies.some(b => b.includes("任务4运行失败")), "webhook 收到通知「任务4运行失败」（最后一次尝试 notifyText）");
  const cfgAfter = fs.readFileSync(path.join(dir, "tasks.txt"), "utf8");
  const restored = cfgAfter.includes("1|enabled|crash-silent") && cfgAfter.includes("2|enabled|game-crash") && cfgAfter.includes("4|enabled|script-crash");
  assert(restored, "运行结束后 tasks.txt 还原为启动前状态（实际：" + cfgAfter.split("\r\n").join("; ") + "）");
  assert(!fs.existsSync(userScriptDir(created.id)), "script 目录已清空");
  assert(!fs.existsSync(userBackupDir(created.id)), "swap-backup 已清理");
  const logPath = path.join(runtimeDir, "logs", "nexus-pipeline-" + localDate() + ".log");
  let managerLog = "";
  if (fs.existsSync(logPath)) managerLog = fs.readFileSync(logPath, "utf8");
  assert(managerLog.includes("已强制关闭游戏"), "尝试2 失败后游戏进程被强制结束（管理器日志含「已强制关闭游戏」）");
  await api("DELETE", "/api/scripts/" + created.id);
}

async function testNoUserRejected() {
  console.log("[用例] 修复验证：无启用用户脚本手动运行被拒绝（全局强制）");
  const dir = makeMultiTaskDir("nouser", ["1|enabled|success"]);
  const res = await api("POST", "/api/scripts", {
    name: "无用户脚本", rootPath: dir, mainExe: path.join(dir, "nexusmt-nouser.bat"),
    configPath: dir, logPath: path.join(dir, "logs\\log.txt"),
    maxAttempts: 1, logStallTimeoutMinutes: 5, totalTimeoutMinutes: FAST ? 60 : 10, gameExe: PING_GAME,
    judgeScriptEnabled: true, judgeScriptLanguage: "javascript", judgeScript: 'console.log(JSON.stringify({ status: "success", reason: "x" }));',
  });
  assert(res.ok, "创建无用户脚本成功");
  const id = (await res.json()).id;
  const dispatch = await api("POST", "/api/dispatch/script", { scriptId: id, mode: "manual" });
  assert(!dispatch.ok, "手动运行无用户脚本被拒绝（400：" + (await dispatch.text()).slice(0, 60) + "）");
  await api("DELETE", "/api/scripts/" + id);
}

async function testQueueSkipNoUser() {
  console.log("[用例] 修复验证：队列运行时无启用用户脚本被跳过（failed 历史 + 不计进度）");
  const dir = makeMultiTaskDir("qskip", ["1|enabled|success"]);
  const res = await api("POST", "/api/scripts", {
    name: "队列跳过脚本", rootPath: dir, mainExe: path.join(dir, "nexusmt-qskip.bat"),
    configPath: dir, logPath: path.join(dir, "logs\\log.txt"),
    maxAttempts: 1, logStallTimeoutMinutes: 5, totalTimeoutMinutes: FAST ? 60 : 10, gameExe: PING_GAME,
    judgeScriptEnabled: true, judgeScriptLanguage: "javascript", judgeScript: 'console.log(JSON.stringify({ status: "success", reason: "x" }));',
  });
  assert(res.ok, "创建无用户脚本成功");
  const id = (await res.json()).id;
  const qRes = await api("POST", "/api/queues", {
    name: "跳过无用户队列", tasks: [{ id: "", index: 0, scriptInstanceId: id }], notifyEnabled: false,
  });
  assert(qRes.ok, "创建队列成功");
  const queues = await (await fetch(baseUrl + "api/queues")).json();
  const queue = queues.find(x => x.name === "跳过无用户队列");
  const dispatch = await api("POST", "/api/dispatch/queue", { queueId: queue.id, mode: "manual" });
  assert(dispatch.ok, "队列执行已受理");
  const ended = await waitNoRunning(60000);
  assert(ended, "队列运行结束");
  const hist = await (await fetch(baseUrl + "api/history?days=7")).json();
  const rec = hist.filter(h => h.scriptInstanceId === id).at(-1);
  assert(rec && rec.status === "failed" && rec.resultDetail.includes("未配置启用用户"),
    "跳过记录 failed 历史（结果：" + rec?.resultDetail + "）");
  assert(!fs.existsSync(userScriptDir(id)), "被跳过脚本未产生运行数据（script 目录不存在）");
  await api("DELETE", "/api/queues/" + queue.id);
  await api("DELETE", "/api/scripts/" + id);
}

/* ---------------- 主流程 ---------------- */

async function main() {
  console.log("NexusPipeline 自定义完成标志专项稳定性测试");
  console.log("========== 准备阶段 ==========");
  setupRuntime();
  await startHookServer();
  startService();
  await waitForService();
  const put = await api("PUT", "/api/settings", {
    webhookType: "slack",
    webhookTemplate: "",
    secretKey: "webhookUrl",
    secretValue: `http://127.0.0.1:${HOOK_PORT}/hook`,
  });
  console.log("  webhook 配置：" + (put.ok ? "OK" : "FAIL"));
  await sleep(500);

  console.log("========== 场景与边界用例 ==========");
  await testScenarioA();
  await testScenarioB();
  await testScenarioC();
  await testScenarioD();
  await testMaaEndJudgeScript();
  await testAutoUpdateConfig();
  await testAutoUpdateConfigContentGuard();
  await testEdgeNoLogStuck();
  await testBugNewFileResidue();
  await testBugPathEscape();
  await testBugMarkerReTrigger();
  await testBugNotifyLostOnPostRun();
  await testJudgeFaultTolerance();
  await testApiEmptyJudgeScript();
  await testBugReplaceMultiRoundRestore();
  await testSingleFileConfig();
  await testCrashRecovery();
  await testSwapCrashRecovery();
  await testNoUserRejected();
  await testQueueSkipNoUser();

  console.log("========== 收尾 ==========");
  await stopService();
  await stopHookServer();

  console.log(`\n结果：${passed} 通过，${failed} 失败`);
  process.exit(failed > 0 ? 1 : 0);
}

main().catch(err => {
  console.error("[异常] " + (err && err.stack ? err.stack : err));
  stopService();
  stopHookServer();
  process.exit(2);
});
