/**
 * chaos-queue.mjs — 混沌调度队列专项压力测试（独立文件，不影响 judge-scenarios.mjs 与 tests/ 54 用例）
 *
 * 覆盖内容：
 *  - 混沌队列（notifyEnabled=true）：S1(1用户)/S2(2用户)/S3(3用户) 共 6 用户串行执行
 *  - 干扰 = (seed + count) % 5：0=fail / 1=stuck(静默) / 2=crash(测试端强杀脚本进程树) /
 *    3=game-crash(测试端强杀游戏进程) / 4=success
 *  - 固定种子轮（seed 1-6 写于 config tasks.txt 元数据行，精确断言 reason/状态/顺序/交换/通知/残留）
 *  - 随机种子轮（seed 随机，只断言不变量）
 *  - 脚本级自定义通知（另建小队列 notifyEnabled=false + 脚本 notifyEnabled=true）
 *
 * 运行：node uitest\chaos-queue.mjs   （先跑 build.cmd；管理员 shell）
 */
import { spawn } from "node:child_process";
import http from "node:http";
import fs from "node:fs";
import os from "node:os";
import path from "node:path";
import { fileURLToPath } from "node:url";

const __dirname = path.dirname(fileURLToPath(import.meta.url));
const projectRoot = path.resolve(__dirname, "..");
const releaseDir = path.join(projectRoot, "release");
const runtimeDir = path.join(__dirname, "runtime");
const runtimeExe = path.join(runtimeDir, "nexus-pipeline.exe");
const baseUrl = "http://127.0.0.1:58731/";
const JSON_HDR = { "Content-Type": "application/json" };
const HOOK_PORT = 58888;
const PING_SRC = "C:\\Windows\\System32\\PING.EXE";

// 测试时间加速（v0.6.2+，v0.6.4 统一 scale=10）：NEXUS_TIME_SCALE 时宿主等待按比例缩放，伪造脚本卡住时长同步缩放（仍远大于缩放后的 stall/周期）。
const TIME_SCALE = Number(process.env.NEXUS_TIME_SCALE || "1") || 1;
const FAST = TIME_SCALE > 1;
const STUCK_PINGS = FAST ? 8 : 75;   // 卡住轮：真实 75 秒，加速 8 次 ping ≈ 7s（v0.6.4 scale=10 下 stall 6s 先于脚本退出触发失败；60 档语义保持）
const CRASH_PINGS = FAST ? 1 : 25;   // crash 轮持续输出循环：日志写入间隔必须 < 加速后的 stall（1 秒），否则误判 stall

const USERS = [
  { name: "甲", seed: 1, inst: "chaos-s1", game: "chaosgame-s1.exe" },
  { name: "乙", seed: 2, inst: "chaos-s2", game: "chaosgame-s2.exe" },
  { name: "丙", seed: 3, inst: "chaos-s2", game: "chaosgame-s2.exe" },
  { name: "丁", seed: 4, inst: "chaos-s3", game: "chaosgame-s3.exe" },
  { name: "戊", seed: 5, inst: "chaos-s3", game: "chaosgame-s3.exe" },
  { name: "己", seed: 6, inst: "chaos-s3", game: "chaosgame-s3.exe" },
];
const FIXED_SEED_MAP = Object.fromEntries(USERS.map(u => [u.name, u.seed]));
const FIXED_GAME_MAP = Object.fromEntries(USERS.map(u => [u.name, u.game]));
const INST_LABEL = { "chaos-s1": "混沌S1", "chaos-s2": "混沌S2", "chaos-s3": "混沌S3" };
const INST_DIR = label => path.join(runtimeDir, label);
const INST_TASKS = label => path.join(INST_DIR(label), "tasks.txt");
const INST_BAT = label => path.join(INST_DIR(label), "nexuschaos.bat");
const INST_LOG = label => path.join(INST_DIR(label), "logs", "log.txt");

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
    await sleep(intervalMs);
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

async function waitNoRunning(timeoutMs = 600000, intervalMs = 500) {
  return waitFor(async () => (await runningCount()) === 0, timeoutMs, intervalMs);
}

/* ---------------- 准备阶段 ---------------- */

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
  for (const g of ["chaosgame-s1.exe", "chaosgame-s2.exe", "chaosgame-s3.exe",
    "chaosgame-n1.exe", "chaosgame-n2.exe", "chaosgame-n3.exe"]) {
    fs.copyFileSync(PING_SRC, path.join(runtimeDir, g));
  }
  cleanupTempCounters();
}

function cleanupTempCounters() {
  const tmp = os.tmpdir();
  try {
    for (const f of fs.readdirSync(tmp)) {
      if (/^chaos-\d+-cnt\.txt$/.test(f)) {
        try { fs.unlinkSync(path.join(tmp, f)); } catch { /* ignore */ }
      }
    }
  } catch { /* ignore */ }
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
 * nexuschaos.bat：多用户通用伪脚本（全 ASCII）。
 * - 启动时归档旧日志（move logs\log.txt → logs\log-old.txt，宿主 LogMonitor 按 FileId 检测文件替换自动重读）；
 * - 读 config/tasks.txt 元数据行 `USER|SEED|GAMEEXE` 获取 seed 与游戏名（随配置交换自动切换当前用户）；
 * - 尝试计数 = %TEMP%\chaos-{seed}-cnt.txt（每用户独立）；
 * - 干扰 = (seed + count) % 5：0=fail / 1=stuck(静默 ping) / 2=crash(输出 START 持续 RUNNING 等注入) /
 *   3=game-crash(输出 START 循环检测游戏进程消失) / 4=success(输出 DONE)。
 * - DBG 行输出 seed/n/m 供测试诊断。
 */
const NEXUS_CHAOS_BAT = [
  "@echo off",
  "setlocal enabledelayedexpansion",
  "cd /d \"%~dp0\"",
  "if not exist \"%~dp0logs\" mkdir \"%~dp0logs\"",
  "if exist \"%~dp0logs\\log.txt\" move /y \"%~dp0logs\\log.txt\" \"%~dp0logs\\log-old.txt\" >nul 2>&1",
  "set seed=0",
  "set gameexe=zzz.exe",
  "for /f \"tokens=1,2,3 delims=|\" %%a in (tasks.txt) do (",
  "  if \"%%a\"==\"USER\" (",
  "    set seed=%%b",
  "    set gameexe=%%c",
  "  )",
  ")",
  "set /a n=0",
  "if exist \"%TEMP%\\chaos-!seed!-cnt.txt\" set /p n=<\"%TEMP%\\chaos-!seed!-cnt.txt\"",
  "set /a n+=1",
  "> \"%TEMP%\\chaos-!seed!-cnt.txt\" echo !n!",
  "set /a m=(!seed!+!n!)%%5",
  "echo DBG seed=!seed! n=!n! m=!m! >> \"%~dp0logs\\log.txt\"",
  "if \"!m!\"==\"0\" goto fail",
  "if \"!m!\"==\"1\" goto stuck",
  "if \"!m!\"==\"2\" goto crash",
  "if \"!m!\"==\"3\" goto gamecrash",
  "echo TASK 1 DONE >> \"%~dp0logs\\log.txt\"",
  "exit /b 0",
  ":fail",
  "echo TASK 1 FAIL >> \"%~dp0logs\\log.txt\"",
  "exit /b 1",
  ":stuck",
  "ping -n " + STUCK_PINGS + " 127.0.0.1 >nul",
  "exit /b 0",
  ":crash",
  "echo TASK 1 START >> \"%~dp0logs\\log.txt\"",
  ":crashloop",
  "ping -n " + CRASH_PINGS + " 127.0.0.1 >nul",
  "echo TASK 1 RUNNING >> \"%~dp0logs\\log.txt\"",
  "goto crashloop",
  ":gamecrash",
  "echo TASK 1 START >> \"%~dp0logs\\log.txt\"",
  ":waitgame",
  "tasklist /FI \"IMAGENAME eq !gameexe!\" | findstr /I \"!gameexe!\" >nul",
  "if errorlevel 1 goto gamegone",
  "ping -n 1 127.0.0.1 >nul",
  "echo TASK 1 WAIT >> \"%~dp0logs\\log.txt\"",
  "goto waitgame",
  ":gamegone",
  "echo TASK 1 FAIL GAMECRASH >> \"%~dp0logs\\log.txt\"",
  "exit /b 1",
].join("\r\n") + "\r\n";

/**
 * 通用判断脚本（JS 内置引擎）：从 config/tasks.txt 元数据行读 seed 与游戏名（固定轮/随机轮通用）。
 * - 日志含 `TASK 1 DONE` → success + notifyText「所有任务已全部完成」；
 * - 日志含 `TASK 1 FAIL` → failed + replaceConfigs（重写 tasks.txt 加 REPLACED 标记，验证替换生效与还原）；
 *   GAMECRASH 行 reason=task-failed-gc-1，否则 task-failed-1；
 * - 其余情况不输出 → 继续运行（stuck/crash 轮交宿主 stall/进程退出路径判定失败）。
 */
function judgeJs() {
  return `
const input = JSON.parse(__NEXUS_INPUT__);
const cfgFile = (input.files || []).find(f => f.Root === "config" && f.Path === "tasks.txt");
const cfgText = cfgFile ? (nexus.readFile(cfgFile.Abs) || "") : "";
const m = /^USER\\|(\\d+)\\|([^|\\r\\n]+)/.exec(cfgText);
const seed = m ? m[1] : "0";
const gameexe = m ? m[2] : "";
const log = input.log || "";
if (/TASK\\s+1\\s+DONE/.test(log)) {
  console.log(JSON.stringify({ status: "success", reason: "all-tasks-done", notifyText: "所有任务已全部完成" }));
} else if (/TASK\\s+1\\s+FAIL/.test(log)) {
  nexus.writeFile("tasks.txt", "USER|" + seed + "|" + gameexe + "|REPLACED");
  const gc = /TASK\\s+1\\s+FAIL\\s+GAMECRASH/.test(log);
  console.log(JSON.stringify({ status: "failed", reason: gc ? "task-failed-gc-1" : "task-failed-1", notifyText: "任务1运行失败", replaceConfigs: ["tasks.txt"] }));
}`;
}

function makeScriptDir(label, seedLine) {
  const dir = INST_DIR(label);
  fs.rmSync(dir, { recursive: true, force: true });
  fs.mkdirSync(path.join(dir, "logs"), { recursive: true });
  fs.writeFileSync(INST_BAT(label), NEXUS_CHAOS_BAT, "ascii");
  fs.writeFileSync(INST_TASKS(label), seedLine + "\r\n", "ascii");
}

function writeInstanceSeed(label, seed, gameExe) {
  fs.writeFileSync(INST_TASKS(label), `USER|${seed}|${gameExe}\r\n`, "ascii");
}

function readInstanceSeedLine(label) {
  try {
    const t = fs.readFileSync(INST_TASKS(label), "utf8");
    return t.split(/\r?\n/)[0] || "";
  } catch {
    return "";
  }
}

/* ---------------- API helpers ---------------- */

async function createScript(extra) {
  const res = await api("POST", "/api/scripts", {
    maxAttempts: 2, logStallTimeoutMinutes: 1, totalTimeoutMinutes: FAST ? 120 : 30,
    launchGame: true, gameWaitSeconds: 5, forceCloseGame: true,
    judgeScriptEnabled: true, judgeScriptLanguage: "javascript",
    notifyEnabled: false,
    ...extra,
  });
  if (!res.ok) {
    return { ok: false, id: "", error: await res.text() };
  }
  const script = await res.json();
  return { ok: true, id: script.id };
}

async function addUser(id, name) {
  const res = await api("POST", `/api/scripts/${id}/users`, { name, enabled: true });
  return res.ok;
}

async function createQueue(name, scriptIds, notifyEnabled) {
  const res = await api("POST", "/api/queues", {
    name, autoRunMode: "none", completionAction: "none", notifyEnabled,
    tasks: scriptIds.map((sid, i) => ({ index: i, scriptInstanceId: sid })),
    timeSets: [],
  });
  return res.ok;
}

async function findQueueByName(name) {
  const queues = await (await fetch(baseUrl + "api/queues")).json();
  return queues.find(x => x.name === name) || null;
}

async function dispatchQueue(queueId) {
  const res = await api("POST", "/api/dispatch/queue", { queueId, mode: "manual" });
  return res.ok;
}

async function deleteScripts(ids) {
  for (const id of ids) {
    try { await api("DELETE", "/api/scripts/" + id); } catch { /* ignore */ }
  }
}

async function deleteQueue(id) {
  try { await api("DELETE", "/api/queues/" + id); } catch { /* ignore */ }
}

async function historyByQueue(queueId) {
  const hist = await (await fetch(baseUrl + "api/history?days=7")).json();
  return hist.filter(h => h.queueId === queueId);
}

/* ---------------- 进程工具（PowerShell / taskkill） ---------------- */

function psOut(script) {
  return new Promise(resolve => {
    const ps = spawn("powershell.exe", ["-NoProfile", "-NonInteractive", "-Command", script]);
    let out = "";
    ps.stdout.on("data", d => { out += d.toString(); });
    ps.on("close", () => resolve(out.trim()));
    ps.on("error", () => resolve(""));
  });
}

async function findScriptCmdPid() {
  const out = await psOut("$p = Get-CimInstance Win32_Process -Filter \"Name='cmd.exe'\" | Where-Object { $_.CommandLine -like '*nexuschaos.bat*' } | Select-Object -First 1; if ($p) { Write-Output $p.ProcessId }");
  return parseInt(out, 10) || 0;
}

async function waitForPid(fn, timeoutMs = 90000, intervalMs = 400) {
  const deadline = Date.now() + timeoutMs;
  while (Date.now() < deadline) {
    const pid = await fn();
    if (pid > 0) return pid;
    await sleep(intervalMs);
  }
  return 0;
}

async function procCount(namePattern) {
  const out = await psOut(`(Get-Process ${namePattern} -ErrorAction SilentlyContinue).Count`);
  const n = parseInt(out, 10);
  return Number.isNaN(n) ? 0 : n;
}

async function hasCmdLineBat() {
  const out = await psOut("$p = Get-CimInstance Win32_Process -Filter \"Name='cmd.exe'\" | Where-Object { $_.CommandLine -like '*nexuschaos.bat*' }; if ($p) { Write-Output 'X' }");
  return out.includes("X");
}

function taskkill(...args) {
  return new Promise(resolve => {
    const p = spawn("taskkill.exe", ["/F", ...args]);
    p.on("close", () => resolve());
    p.on("error", () => resolve());
  });
}

/* ---------------- 注入器与运行中采样 ---------------- */

function currentUser(exec) {
  if (!exec) return "";
  const m = /（(.+?)）$/.exec(exec.currentScriptName || "");
  return m ? m[1] : "";
}

function interference(seed, attempt) {
  return (seed + attempt) % 5;
}

const INJECT_WAIT_MS = FAST ? 800 : 4000;

/**
 * 运行中注入器：轮询 /api/status 定位当前用户与尝试，干扰为 2(crash) 时强杀脚本进程树，
 * 干扰为 3(game-crash) 时强杀游戏进程。seedMap = {用户名: seed}，gameMap = {用户名: 游戏exe名}。
 */
async function runInjector(seedMap, gameMap, deadlineMs, label) {
  const injected = new Set();
  let lastKey = "";
  while (Date.now() < deadlineMs) {
    if ((await runningCount()) === 0) break;
    let exec = null;
    try {
      const status = await (await fetch(baseUrl + "api/status")).json();
      exec = (status.running || []).find(e => e.kind === "queue");
    } catch { /* retry */ }
    const user = currentUser(exec);
    const attempt = (exec && exec.currentAttempt) || 1;
    const curKey = user + ":" + attempt;
    if (user && curKey !== lastKey) {
      lastKey = curKey;
      const inst = USERS.find(u => u.name === user)?.inst;
      if (inst) {
        try {
          const logDir = path.join(INST_DIR(inst), "logs");
          const files = fs.existsSync(logDir) ? fs.readdirSync(logDir) : [];
          const contents = files.map(f => {
            const fp = path.join(logDir, f);
            try {
              const t = fs.readFileSync(fp, "utf8");
              return f + "=[" + (t.split(/\r?\n/)[0] || "").trim() + "]";
            } catch {
              return f + "=[读失败]";
            }
          });
          console.log(`  [观测] ${label} ${curKey} 切换时 logs: ${JSON.stringify(contents)}`);
        } catch (e) {
          console.log(`  [观测] ${label} ${curKey} 切换时 logs 读取异常: ${e.message}`);
        }
      }
    }
    if (user && seedMap[user] !== undefined) {
      const key = user + ":" + attempt;
      const kind = interference(seedMap[user], attempt);
      if (!injected.has(key)) {
        if (kind === 2) {
          const pid = await waitForPid(async () => await findScriptCmdPid());
          if (pid) {
            await sleep(INJECT_WAIT_MS);
            await taskkill("/T", "/PID", String(pid));
            injected.add(key);
            console.log(`  [注入] ${label} ${user} 尝试${attempt}：已强杀脚本进程树 PID=${pid}`);
          }
        } else if (kind === 3) {
          const exe = gameMap[user];
          const ok = await waitFor(async () => (await procCount(exe.replace(".exe", ""))) > 0, 60000, 400);
          if (ok) {
            await sleep(INJECT_WAIT_MS);
            await taskkill("/IM", exe);
            injected.add(key);
            console.log(`  [注入] ${label} ${user} 尝试${attempt}：已强杀游戏进程 ${exe}`);
          }
        }
      }
    }
    await sleep(300);
  }
  return injected;
}

/** 运行中 config 采样：收集各用户运行期间 config/tasks.txt 首行内容集合。 */
async function collectConfigDuringRun(deadlineMs) {
  const seen = {};
  const logSeen = {};
  while (Date.now() < deadlineMs) {
    if ((await runningCount()) === 0) break;
    let user = "";
    try {
      const status = await (await fetch(baseUrl + "api/status")).json();
      user = currentUser((status.running || []).find(e => e.kind === "queue"));
    } catch { /* retry */ }
    if (user) {
      const inst = USERS.find(u => u.name === user)?.inst;
      if (inst) {
        const line = readInstanceSeedLine(inst);
        if (line) {
          if (!seen[user]) seen[user] = new Set();
          seen[user].add(line);
        }
        try {
          const t = fs.readFileSync(path.join(INST_DIR(inst), "logs", "log.txt"), "utf8");
          const first = t.split(/\r?\n/)[0] || "";
          if (first) {
            if (!logSeen[user]) logSeen[user] = new Set();
            logSeen[user].add(first);
          }
        } catch { /* 日志文件不存在（未写入或已归档） */ }
      }
    }
    await sleep(250);
  }
  return { seen, logSeen };
}

/* ---------------- 数据残留检查 ---------------- */

function userDataDir(scriptId, userName) {
  return path.join(runtimeDir, "data", scriptId, userName);
}

function assertNoResidue(scriptId, users) {
  for (const u of users) {
    const dir = userDataDir(scriptId, u);
    assert(!fs.existsSync(path.join(dir, "script")), `data/${scriptId}/${u} 无 script 残留`);
    assert(!fs.existsSync(path.join(dir, "swap-backup")), `data/${scriptId}/${u} 无 swap-backup 残留`);
    assert(!fs.existsSync(path.join(dir, ".session")), `data/${scriptId}/${u} 无 .session 残留`);
    const original = path.join(dir, "original");
    assert(!fs.existsSync(original) || fs.readdirSync(original).length === 0, `data/${scriptId}/${u} original 已清空`);
  }
}

async function assertNoProcessResidue(label) {
  let count = 0;
  for (const g of ["chaosgame-s1", "chaosgame-s2", "chaosgame-s3", "chaosgame-n1", "chaosgame-n2", "chaosgame-n3"]) {
    count += await procCount(g);
  }
  assert(count === 0, `${label}：无游戏进程残留（chaosgame-* 共 ${count} 个）`);
  const cmd = await hasCmdLineBat();
  assert(!cmd, `${label}：无脚本 cmd 进程残留（nexuschaos.bat）`);
}

async function managerLogText() {
  const logPath = path.join(runtimeDir, "logs", "nexus-pipeline-" + localDate() + ".log");
  try {
    return fs.readFileSync(logPath, "utf8");
  } catch {
    return "";
  }
}

/* ---------------- 固定种子轮 ---------------- */

async function fixedSeedRound() {
  console.log("========== 固定种子轮 ==========");
  const scriptIds = {};
  const queues = [];

  // 搭建三个实例目录（S2 用户乙/丙、S3 用户丁/戊/己的专属内容在添加用户时逐个写入）
  makeScriptDir("chaos-s1", "USER|1|chaosgame-s1.exe");
  makeScriptDir("chaos-s2", "USER|2|chaosgame-s2.exe");
  makeScriptDir("chaos-s3", "USER|4|chaosgame-s3.exe");

  for (const label of ["chaos-s1", "chaos-s2", "chaos-s3"]) {
    const created = await createScript({
      name: INST_LABEL[label], rootPath: INST_DIR(label), mainExe: INST_BAT(label),
      configPath: INST_DIR(label), logPath: path.join(INST_DIR(label), "logs\\log.txt"),
      gameExe: path.join(runtimeDir, label.replace("chaos-", "chaosgame-") + ".exe"),
      gameArgs: "-n 120 127.0.0.1",
      judgeScript: judgeJs(),
    });
    assert(created.ok, `创建脚本实例 ${INST_LABEL[label]}（${created.error || ""}）`);
    scriptIds[label] = created.id;
  }

  // 逐个添加用户：添加前写入该用户的专属 tasks.txt（快照 = 当时内容）
  const addSteps = [
    ["chaos-s1", "甲", "1"],
    ["chaos-s2", "乙", "2"],
    ["chaos-s2", "丙", "3"],
    ["chaos-s3", "丁", "4"],
    ["chaos-s3", "戊", "5"],
    ["chaos-s3", "己", "6"],
  ];
  for (const [label, userName, seed] of addSteps) {
    writeInstanceSeed(label, seed, label.replace("chaos-", "chaosgame-") + ".exe");
    const ok = await addUser(scriptIds[label], userName);
    assert(ok, `添加用户 ${userName}（seed=${seed}）`);
  }

  const qOk = await createQueue("混沌队列", [scriptIds["chaos-s1"], scriptIds["chaos-s2"], scriptIds["chaos-s3"]], true);
  assert(qOk, "创建混沌队列（notifyEnabled=true）");
  const queue = await findQueueByName("混沌队列");
  assert(queue !== null, "混沌队列已存在");
  queues.push(queue.id);

  const t0 = Date.now();
  const dispatchOk = await dispatchQueue(queue.id);
  assert(dispatchOk, "混沌队列已受理执行");

  // 注入器 + 运行中 config 采样（并行）
  const injector = runInjector(FIXED_SEED_MAP, FIXED_GAME_MAP, Date.now() + 20 * 60 * 1000, "固定轮");
  const probe = collectConfigDuringRun(Date.now() + 20 * 60 * 1000);

  // 进度轮询：doneTasks 至 6
  let maxDone = 0;
  let totalTasks = 0;
  const progressDeadline = Date.now() + 20 * 60 * 1000;
  while (Date.now() < progressDeadline && (await runningCount()) > 0) {
    try {
      const status = await (await fetch(baseUrl + "api/status")).json();
      const exec = (status.running || []).find(e => e.kind === "queue");
      if (exec) {
        totalTasks = exec.totalTasks;
        maxDone = Math.max(maxDone, exec.doneTasks);
      }
    } catch { /* retry */ }
    await sleep(100);
  }
  const ended = await waitNoRunning(20 * 60 * 1000);
  await injector;
  const { seen, logSeen } = await probe;
  const elapsed = Math.round((Date.now() - t0) / 1000);

  assert(ended, `固定轮运行结束（耗时 ${elapsed}s）`);
  assert(totalTasks === 6, `队列 TotalTasks=6（实际 ${totalTasks}）`);
  const hist = await historyByQueue(queue.id);
  assert(hist.length === 6, `历史共 6 条记录（实际 ${hist.length}）`);
  const noSkip = !hist.some(h => (h.resultDetail || "").includes("已跳过"));
  assert(maxDone === 6 || (maxDone === 5 && noSkip),
    `队列进度 DoneTasks 达到 6（实际 ${maxDone}；DoneTasks++ 至 Unregister 窗口小于轮询周期时以历史 6 条+无跳过佐证）`);

  // 历史断言
  const ordered = [...hist].sort((a, b) => new Date(a.startTime) - new Date(b.startTime));
  const seq = ordered.map(r => r.userName);
  assert(JSON.stringify(seq) === JSON.stringify(["甲", "乙", "丙", "丁", "戊", "己"]),
    `执行顺序 甲→乙→丙→丁→戊→己（实际 ${JSON.stringify(seq)}）`);

  const STUCK_RE = /未产生日志条目|无更新/;
  const EXPECT = {
    甲: { final: "failed", attempts: 2, reasons: ["进程退出但未检测到完成标志", "判断脚本判定失败：task-failed-gc-1"] },
    乙: { final: "partial", attempts: 2, reasons: ["判断脚本判定失败：task-failed-gc-1", "判断脚本判定成功：all-tasks-done"] },
    丙: { final: "success", attempts: 1, reasons: ["判断脚本判定成功：all-tasks-done"] },
    丁: { final: "failed", attempts: 2, reasons: ["判断脚本判定失败：task-failed-1", STUCK_RE] },
    戊: { final: "failed", attempts: 2, reasons: [STUCK_RE, "进程退出但未检测到完成标志"] },
    己: { final: "failed", attempts: 2, reasons: ["进程退出但未检测到完成标志", "判断脚本判定失败：task-failed-gc-1"] },
  };
  const matchReason = (exp, actual) => {
    if (exp instanceof RegExp) return exp.test(actual);
    return exp === actual;
  };
  for (const r of ordered) {
    const exp = EXPECT[r.userName];
    const reasons = (r.attemptDetails || []).map(a => a.reason);
    assert(exp !== undefined, `用户 ${r.userName} 有历史记录`);
    assert(r.finalStatus === exp.final, `${r.userName} FinalStatus=${exp.final}（实际 ${r.finalStatus}）`);
    assert(r.attempts === exp.attempts, `${r.userName} attempts=${exp.attempts}（实际 ${r.attempts}）`);
    const matched = reasons.length === exp.reasons.length && exp.reasons.every((e, i) => matchReason(e, reasons[i]));
    assert(matched, `${r.userName} reason 序列匹配（实际 ${JSON.stringify(reasons)}）`);
  }

  // 日志文件存在性采样（区分「bat 未写入」与「宿主监控读不到」）
  for (const u of USERS) {
    const l = [...(logSeen[u.name] || [])];
    assert(l.some(x => x.startsWith("DBG seed=") || x.startsWith("TASK 1")),
      `${u.name} 运行期间 logs/log.txt 被脚本写入（测试端文件系统采样：${JSON.stringify(l)}）`);
  }

  // 运行中 config 交换采样断言
  for (const u of USERS) {
    const expLine = `USER|${u.seed}|${u.game}`;
    const snapshots = [...(seen[u.name] || [])];
    if (u.name === "丁") {
      assert(snapshots.some(l => l.includes("REPLACED")),
        `丁 fail 轮运行中 config 被替换（含 REPLACED，观测：${JSON.stringify(snapshots)}）`);
    } else if (snapshots.some(l => l === expLine)) {
      // 采样直接命中
    } else {
      // v0.6.9+ F5 治理：乙/丙快速成功轮（判定→宿主收尾仅数百毫秒）窗口短于 100ms 采样间隔时 seen 可能缺失，
      // 以历史记录（上方 EXPECT 断言已严格校验 finalStatus/attempts/reason）+ 日志文件采样佐证通过，
      // 复用 maxDone 的 noSkip 佐证先例；其余用户（多轮慢窗口）保持严格断言。
      const rec = hist.find(h => h.userName === u.name);
      const logHits = (logSeen[u.name] || []).length;
      const fastOk = (u.name === "乙" && rec && rec.finalStatus === "partial" && logHits > 0) ||
        (u.name === "丙" && rec && rec.finalStatus === "success" && rec.attempts === 1 && logHits > 0);
      assert(fastOk,
        `${u.name} 运行中 config=用户快照 ${expLine}（观测：${JSON.stringify(snapshots)}；历史佐证：${rec ? rec.finalStatus + "/" + rec.attempts : "无"}；日志采样 ${logHits} 条）`);
    }
  }

  // 结束后 config 还原断言（最后运行用户现场）
  const endChecks = [
    ["chaos-s1", "USER|1|chaosgame-s1.exe"],
    ["chaos-s2", "USER|3|chaosgame-s2.exe"],
    ["chaos-s3", "USER|6|chaosgame-s3.exe"],
  ];
  for (const [label, expLine] of endChecks) {
    const line = readInstanceSeedLine(label);
    assert(line === expLine, `${label} 运行结束还原（实际首行：${line}）`);
  }

  // data 残留
  assertNoResidue(scriptIds["chaos-s1"], ["甲"]);
  assertNoResidue(scriptIds["chaos-s2"], ["乙", "丙"]);
  assertNoResidue(scriptIds["chaos-s3"], ["丁", "戊", "己"]);

  // 管理器日志：强制结束游戏断言
  const mlog = await managerLogText();
  assert(/已强制关闭游戏（chaosgame-s1|chaosgame-s2|chaosgame-s3）/.test(mlog),
    "crash/stuck/success 轮游戏进程被宿主强制关闭（日志含「已强制关闭游戏」）");
  assert(/未发现需要关闭的游戏进程（chaosgame-s\d+）/.test(mlog),
    "game-crash 轮宿主执行强制结束流程（日志含「未发现需要关闭的游戏进程」）");
  assert(!mlog.includes("运行异常"), "管理器日志无「运行异常」");

  // 队列级汇总通知（slack webhook 正文为 {"text": ...} JSON 包裹，需解析后取 text）
  const summaryOk = await waitFor(() => hookBodies.some(b => {
    try {
      const t = JSON.parse(b).text || "";
      return t.includes("调度队列「混沌队列」运行汇总") && t.includes("任务总数：6");
    } catch { return false; }
  }), 10000);
  assert(summaryOk, "webhook 收到混沌队列汇总通知（任务总数：6）");
  let summaryText = "";
  for (const b of hookBodies) {
    try {
      const t = JSON.parse(b).text || "";
      if (t.includes("调度队列「混沌队列」运行汇总")) { summaryText = t; break; }
    } catch { /* not json */ }
  }
  const lines = summaryText.split(/\r?\n/).filter(l => l.startsWith("· "));
  assert(lines.length === 6, `汇总通知含 6 行状态（实际 ${lines.length}）`);
  const succLines = lines.filter(l => l.includes("成功（"));
  assert(succLines.length === 2, `汇总通知成功 2 行（乙 partial、丙 success，实际 ${succLines.length}：${JSON.stringify(succLines)}）`);

  // 进程残留
  await assertNoProcessResidue("固定轮");

  // 清理
  await deleteQueue(queue.id);
  await deleteScripts([scriptIds["chaos-s1"], scriptIds["chaos-s2"], scriptIds["chaos-s3"]]);
  return { hist };
}

/* ---------------- 随机种子轮 ---------------- */

async function randomSeedRound() {
  console.log("========== 随机种子轮 ==========");
  const scriptIds = {};
  const seedMap = {};
  const gameMap = {};

  makeScriptDir("chaos-s1", "USER|1|chaosgame-s1.exe");
  makeScriptDir("chaos-s2", "USER|2|chaosgame-s2.exe");
  makeScriptDir("chaos-s3", "USER|4|chaosgame-s3.exe");

  for (const label of ["chaos-s1", "chaos-s2", "chaos-s3"]) {
    const created = await createScript({
      name: INST_LABEL[label], rootPath: INST_DIR(label), mainExe: INST_BAT(label),
      configPath: INST_DIR(label), logPath: path.join(INST_DIR(label), "logs\\log.txt"),
      gameExe: path.join(runtimeDir, label.replace("chaos-", "chaosgame-") + ".exe"),
      gameArgs: "-n 120 127.0.0.1",
      judgeScript: judgeJs(),
    });
    scriptIds[label] = created.id;
  }

  const addSteps = [
    ["chaos-s1", "甲"], ["chaos-s2", "乙"], ["chaos-s2", "丙"],
    ["chaos-s3", "丁"], ["chaos-s3", "戊"], ["chaos-s3", "己"],
  ];
  for (const [label, userName] of addSteps) {
    writeInstanceSeed(label, 1, label.replace("chaos-", "chaosgame-") + ".exe");
    await addUser(scriptIds[label], userName);
  }

  // 随机 seed 写入各用户 store 快照，并记录实例 config 运行前内容
  const rand = [];
  for (const u of USERS) {
    const seed = 100 + Math.floor(Math.random() * 900);
    rand.push({ user: u.name, seed });
    seedMap[u.name] = seed;
    gameMap[u.name] = u.game;
    const store = path.join(runtimeDir, "data", scriptIds[u.inst], u.name, "store", "tasks.txt");
    fs.writeFileSync(store, `USER|${seed}|${u.game}\r\n`, "ascii");
    console.log(`  [随机] ${u.name} seed=${seed}`);
  }
  const beforeContent = {};
  for (const label of ["chaos-s1", "chaos-s2", "chaos-s3"]) {
    beforeContent[label] = readInstanceSeedLine(label);
  }

  const qOk = await createQueue("混沌随机队列", [scriptIds["chaos-s1"], scriptIds["chaos-s2"], scriptIds["chaos-s3"]], true);
  assert(qOk, "创建混沌随机队列");
  const queue = await findQueueByName("混沌随机队列");
  const dispatchOk = await dispatchQueue(queue.id);
  assert(dispatchOk, "混沌随机队列已受理执行");

  const t0 = Date.now();
  const injector = runInjector(seedMap, gameMap, Date.now() + 25 * 60 * 1000, "随机轮");
  const ended = await waitNoRunning(25 * 60 * 1000);
  await injector;
  const elapsed = Math.round((Date.now() - t0) / 1000);
  assert(ended, `随机轮运行结束（耗时 ${elapsed}s）`);

  const hist = await historyByQueue(queue.id);
  assert(hist.length === 6, `随机轮历史共 6 条（实际 ${hist.length}）`);
  for (const r of hist) {
    assert(r.attempts <= 2, `${r.userName} attempts ≤ 2（实际 ${r.attempts}）`);
    assert(["success", "failed", "partial"].includes(r.finalStatus),
      `${r.userName} FinalStatus ∈ {success,failed,partial}（实际 ${r.finalStatus}）`);
    assert(!((r.attemptDetails || []).some(a => a.status === "running")), `${r.userName} 无未完成尝试`);
  }

  // 运行结束 config 还原为随机轮运行前内容
  for (const label of ["chaos-s1", "chaos-s2", "chaos-s3"]) {
    const line = readInstanceSeedLine(label);
    assert(line === beforeContent[label], `${label} 运行结束还原为运行前现场（实际：${line}，期望：${beforeContent[label]}）`);
  }

  assertNoResidue(scriptIds["chaos-s1"], ["甲"]);
  assertNoResidue(scriptIds["chaos-s2"], ["乙", "丙"]);
  assertNoResidue(scriptIds["chaos-s3"], ["丁", "戊", "己"]);

  const mlog = await managerLogText();
  assert(!mlog.includes("运行异常"), "随机轮管理器日志无「运行异常」");

  const summaryOk = await waitFor(() => hookBodies.some(b => b.includes("调度队列「混沌随机队列」运行汇总")), 10000);
  assert(summaryOk, "随机轮 webhook 收到队列汇总通知");

  await assertNoProcessResidue("随机轮");

  await deleteQueue(queue.id);
  await deleteScripts([scriptIds["chaos-s1"], scriptIds["chaos-s2"], scriptIds["chaos-s3"]]);
}

/* ---------------- 小队列：脚本级自定义通知 ---------------- */

async function scriptNotifyRound() {
  console.log("========== 小队列：脚本级自定义通知 ==========");
  const scriptIds = {};

  for (const n of [1, 2, 3]) {
    const label = `chaos-n${n}`;
    const dir = INST_DIR(label);
    fs.rmSync(dir, { recursive: true, force: true });
    fs.mkdirSync(path.join(dir, "logs"), { recursive: true });
    fs.writeFileSync(INST_BAT(label), NEXUS_CHAOS_BAT, "ascii");
    // seed = 8/13/18：seed%5=3 → (seed+1)%5=4 → 一次成功 DONE
    const seed = 8 + (n - 1) * 5;
    fs.writeFileSync(INST_TASKS(label), `USER|${seed}|chaosgame-n${n}.exe\r\n`, "ascii");
    const created = await createScript({
      name: `混沌通知${n}`, rootPath: dir, mainExe: INST_BAT(label),
      configPath: dir, logPath: path.join(dir, "logs\\log.txt"),
      gameExe: path.join(runtimeDir, `chaosgame-n${n}.exe`),
      gameArgs: "-n 120 127.0.0.1",
      notifyEnabled: true,
      judgeScript: judgeJs(),
    });
    assert(created.ok, `创建通知脚本 ${n}`);
    scriptIds[n] = created.id;
    const ok = await addUser(created.id, "默认");
    assert(ok, `通知脚本 ${n} 添加用户`);
  }

  const qOk = await createQueue("混沌小队列", [scriptIds[1], scriptIds[2], scriptIds[3]], false);
  assert(qOk, "创建小队列（notifyEnabled=false）");
  const queue = await findQueueByName("混沌小队列");
  const dispatchOk = await dispatchQueue(queue.id);
  assert(dispatchOk, "小队列已受理执行");

  const t0 = Date.now();
  const ended = await waitNoRunning(5 * 60 * 1000);
  assert(ended, `小队列运行结束（耗时 ${Math.round((Date.now() - t0) / 1000)}s）`);

  const hist = await historyByQueue(queue.id);
  assert(hist.length === 3, `小队列历史共 3 条（实际 ${hist.length}）`);
  for (const r of hist) {
    assert(r.finalStatus === "success", `${r.scriptName} 一次成功（FinalStatus=${r.finalStatus}）`);
  }

  const scriptNotify = await waitFor(() => {
    const hits = hookBodies.filter(b => b.includes("所有任务已全部完成"));
    return hits.length >= 3;
  }, 15000);
  assert(scriptNotify, "webhook 收到 3 条脚本级通知「所有任务已全部完成」（CustomNotifyText 逐脚本替换）");
  const totalNotify = hookBodies.filter(b => b.includes("所有任务已全部完成")).length;
  assert(totalNotify === 3, `脚本级通知恰好 3 条（实际 ${totalNotify}）`);

  const mlog = await managerLogText();
  assert(!mlog.includes("运行异常"), "小队列管理器日志无「运行异常」");

  assertNoResidue(scriptIds[1], ["默认"]);
  assertNoResidue(scriptIds[2], ["默认"]);
  assertNoResidue(scriptIds[3], ["默认"]);
  await assertNoProcessResidue("小队列");

  await deleteQueue(queue.id);
  await deleteScripts([scriptIds[1], scriptIds[2], scriptIds[3]]);
}

/* ---------------- 主流程 ---------------- */

async function main() {
  console.log("NexusPipeline 混沌调度队列核心功能压力测试");
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

  await fixedSeedRound();
  if (process.argv.includes("fixed")) {
    console.log("========== 仅固定轮模式，跳过随机轮与小队列 ==========");
  } else {
    await randomSeedRound();
    await scriptNotifyRound();
  }

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
