import { spawn } from "node:child_process";
import fs from "node:fs";
import path from "node:path";
import { fileURLToPath } from "node:url";
import {
  isProcessAlive,
  killProcessTree,
  readPidFile,
  waitForExit,
} from "../../support/windows-process.mjs";

const __dirname = path.dirname(fileURLToPath(import.meta.url));
export const projectRoot = path.resolve(__dirname, "..", "..", "..");
export const releaseDir = path.join(projectRoot, "release");
export const runtimeDir = path.join(__dirname, "..", "runtime");
export const runtimeExe = path.join(runtimeDir, "nexus-pipeline.exe");
export const servicePidPath = path.join(runtimeDir, ".nxp", "runtime", "service.pid");
export const baseUrl = "http://127.0.0.1:58731/";
export const JSON_HDR = { "Content-Type": "application/json" };
export const PING_GAME = "C:\\Windows\\System32\\PING.EXE";

let child = null;

export const sleep = ms => new Promise(resolve => setTimeout(resolve, ms));

function ownedPids() {
  const pids = new Set();
  const marked = readPidFile(servicePidPath);
  if (marked) pids.add(marked);
  if (child?.pid) pids.add(Number(child.pid));
  return [...pids].filter(pid => Number.isInteger(pid) && pid > 0);
}

function stopOwnedProcessesSync() {
  for (const pid of ownedPids()) killProcessTree(pid);
}

export function setupRuntime() {
  stopOwnedProcessesSync();
  fs.rmSync(runtimeDir, { recursive: true, force: true, maxRetries: 120, retryDelay: 250 });
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

export function startService() {
  fs.rmSync(servicePidPath, { force: true });
  const env = {
    ...process.env,
    NEXUS_SYSTEM_ACTION_DRYRUN: process.env.NEXUS_SYSTEM_ACTION_DRYRUN || "1",
    NEXUS_ADB_EXE: process.env.NEXUS_ADB_EXE || path.join(runtimeDir, "adb-stub", "adb-stub.cmd"),
    NEXUS_MUMU_MANAGER_EXE: process.env.NEXUS_MUMU_MANAGER_EXE || path.join(runtimeDir, "mumu-stub", "mumu-manager-stub.cmd"),
  };
  child = spawn(runtimeExe, ["web"], {
    cwd: runtimeDir,
    stdio: ["pipe", "ignore", "ignore"],
    env,
    windowsHide: true,
  });
}

export async function waitForService(timeoutMs = 30000) {
  const deadline = Date.now() + timeoutMs;
  while (Date.now() < deadline) {
    try {
      const response = await fetch(baseUrl + "api/status");
      if (response.ok) return;
    } catch {
      // 启动窗口内端口尚未监听。
    }
    await sleep(250);
  }
  throw new Error(`服务未在 ${timeoutMs}ms 内启动`);
}

export async function stopService() {
  const pids = ownedPids();
  for (const pid of pids) killProcessTree(pid);
  for (const pid of pids) await waitForExit(pid, 10000, 250);
  child = null;
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

export function isRuntimeAlive(pid) {
  return isProcessAlive(Number(pid));
}
