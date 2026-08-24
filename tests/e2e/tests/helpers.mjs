import { spawn, spawnSync } from "node:child_process";
import fs from "node:fs";
import path from "node:path";
import { fileURLToPath } from "node:url";

const __dirname = path.dirname(fileURLToPath(import.meta.url));
export const projectRoot = path.resolve(__dirname, "..", "..", "..");
export const releaseDir = path.join(projectRoot, "release");
export const runtimeDir = path.join(__dirname, "..", "runtime");
export const runtimeExe = path.join(runtimeDir, "nexus-pipeline.exe");
export const baseUrl = "http://127.0.0.1:58731/";
export const JSON_HDR = { "Content-Type": "application/json" };
export const PING_GAME = "C:\\Windows\\System32\\PING.EXE";
export const CI_MODE = process.env.NEXUS_CI === "1";

// v0.7.0+：模拟器 e2e 用 stub adb。global-setup 设置的 env 不进入 spec worker 进程（独立进程），
// 且 spec 级 ensureService 重拉的服务需要它——这里在 worker 侧兜底注入（setupRuntime 会在该路径重建 stub）。
if (!process.env.NEXUS_ADB_EXE) {
  process.env.NEXUS_ADB_EXE = path.join(__dirname, "..", "runtime", "adb-stub", "adb-stub.cmd");
}
if (!process.env.NEXUS_MUMU_MANAGER_EXE) {
  process.env.NEXUS_MUMU_MANAGER_EXE = path.join(__dirname, "..", "runtime", "mumu-stub", "mumu-manager-stub.cmd");
}

let child = null;

const pidFile = path.join(runtimeDir, "service.pid");

export function localDate() {
  const d = new Date();
  const y = d.getFullYear();
  const m = String(d.getMonth() + 1).padStart(2, "0");
  const day = String(d.getDate()).padStart(2, "0");
  return `${y}-${m}-${day}`;
}

export function isElevated() {
  try {
    const r = spawnSync("net", ["session"], { stdio: "ignore", windowsHide: true });
    return r.status === 0;
  } catch {
    return false;
  }
}

export const sleep = ms => new Promise(r => setTimeout(r, ms));

export async function waitFor(predicate, timeoutMs = 5000, intervalMs = 200) {
  const deadline = Date.now() + timeoutMs;
  while (Date.now() < deadline) {
    if (await predicate()) return true;
    await new Promise(r => setTimeout(r, intervalMs));
  }
  return !!(await predicate());
}

export async function waitForService(timeoutMs = 30000) {
  const deadline = Date.now() + timeoutMs;
  while (Date.now() < deadline) {
    try {
      const res = await fetch(baseUrl + "api/status");
      if (res.ok) return;
    } catch { /* retry */ }
    await sleep(500);
  }
  throw new Error("服务未在 " + timeoutMs + "ms 内启动\n" + serviceDiagnostics());
}

/** 服务启动失败诊断（v0.6.9+，F1/F4 治理）：runtime 进程状态 + 58731 监听 + 服务日志尾部，直接给出「启动即死」现场。 */
function serviceDiagnostics() {
  const lines = ["—— 服务启动诊断 ——"];
  try {
    const r = spawnSync("powershell", ["-NoProfile", "-NonInteractive", "-Command",
      "$p = Get-CimInstance Win32_Process -Filter \"Name='nexus-pipeline.exe'\" | Where-Object { $_.ExecutablePath -like '*tests\\e2e\\runtime\\*' }; if (-not $p -and $env:NEXUS_ELEVATED_SERVICE -eq '1') { $p = Get-CimInstance Win32_Process -Filter \"Name='nexus-pipeline.exe'\" }; $p | ForEach-Object { \"$($_.ProcessId) \" + $_.CreationDate }"],
      { stdio: ["ignore", "pipe", "ignore"], encoding: "utf8" });
    lines.push("runtime nexus-pipeline 进程：" + ((r.stdout || "").trim() || "（无）"));
  } catch { lines.push("runtime nexus-pipeline 进程：查询失败"); }
  try {
    const r = spawnSync("powershell", ["-NoProfile", "-NonInteractive", "-Command",
      "$c = Get-NetTCPConnection -LocalPort 58731 -State Listen -ErrorAction SilentlyContinue; if ($c) { 'LISTENING' } else { 'not-listening' }"],
      { stdio: ["ignore", "pipe", "ignore"], encoding: "utf8" });
    lines.push("端口 58731 监听：" + ((r.stdout || "").trim() || "查询失败"));
  } catch { lines.push("端口 58731 监听：查询失败"); }
  const logFile = path.join(runtimeDir, "logs", "nexus-pipeline-" + localDate() + ".log");
  try {
    const text = fs.readFileSync(logFile, "utf8").replace(/^\uFEFF/, "");
    const rows = text.split(/\r?\n/).filter(Boolean);
    lines.push("服务日志尾部（" + rows.length + " 行）：");
    lines.push(rows.slice(-25).join("\n"));
  } catch {
    lines.push("服务日志：不存在或读取失败（" + logFile + "）");
  }
  return lines.join("\n");
}

export async function api(method, pathName, body) {
  const options = { method };
  if (body !== undefined) {
    options.headers = JSON_HDR;
    options.body = JSON.stringify(body);
  }
  return fetch(baseUrl + pathName.replace(/^\/+/, ""), options);
}

export async function createScript(body) {
  const res = await api("POST", "/api/scripts", { maxAttempts: 1, logStallTimeoutMinutes: 5, totalTimeoutMinutes: 120, gameExe: PING_GAME, autoUpdateConfig: false, ...body });
  if (!res.ok) {
    return { ok: false, id: "" };
  }
  const script = await res.json();
  await api("POST", `/api/scripts/${script.id}/users`, { name: "默认", enabled: true });
  return { ok: true, id: script.id };
}

/** 创建真实存在的脚本目录（根目录/占位脚本/配置目录/日志目录），路径校验用例使用。
 *  占位脚本用唯一命名（nexustest-*），避免 IsExeRunning 按进程名误报（如 run.bat → 进程名 run）。 */
export function makeScriptDir(label) {
  const dir = path.join(runtimeDir, "test-" + label);
  fs.rmSync(dir, { recursive: true, force: true });
  fs.mkdirSync(path.join(dir, "cfg"), { recursive: true });
  fs.mkdirSync(path.join(dir, "logs"), { recursive: true });
  fs.writeFileSync(path.join(dir, `nexustest-${label}.bat`), "@echo off\r\nexit /b 0\r\n", "ascii");
  return { root: dir, main: path.join(dir, `nexustest-${label}.bat`), cfg: path.join(dir, "cfg"), log: path.join(dir, "logs") };
}

function killRuntimePid(pid) {
  if (!Number.isInteger(pid) || pid <= 0) return;
  if (process.env.NEXUS_ELEVATED_SERVICE === "1") {
    const command = "$p=Start-Process -FilePath 'taskkill.exe' -ArgumentList @('/PID','" + pid + "','/T','/F') -Verb RunAs -WindowStyle Hidden -Wait -PassThru; [Console]::WriteLine($p.ExitCode)";
    spawnSync("powershell", ["-NoProfile", "-NonInteractive", "-Command", command], { encoding: "utf8", windowsHide: true });
    return;
  }
  spawnSync("taskkill", ["/PID", String(pid), "/T", "/F"], { stdio: "ignore" });
}

function startElevatedRuntime(args, inheritedEnv) {
  const stamp = Date.now() + "-" + Math.random().toString(36).slice(2);
  const scriptPath = path.join(runtimeDir, "elevated-start-" + stamp + ".ps1");
  const launchPidPath = path.join(runtimeDir, "elevated-start-" + stamp + ".pid");
  const quote = value => String(value).replace(/'/g, "''");
  const argumentList = args.length ? `-ArgumentList @(${args.map(arg => `'${quote(arg)}'`).join(",")})` : "";
  const envAssignments = Object.entries(inheritedEnv)
    .filter(([, value]) => value !== "" && value !== undefined)
    .map(([key, value]) => `$env:${key}='${quote(value)}'`)
    .join("; ");
  const script = [
    envAssignments,
    `$p=Start-Process -FilePath '${quote(runtimeExe)}' ${argumentList} -WorkingDirectory '${quote(runtimeDir)}' -WindowStyle Hidden -PassThru`,
    `Set-Content -LiteralPath '${quote(launchPidPath)}' -Value $p.Id -Encoding ascii`,
  ].filter(Boolean).join("; ");
  fs.writeFileSync(scriptPath, script, "utf8");
  const command = "$p=Start-Process -FilePath 'powershell.exe' -ArgumentList @('-NoProfile','-NonInteractive','-ExecutionPolicy','Bypass','-File','" + quote(scriptPath) + "') -Verb RunAs -WindowStyle Hidden -PassThru; [Console]::WriteLine($p.Id)";
  const result = spawnSync("powershell", ["-NoProfile", "-NonInteractive", "-Command", command], { encoding: "utf8", windowsHide: true });
  return { ...result, scriptPath, launchPidPath };
}

export async function runningCount() {
  const status = await (await fetch(baseUrl + "api/status")).json();
  return (status.running || []).length;
}

export async function waitNoRunning(timeoutMs = 60000, intervalMs = 300) {
  return waitFor(async () => (await runningCount()) === 0, timeoutMs, intervalMs);
}

export async function waitAbsent(page, text, timeoutMs = 5000) {
  return page.waitForFunction(t => !document.body.textContent.includes(t), text, { timeout: timeoutMs });
}

export function latestHistoryDay() {
  const historyRoot = path.join(runtimeDir, "history");
  const dirs = fs.existsSync(historyRoot) ? fs.readdirSync(historyRoot).filter(d => /^\d{4}-\d{2}-\d{2}$/.test(d)).sort() : [];
  return dirs.length ? dirs[dirs.length - 1] : localDate();
}

export function setupRuntime() {
  // 清理上次残留的测试服务（崩溃/中断遗留仍占用 58731）：仅杀 tests/e2e/runtime 目录下的 nexus-pipeline.exe，
  // 避免 e2e 服务落到 58732 而请求打向残留实例（先跑 judge/chaos 再跑 e2e 的常见工作流踩坑，v0.6.2 修复）。
  try {
    spawnSync("powershell", ["-NoProfile", "-NonInteractive", "-Command",
      "$p = Get-CimInstance Win32_Process -Filter \"Name='nexus-pipeline.exe'\" | Where-Object { $_.ExecutablePath -like '*tests\\e2e\\runtime\\*' }; if (-not $p -and $env:NEXUS_ELEVATED_SERVICE -eq '1') { $p = Get-CimInstance Win32_Process -Filter \"Name='nexus-pipeline.exe'\" }; $p | ForEach-Object { Stop-Process -Id $_.ProcessId -Force }"],
      { stdio: "ignore" });
  } catch { /* 清理失败不阻塞（后续 startService 端口 +1 重试兜底） */ }
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
  // v0.7.0+：模拟器 e2e 用 stub adb（fixtures 复制到隔离目录，经 NEXUS_ADB_EXE 注入服务进程）。
  const stubDir = path.join(runtimeDir, "adb-stub");
  fs.mkdirSync(stubDir, { recursive: true });
  fs.copyFileSync(path.join(__dirname, "fixtures", "adb-stub.cmd"), path.join(stubDir, "adb-stub.cmd"));
  fs.writeFileSync(path.join(stubDir, "foreground.txt"), "  mCurrentFocus=Window{test u0 app.lawnchair/app.lawnchair.LawnchairLauncher}", "utf8");
  try {
    fs.rmSync(path.join(stubDir, "calls.log"), { force: true });
  } catch { /* 忽略 */ }
  // v0.9.5+：MuMuManager stub 只把 16416 映射为 MuMu，16384 等端口仍验证通用 ADB 路由。
  const mumuDir = path.join(runtimeDir, "mumu-stub");
  fs.mkdirSync(mumuDir, { recursive: true });
  fs.copyFileSync(path.join(__dirname, "fixtures", "mumu-manager-stub.cmd"), path.join(mumuDir, "mumu-manager-stub.cmd"));
  fs.writeFileSync(path.join(mumuDir, "foreground.txt"), "  mCurrentFocus=Window{test u0 app.lawnchair/app.lawnchair.LawnchairLauncher}", "utf8");
  fs.rmSync(path.join(mumuDir, "mumu-calls.log"), { force: true });
  fs.rmSync(path.join(mumuDir, "stopped.flag"), { force: true });
  if (!fs.existsSync(runtimeExe)) {
    throw new Error("runtime exe 拷贝失败，拒绝运行（避免测试数据写入项目根）");
  }
}

/** 启动测试服务（v0.6.5+ 支持 service 模式：自重启仅常驻服务模式支持，测试用无参数启动；默认 web 模式）。
 *  extraEnv（v0.7.0+）：额外注入的环境变量（如 NEXUS_ADB_EXE 指向 stub adb）。 */
export function startService(mode = "web", extraEnv = {}) {
  const args = mode === "service" ? [] : [mode];
  // 自重启由产品进程完成，测试服务的 PID 文件不会随新进程更新。
  // 启动前必须清除旧值，否则提权启动轮询会把旧 PID 误判为本轮启动成功。
  try {
    fs.rmSync(pidFile, { force: true });
  } catch { /* PID 文件仅作启动同步信号，清理失败交由后续写入处理 */ }
  // v0.6.6+：stdin 用 pipe 保持打开（web 模式「按回车停止」阻塞等待；stdio:ignore 的 NUL/无效句柄会被视为 EOF 立即退出）。
  if (process.env.NEXUS_ELEVATED_SERVICE === "1") {
    const inheritedEnv = {
      NEXUS_SYSTEM_ACTION_DRYRUN: process.env.NEXUS_SYSTEM_ACTION_DRYRUN || "1",
      NEXUS_TIME_SCALE: process.env.NEXUS_TIME_SCALE || "",
      NEXUS_ADB_EXE: process.env.NEXUS_ADB_EXE || "",
      NEXUS_MUMU_MANAGER_EXE: process.env.NEXUS_MUMU_MANAGER_EXE || "",
      ...extraEnv,
    };
    // 提权启动没有可保持打开的 stdin。使用无参数常驻服务模式，避免 web 模式把隐藏进程的 EOF 当作停止信号。
    const result = startElevatedRuntime([], inheritedEnv);
    const deadline = Date.now() + 15000;
    let pid = 0;
    try {
      while (Date.now() < deadline) {
        pid = Number(fs.existsSync(result.launchPidPath) ? fs.readFileSync(result.launchPidPath, "utf8").trim() : "");
        if (Number.isInteger(pid) && pid > 0) break;
        spawnSync("powershell", ["-NoProfile", "-NonInteractive", "-Command", "Start-Sleep -Milliseconds 100"], { stdio: "ignore" });
      }
    } finally {
      fs.rmSync(result.scriptPath, { force: true });
      fs.rmSync(result.launchPidPath, { force: true });
    }
    const details = [result.error?.message, (result.stderr || "").trim(), (result.stdout || "").trim()].filter(Boolean).join(" | ");
    if (!Number.isInteger(pid) || pid <= 0) throw new Error("提权启动 E2E 服务失败：未获得有效服务 PID" + (details ? "（" + details + "）" : ""));
    child = { pid, kill: () => killRuntimePid(pid) };
  } else {
    child = spawn(runtimeExe, args, { cwd: runtimeDir, stdio: ["pipe", "ignore", "ignore"], env: { ...process.env, ...extraEnv } });
  }
  try {
    fs.writeFileSync(pidFile, String(child.pid));
  } catch { /* pid 文件仅作跨进程兜底，写失败不阻塞 */ }
}

export async function stopService() {
  if (child) {
    child.kill();
    await sleep(500);
    child = null;
  }
  // 跨进程兜底：globalSetup 启动的服务在 worker 进程中 child 为 null，按 PID 文件结束进程。
  if (fs.existsSync(pidFile)) {
    try {
      const pid = Number(fs.readFileSync(pidFile, "utf8"));
      if (pid > 0) {
        killRuntimePid(pid);
      }
    } catch { /* 进程已退出 */ }
    try {
      fs.rmSync(pidFile, { force: true });
    } catch { /* 忽略 */ }
    await sleep(500);
  }
  if (process.env.NEXUS_ELEVATED_SERVICE === "1") {
    await killRuntimeServices(5000);
  }
}

export async function restartService() {
  await stopService();
  await sleep(400);
  startService();
  await waitForService();
  await sleep(500);
}

/** spec 文件级服务兜底（v0.6.9+，A5 级联隔离）：各 spec 文件模块加载时调用。
 *  上一文件尾部服务死亡（F1/F4 级联）时，本文件自动强杀残留并重拉 web 模式服务，把整场级联失败隔离为单文件失败。
 *  v0.7.0+：重拉时继承 NEXUS_ADB_EXE（stub adb），避免回退到本机真实模拟器 adb。 */
export async function ensureService() {
  try {
    const res = await fetch(baseUrl + "api/status");
    if (res.ok) return;
  } catch { /* 不可达，进入重拉 */ }
  console.warn("[helpers] ensureService：服务不可达，强杀残留并重拉 web 模式服务（级联隔离兜底）");
  await killRuntimeServices();
  await startService("web", { NEXUS_ADB_EXE: process.env.NEXUS_ADB_EXE || "", NEXUS_MUMU_MANAGER_EXE: process.env.NEXUS_MUMU_MANAGER_EXE || "" });
  await waitForService();
}

/** 强杀 tests/e2e/runtime 目录下全部 nexus-pipeline 进程（v0.6.5+：自重启后新进程未登记 PID 文件，需按路径清理）。
 *  v0.6.9+：杀后轮询确认进程完全消失（Stop-Process 异步，固定 600ms 等待存在旧进程互斥体未释放的竞态窗口，
 *  曾致后续 startService("web") 因互斥体被占直接退出——F1/F4 级联 flake），确认消失后才返回。 */
export async function killRuntimeServices(timeoutMs = 15000) {
  const listeningPids = () => {
    try {
      const r = spawnSync("powershell", ["-NoProfile", "-NonInteractive", "-Command",
        "@(Get-NetTCPConnection -LocalPort 58731 -State Listen -ErrorAction SilentlyContinue | Select-Object -ExpandProperty OwningProcess) -join ','"],
        { stdio: ["ignore", "pipe", "ignore"], encoding: "utf8" });
      return (r.stdout || "").trim();
    } catch {
      return "";
    }
  };
  const processTreePids = () => {
    try {
      const root = Number(fs.readFileSync(pidFile, "utf8").trim());
      if (!Number.isInteger(root) || root <= 0) return "";
      const r = spawnSync("powershell", ["-NoProfile", "-NonInteractive", "-Command",
        "$all=@(Get-CimInstance Win32_Process -Filter \"Name='nexus-pipeline.exe'\"); $ids=@(" + root + "); do { $children=@($all | Where-Object { $ids -contains $_.ParentProcessId } | Select-Object -ExpandProperty ProcessId); $new=@($children | Where-Object { $ids -notcontains $_ }); $ids += $new } while ($new.Count -gt 0); $found=@($all | Where-Object { $ids -contains $_.ProcessId } | Select-Object -ExpandProperty ProcessId); if ($found.Count -eq 0) { $found=@($all | Select-Object -ExpandProperty ProcessId) }; $found -join ','"],
        { stdio: ["ignore", "pipe", "ignore"], encoding: "utf8" });
      return (r.stdout || "").trim();
    } catch {
      return "";
    }
  };
  const runtimePids = () => {
    if (process.env.NEXUS_ELEVATED_SERVICE === "1") {
      // HttpListener 的端口归属通常显示为 PID 4（HTTP.sys），不能据此结束系统进程；
      // 产品自重启产生的新服务进程则通过旧服务 PID 的父子关系追踪。
      const portPids = listeningPids().split(",").map(value => Number(value)).filter(value => Number.isInteger(value) && value > 4).join(",");
      return [...new Set([processTreePids(), portPids].join(",").split(",").map(value => value.trim()).filter(Boolean))].join(",");
    }
    try {
      const r = spawnSync("powershell", ["-NoProfile", "-NonInteractive", "-Command",
        "$p = Get-CimInstance Win32_Process -Filter \"Name='nexus-pipeline.exe'\" | Where-Object { $_.ExecutablePath -like '*tests\\e2e\\runtime\\*' }; ($p | ForEach-Object { $_.ProcessId }) -join ','"],
        { stdio: ["ignore", "pipe", "ignore"], encoding: "utf8" });
      return (r.stdout || "").trim();
    } catch {
      return "";
    }
  };
  try {
    if (process.env.NEXUS_ELEVATED_SERVICE === "1") {
      for (const pid of runtimePids().split(",").map(value => Number(value)).filter(value => Number.isInteger(value) && value > 0)) {
        killRuntimePid(pid);
      }
    } else {
    spawnSync("powershell", ["-NoProfile", "-NonInteractive", "-Command",
      "$p = Get-CimInstance Win32_Process -Filter \"Name='nexus-pipeline.exe'\" | Where-Object { $_.ExecutablePath -like '*tests\\e2e\\runtime\\*' }; $p | ForEach-Object { Stop-Process -Id $_.ProcessId -Force }"],
      { stdio: "ignore" });
    }
  } catch { /* 清理失败不阻塞 */ }
  const gone = await waitFor(() => runtimePids() === "", timeoutMs, 300);
  if (!gone) {
    console.warn("[helpers] killRuntimeServices：轮询 " + Math.round(timeoutMs / 1000) + "s 后仍有 runtime 进程残留（" + runtimePids() + "），继续执行");
  }
  child = null;
  try {
    fs.rmSync(pidFile, { force: true });
  } catch { /* 清理失败不阻塞后续测试；下一次 startService 会再次尝试 */ }
}
