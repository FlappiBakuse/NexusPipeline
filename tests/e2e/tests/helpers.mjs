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

const pidFile = path.join(runtimeDir, "service.pid");
let child = null;

export const sleep = ms => new Promise(resolve => setTimeout(resolve, ms));

export function localDate() {
  const now = new Date();
  return `${now.getFullYear()}-${String(now.getMonth() + 1).padStart(2, "0")}-${String(now.getDate()).padStart(2, "0")}`;
}

function psQuote(value) {
  return String(value).replaceAll("'", "''");
}

function killPid(pid) {
  if (!Number.isInteger(pid) || pid <= 0) return;
  if (process.env.NEXUS_ELEVATED_SERVICE === "1") {
    const command = `$p=Start-Process -FilePath 'taskkill.exe' -ArgumentList @('/PID','${pid}','/T','/F') -Verb RunAs -WindowStyle Hidden -Wait -PassThru; [Console]::WriteLine($p.ExitCode)`;
    spawnSync("pwsh", ["-NoProfile", "-NonInteractive", "-Command", command], { encoding: "utf8", windowsHide: true });
    return;
  }
  spawnSync("taskkill", ["/PID", String(pid), "/T", "/F"], { stdio: "ignore", windowsHide: true });
}

function cleanupRuntimeProcesses() {
  const runtimePrefix = psQuote(runtimeDir + "\\");
  const command = `$p=Get-CimInstance Win32_Process -Filter \"Name='nexus-pipeline.exe'\" | Where-Object { $_.ExecutablePath -like '${runtimePrefix}*' }; $p | ForEach-Object { Stop-Process -Id $_.ProcessId -Force }`;
  spawnSync("pwsh", ["-NoProfile", "-NonInteractive", "-Command", command], { stdio: "ignore", windowsHide: true });
}

export function setupRuntime() {
  cleanupRuntimeProcesses();
  fs.rmSync(runtimeDir, { recursive: true, force: true });
  fs.mkdirSync(runtimeDir, { recursive: true });
  const sourceExe = path.join(releaseDir, "nexus-pipeline.exe");
  if (!fs.existsSync(sourceExe)) throw new Error("release/nexus-pipeline.exe 不存在，请先运行 build.cmd");
  fs.copyFileSync(sourceExe, runtimeExe);
  fs.cpSync(path.join(releaseDir, "wwwroot"), path.join(runtimeDir, "wwwroot"), { recursive: true });
  const plugins = path.join(releaseDir, "plugins");
  if (fs.existsSync(plugins)) fs.cpSync(plugins, path.join(runtimeDir, "plugins"), { recursive: true });

  const fixtures = path.join(__dirname, "fixtures");
  const adbDir = path.join(runtimeDir, "adb-stub");
  fs.mkdirSync(adbDir, { recursive: true });
  fs.copyFileSync(path.join(fixtures, "adb-stub.cmd"), path.join(adbDir, "adb-stub.cmd"));
  fs.writeFileSync(path.join(adbDir, "foreground.txt"), "  mCurrentFocus=Window{test u0 app.lawnchair/app.lawnchair.LawnchairLauncher}", "utf8");
  const mumuDir = path.join(runtimeDir, "mumu-stub");
  fs.mkdirSync(mumuDir, { recursive: true });
  fs.copyFileSync(path.join(fixtures, "mumu-manager-stub.cmd"), path.join(mumuDir, "mumu-manager-stub.cmd"));
  fs.writeFileSync(path.join(mumuDir, "foreground.txt"), "  mCurrentFocus=Window{test u0 app.lawnchair/app.lawnchair.LawnchairLauncher}", "utf8");
}

function startElevatedRuntime(env) {
  const inheritedKeys = ["NEXUS_SYSTEM_ACTION_DRYRUN", "NEXUS_TIME_SCALE", "NEXUS_ADB_EXE", "NEXUS_MUMU_MANAGER_EXE", "NEXUS_UPDATE_URL"];
  const previousValues = new Map(inheritedKeys.map(key => [key, process.env[key]]));
  const environmentAssignments = inheritedKeys
    .filter(key => env[key])
    .map(key => `$env:${key} = '${psQuote(env[key])}'`)
    .join("; ");
  try {
    for (const key of inheritedKeys) {
      if (env[key]) process.env[key] = env[key];
    }
    const wrapperCommand = `${environmentAssignments}; & '${psQuote(runtimeExe)}'`;
    const command = `$p=Start-Process -FilePath 'pwsh.exe' -ArgumentList @('-NoProfile','-NonInteractive','-Command','${psQuote(wrapperCommand)}') -WorkingDirectory '${psQuote(runtimeDir)}' -WindowStyle Hidden -Verb RunAs -PassThru; [Console]::WriteLine($p.Id)`;
    const result = spawnSync("pwsh", ["-NoProfile", "-NonInteractive", "-Command", command], { encoding: "utf8", windowsHide: true });
    const pid = Number(String(result.stdout || "").trim().split(/\r?\n/).pop());
    if (!Number.isInteger(pid) || pid <= 0) {
      throw new Error(`提权启动 UI Smoke runtime 失败：${result.stderr || result.error?.message || "未获得 PID"}`);
    }
    return { pid, kill: () => killPid(pid) };
  } finally {
    for (const [key, value] of previousValues) {
      if (value === undefined) delete process.env[key];
      else process.env[key] = value;
    }
  }
}

export function startService() {
  fs.rmSync(pidFile, { force: true });
  const env = {
    ...process.env,
    NEXUS_SYSTEM_ACTION_DRYRUN: process.env.NEXUS_SYSTEM_ACTION_DRYRUN || "1",
    NEXUS_ADB_EXE: process.env.NEXUS_ADB_EXE || path.join(runtimeDir, "adb-stub", "adb-stub.cmd"),
    NEXUS_MUMU_MANAGER_EXE: process.env.NEXUS_MUMU_MANAGER_EXE || path.join(runtimeDir, "mumu-stub", "mumu-manager-stub.cmd"),
  };
  child = process.env.NEXUS_ELEVATED_SERVICE === "1"
    ? startElevatedRuntime(env)
    : spawn(runtimeExe, ["web"], { cwd: runtimeDir, stdio: ["pipe", "ignore", "ignore"], env, windowsHide: true });
  fs.writeFileSync(pidFile, String(child.pid), "ascii");
}

export async function waitForService(timeoutMs = 30000) {
  const deadline = Date.now() + timeoutMs;
  while (Date.now() < deadline) {
    try {
      const response = await fetch(baseUrl + "api/status");
      if (response.ok) return;
    } catch { /* 启动窗口内端口尚未监听 */ }
    await sleep(250);
  }
  throw new Error(`服务未在 ${timeoutMs}ms 内启动`);
}

export async function stopService() {
  let pid = 0;
  try { pid = Number(fs.readFileSync(pidFile, "utf8").trim()); } catch { /* 由 child 或路径扫描兜底 */ }
  const activePid = Number(child?.pid) || pid;
  killPid(activePid);
  child = null;
  fs.rmSync(pidFile, { force: true });
  // 更新应用会重拉服务进程（新 PID），按 runtimeDir 前缀兜底清理全部残留。
  cleanupRuntimeProcesses();
  await sleep(500);
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
  const response = await api("POST", "/api/scripts", { maxAttempts: 1, logStallTimeoutMinutes: 5, totalTimeoutMinutes: 120, gameExe: PING_GAME, autoUpdateConfig: false, ...body });
  if (!response.ok) return { ok: false, id: "" };
  const script = await response.json();
  await api("POST", `/api/scripts/${script.id}/users`, { name: "默认", enabled: true });
  return { ok: true, id: script.id };
}

export function makeScriptDir(label) {
  const dir = path.join(runtimeDir, `test-${label}`);
  fs.rmSync(dir, { recursive: true, force: true });
  fs.mkdirSync(path.join(dir, "cfg"), { recursive: true });
  fs.mkdirSync(path.join(dir, "logs"), { recursive: true });
  const stem = `nexustest-${label}`;
  const main = path.join(dir, `${stem}.bat`);
  fs.writeFileSync(main, "@echo off\r\nexit /b 0\r\n", "ascii");
  return { root: dir, main, cfg: path.join(dir, "cfg"), log: path.join(dir, "logs") };
}

export async function waitNoRunning(timeoutMs = 60000, intervalMs = 250) {
  const deadline = Date.now() + timeoutMs;
  while (Date.now() < deadline) {
    const response = await api("GET", "/api/status");
    if (response.ok && (await response.json()).running.length === 0) return true;
    await sleep(intervalMs);
  }
  return false;
}
