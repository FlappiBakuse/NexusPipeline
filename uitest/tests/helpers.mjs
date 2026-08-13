import { spawn, spawnSync } from "node:child_process";
import fs from "node:fs";
import path from "node:path";
import { fileURLToPath } from "node:url";

const __dirname = path.dirname(fileURLToPath(import.meta.url));
export const projectRoot = path.resolve(__dirname, "..", "..");
export const releaseDir = path.join(projectRoot, "release");
export const runtimeDir = path.join(__dirname, "..", "runtime");
export const runtimeExe = path.join(runtimeDir, "nexus-pipeline.exe");
export const baseUrl = "http://127.0.0.1:58731/";
export const JSON_HDR = { "Content-Type": "application/json" };
export const PING_GAME = "C:\\Windows\\System32\\PING.EXE";
export const CI_MODE = process.env.NEXUS_CI === "1";

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
  throw new Error("服务未在 " + timeoutMs + "ms 内启动");
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
  const res = await api("POST", "/api/scripts", { maxAttempts: 1, logStallTimeoutMinutes: 5, totalTimeoutMinutes: 120, gameExe: PING_GAME, ...body });
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
  // 清理上次残留的测试服务（崩溃/中断遗留仍占用 58731）：仅杀 uitest/runtime 目录下的 nexus-pipeline.exe，
  // 避免 e2e 服务落到 58732 而请求打向残留实例（先跑 judge/chaos 再跑 e2e 的常见工作流踩坑，v0.6.2 修复）。
  try {
    spawnSync("powershell", ["-NoProfile", "-NonInteractive", "-Command",
      "$p = Get-CimInstance Win32_Process -Filter \"Name='nexus-pipeline.exe'\" | Where-Object { $_.ExecutablePath -like '*uitest\\runtime\\*' }; $p | ForEach-Object { Stop-Process -Id $_.ProcessId -Force }"],
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
  if (!fs.existsSync(runtimeExe)) {
    throw new Error("runtime exe 拷贝失败，拒绝运行（避免测试数据写入项目根）");
  }
}

export function startService() {
  child = spawn(runtimeExe, ["web"], { cwd: runtimeDir, stdio: "ignore" });
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
        spawnSync("taskkill", ["/PID", String(pid), "/F"], { stdio: "ignore" });
      }
    } catch { /* 进程已退出 */ }
    try {
      fs.rmSync(pidFile, { force: true });
    } catch { /* 忽略 */ }
    await sleep(500);
  }
}

export async function restartService() {
  await stopService();
  await sleep(400);
  startService();
  await waitForService();
  await sleep(500);
}
