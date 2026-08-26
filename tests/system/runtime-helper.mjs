import { spawn, spawnSync } from "node:child_process";
import fs from "node:fs";
import path from "node:path";
import { fileURLToPath } from "node:url";

const here = path.dirname(fileURLToPath(import.meta.url));
export const projectRoot = path.resolve(here, "..", "..");
export const releaseDir = path.join(projectRoot, "release");
const runtimeName = process.env.NEXUS_SYSTEM_RUNTIME_NAME || "runtime";
if (!/^[A-Za-z0-9_-]+$/.test(runtimeName)) {
  throw new Error(`非法 NEXUS_SYSTEM_RUNTIME_NAME：${runtimeName}`);
}
export const runtimeDir = path.join(here, runtimeName);
export const runtimeExe = path.join(runtimeDir, "nexus-pipeline.exe");
export const baseUrl = "http://127.0.0.1:58731/";
export const adbStub = path.join(runtimeDir, "adb-stub", "adb-stub.cmd");
export const mumuStub = path.join(runtimeDir, "mumu-stub", "mumu-manager-stub.cmd");

let child = null;
let stdout = "";
let stderr = "";

export const sleep = ms => new Promise(resolve => setTimeout(resolve, ms));

export function isElevated() {
  if (process.env.NEXUS_SYSTEM_SMOKE_ELEVATED === "1") return true;
  try {
    return spawnSync("net", ["session"], { stdio: "ignore", windowsHide: true }).status === 0;
  } catch {
    return false;
  }
}

export function prepareRuntime() {
  // 提权服务退出后，Windows 可能在极短窗口内仍持有 exe/日志句柄；这是运行时清理，
  // 使用 fs.rm 的有界句柄重试完成目录隔离，不影响测试断言或业务重试语义。
  fs.rmSync(runtimeDir, { recursive: true, force: true, maxRetries: 120, retryDelay: 250 });
  fs.mkdirSync(runtimeDir, { recursive: true });
  const sourceExe = path.join(releaseDir, "nexus-pipeline.exe");
  if (!fs.existsSync(sourceExe)) {
    throw new Error("release/nexus-pipeline.exe 不存在，请先运行 build.cmd");
  }
  fs.copyFileSync(sourceExe, runtimeExe);
  fs.cpSync(path.join(releaseDir, "wwwroot"), path.join(runtimeDir, "wwwroot"), { recursive: true });
  const pluginsDir = path.join(releaseDir, "plugins");
  if (fs.existsSync(pluginsDir)) fs.cpSync(pluginsDir, path.join(runtimeDir, "plugins"), { recursive: true });

  const fixtureDir = path.join(projectRoot, "tests", "e2e", "tests", "fixtures");
  fs.mkdirSync(path.join(runtimeDir, "adb-stub"), { recursive: true });
  fs.copyFileSync(path.join(fixtureDir, "adb-stub.cmd"), adbStub);
  fs.writeFileSync(path.join(runtimeDir, "adb-stub", "foreground.txt"), "  mCurrentFocus=Window{test u0 app.lawnchair/app.lawnchair.LawnchairLauncher}", "utf8");
  fs.mkdirSync(path.join(runtimeDir, "mumu-stub"), { recursive: true });
  fs.copyFileSync(path.join(fixtureDir, "mumu-manager-stub.cmd"), mumuStub);
  fs.writeFileSync(path.join(runtimeDir, "mumu-stub", "foreground.txt"), "  mCurrentFocus=Window{test u0 app.lawnchair/app.lawnchair.LawnchairLauncher}", "utf8");
}

function powershellLiteral(value) {
  return String(value).replaceAll("'", "''");
}

function runtimePids() {
  const filter = powershellLiteral(runtimeDir + "\\");
  const command = `$p = Get-CimInstance Win32_Process -Filter "Name='nexus-pipeline.exe'" | Where-Object { $_.ExecutablePath -like '${filter}*' }; ($p | ForEach-Object { $_.ProcessId }) -join ','`;
  const markerPath = path.join(runtimeDir, "service.pid");
  const markerPids = [];
  try {
    const markerPid = Number(fs.readFileSync(markerPath, "utf8").trim());
    if (Number.isInteger(markerPid) && markerPid > 0) {
      // 跨完整性级别下 process.kill(pid, 0) 可能返回 EPERM，即使进程真实存在；
      // service.pid 由目标 runtime 写入，交给精确 taskkill 处理，避免误判为已退出。
      markerPids.push(markerPid);
    }
  } catch {
  }
  try {
    const result = spawnSync("pwsh", ["-NoProfile", "-NonInteractive", "-Command", command], { encoding: "utf8", windowsHide: true });
    return [...new Set([...markerPids, ...(result.stdout || "").trim().split(/\s*,\s*/).filter(Boolean).map(Number)])];
  } catch {
    return markerPids;
  }
}

export function isRuntimeAlive(pid) {
  return runtimePids().includes(Number(pid));
}

function killRuntimeProcesses() {
  const markerPath = path.join(runtimeDir, "service.pid");
  let markerPid = 0;
  try {
    markerPid = Number(fs.readFileSync(markerPath, "utf8").trim());
  } catch {
  }
  for (const pid of runtimePids()) {
    if (killPid(pid) && pid === markerPid) {
      fs.rmSync(markerPath, { force: true });
    }
  }
}

function killPid(pid) {
  if (!Number.isInteger(pid) || pid <= 0) return false;
  if (process.env.NEXUS_SYSTEM_SMOKE_ELEVATED === "1") {
    const command = `$p=Start-Process -FilePath 'taskkill.exe' -ArgumentList @('/PID','${pid}','/T','/F') -Verb RunAs -WindowStyle Hidden -Wait -PassThru; [Console]::WriteLine($p.ExitCode)`;
    const result = spawnSync("pwsh", ["-NoProfile", "-NonInteractive", "-Command", command], { encoding: "utf8", windowsHide: true });
    return result.status === 0 || String(result.stdout || "").trim().endsWith("0");
  }
  return spawnSync("taskkill", ["/PID", String(pid), "/T", "/F"], { stdio: "ignore", windowsHide: true }).status === 0;
}

function startElevatedRuntime(args, env) {
  const stamp = `${Date.now()}-${Math.random().toString(36).slice(2)}`;
  const scriptPath = path.join(runtimeDir, `elevated-start-${stamp}.ps1`);
  const launchPidPath = path.join(runtimeDir, `elevated-start-${stamp}.pid`);
  const argumentList = args.length
    ? `-ArgumentList @(${args.map(arg => `'${powershellLiteral(arg)}'`).join(",")})`
    : "";
  const forwarded = ["NEXUS_SYSTEM_ACTION_DRYRUN", "NEXUS_TIME_SCALE", "NEXUS_ADB_EXE", "NEXUS_MUMU_MANAGER_EXE"]
    .filter(key => env[key])
    .map(key => `$env:${key}='${powershellLiteral(env[key])}'`)
    .join("; ");
  const script = [
    forwarded,
    `$p=Start-Process -FilePath '${powershellLiteral(runtimeExe)}' ${argumentList} -WorkingDirectory '${powershellLiteral(runtimeDir)}' -WindowStyle Hidden -PassThru`,
    `Set-Content -LiteralPath '${powershellLiteral(launchPidPath)}' -Value $p.Id -Encoding ascii`,
  ].filter(Boolean).join("; ");
  fs.writeFileSync(scriptPath, script, "utf8");
  const command = `$p=Start-Process -FilePath 'pwsh.exe' -ArgumentList @('-NoProfile','-NonInteractive','-ExecutionPolicy','Bypass','-File','${powershellLiteral(scriptPath)}') -Verb RunAs -WindowStyle Hidden -PassThru; [Console]::WriteLine($p.Id)`;
  const result = spawnSync("pwsh", ["-NoProfile", "-NonInteractive", "-Command", command], { encoding: "utf8", windowsHide: true });
  let pid = 0;
  const deadline = Date.now() + 15000;
  while (Date.now() < deadline) {
    if (fs.existsSync(launchPidPath)) {
      pid = Number(fs.readFileSync(launchPidPath, "utf8").trim());
      if (Number.isInteger(pid) && pid > 0) break;
    }
    spawnSync("pwsh", ["-NoProfile", "-NonInteractive", "-Command", "Start-Sleep -Milliseconds 100"], { stdio: "ignore", windowsHide: true });
  }
  fs.rmSync(scriptPath, { force: true });
  fs.rmSync(launchPidPath, { force: true });
  if (!pid) throw new Error(`提权启动 System Smoke runtime 失败：${result.stderr || result.error?.message || "未获得 PID"}`);
  return { pid, kill: () => killPid(pid) };
}

export function startRuntime(args = [], extraEnv = {}) {
  stdout = "";
  stderr = "";
  const env = {
    ...process.env,
    NEXUS_SYSTEM_ACTION_DRYRUN: "1",
    NEXUS_ADB_EXE: adbStub,
    NEXUS_MUMU_MANAGER_EXE: mumuStub,
    ...extraEnv,
  };
  child = process.env.NEXUS_SYSTEM_SMOKE_ELEVATED === "1"
    ? startElevatedRuntime(args[0] === "web" ? [] : args, env)
    : spawn(runtimeExe, args, {
      cwd: runtimeDir,
      env,
      stdio: ["pipe", "pipe", "pipe"],
      windowsHide: true,
    });
  child.stdout?.on("data", chunk => { stdout += chunk.toString(); });
  child.stderr?.on("data", chunk => { stderr += chunk.toString(); });
  child.on?.("error", error => { stderr += error.stack || error.message; });
  return child;
}

export async function stopRuntime() {
  if (child && !child.killed) killPid(Number(child.pid));
  killRuntimeProcesses();
  const deadline = Date.now() + 10000;
  while (Date.now() < deadline && runtimePids().length > 0) await sleep(250);
  child = null;
}

export function runtimeOutput() {
  return { stdout, stderr };
}

export async function waitForService(url = baseUrl, timeoutMs = 30000) {
  const deadline = Date.now() + timeoutMs;
  let lastError = "";
  while (Date.now() < deadline) {
    try {
      const response = await fetch(url + "api/status");
      if (response.ok) return;
      lastError = `HTTP ${response.status}`;
    } catch (error) {
      lastError = error.message;
    }
    await sleep(250);
  }
  throw new Error(`System Smoke 服务未启动：${url}；${lastError}\n${runtimeOutput().stderr}`);
}

export async function waitFor(predicate, timeoutMs = 30000, intervalMs = 250) {
  const deadline = Date.now() + timeoutMs;
  while (Date.now() < deadline) {
    if (await predicate()) return true;
    await sleep(intervalMs);
  }
  return !!(await predicate());
}

export async function api(method, pathName, body, url = baseUrl) {
  const options = { method };
  if (body !== undefined) {
    options.headers = { "Content-Type": "application/json" };
    options.body = JSON.stringify(body);
  }
  return fetch(url + pathName.replace(/^\/+/, ""), options);
}

export function makeFixture(label) {
  const dir = path.join(runtimeDir, "fixtures", `${label}-${Date.now()}-${Math.random().toString(36).slice(2)}`);
  fs.mkdirSync(path.join(dir, "cfg"), { recursive: true });
  fs.mkdirSync(path.join(dir, "logs"), { recursive: true });
  return {
    dir,
    cfg: path.join(dir, "cfg"),
    log: path.join(dir, "logs", "run.log"),
    exe: path.join(dir, `${label}.bat`),
  };
}

export function writeBatch(fixture, lines) {
  fs.writeFileSync(fixture.exe, ["@echo off", ...lines, "exit /b 0", ""].join("\r\n"), "ascii");
}

export async function waitForHistory(scriptId, timeoutMs = 30000) {
  let record = null;
  await waitFor(async () => {
    const response = await api("GET", "/api/history?days=7&offset=0&limit=100");
    if (!response.ok) return false;
    const payload = await response.json();
    const records = Array.isArray(payload) ? payload : payload.records;
    record = records?.find(item => item.scriptInstanceId === scriptId || item.ScriptInstanceId === scriptId) || null;
    return record !== null;
  }, timeoutMs);
  return record;
}

export async function waitNoRunning(timeoutMs = 30000) {
  return waitFor(async () => {
    const response = await api("GET", "/api/status");
    if (!response.ok) return false;
    const status = await response.json();
    return (status.running || []).length === 0;
  }, timeoutMs);
}

export async function deleteScript(scriptId) {
  if (scriptId) await api("DELETE", `/api/scripts/${encodeURIComponent(scriptId)}`);
}
