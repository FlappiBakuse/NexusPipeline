import { spawn } from "node:child_process";
import fs from "node:fs";
import path from "node:path";
import { fileURLToPath } from "node:url";
import {
  isAdministrator,
  isProcessAlive,
  killProcessTree,
  readPidFile,
  waitForExit,
} from "../support/windows-process.mjs";

const here = path.dirname(fileURLToPath(import.meta.url));
export const projectRoot = path.resolve(here, "..", "..");
export const releaseDir = path.join(projectRoot, "release");
const runtimeName = process.env.NEXUS_SYSTEM_RUNTIME_NAME || "runtime";
if (!/^[A-Za-z0-9_-]+$/.test(runtimeName)) {
  throw new Error(`非法 NEXUS_SYSTEM_RUNTIME_NAME：${runtimeName}`);
}
export const runtimeDir = path.join(here, runtimeName);
export const runtimeExe = path.join(runtimeDir, "nexus-pipeline.exe");
export const servicePidPath = path.join(runtimeDir, ".nxp", "runtime", "service.pid");
export const baseUrl = "http://127.0.0.1:58731/";
export const adbStub = path.join(runtimeDir, "adb-stub", "adb-stub.cmd");
export const mumuStub = path.join(runtimeDir, "mumu-stub", "mumu-manager-stub.cmd");

let child = null;
let stdout = "";
let stderr = "";

export const sleep = ms => new Promise(resolve => setTimeout(resolve, ms));

export function isElevated() {
  return isAdministrator();
}

function ownedPids() {
  const pids = new Set();
  const marked = readPidFile(servicePidPath);
  if (marked) pids.add(marked);
  if (child?.pid) pids.add(Number(child.pid));
  return [...pids].filter(pid => Number.isInteger(pid) && pid > 0);
}

export function prepareRuntime() {
  // 测试只清理自身 runtime 写入的 service.pid 所指向进程，避免全局进程扫描误杀用户实例。
  for (const pid of ownedPids()) killProcessTree(pid);
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

export function isRuntimeAlive(pid) {
  return isProcessAlive(Number(pid));
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
  child = spawn(runtimeExe, args, {
    cwd: runtimeDir,
    env,
    stdio: ["pipe", "pipe", "pipe"],
    windowsHide: true,
  });
  child.stdout?.on("data", chunk => { stdout += chunk.toString(); });
  child.stderr?.on("data", chunk => { stderr += chunk.toString(); });
  child.on("error", error => { stderr += error.stack || error.message; });
  return child;
}

export async function stopRuntime() {
  const pids = ownedPids();
  for (const pid of pids) killProcessTree(pid);
  for (const pid of pids) await waitForExit(pid, 10000, 250);
  child = null;
}

export function runtimeOutput() {
  return { stdout, stderr };
}

export function runtimeDiagnostic() {
  return [
    `runtime PID: ${child?.pid ?? "unknown"}`,
    `stdout:\n${stdout || "(empty)"}`,
    `stderr:\n${stderr || "(empty)"}`,
  ].join("\n");
}

export async function fetchWithTimeout(url, options = {}, timeoutMs = 5000) {
  const controller = new AbortController();
  const timer = setTimeout(() => controller.abort(), timeoutMs);
  try {
    return await fetch(url, { ...options, signal: controller.signal });
  } catch (error) {
    if (error?.name === "AbortError") {
      throw new Error(`HTTP 请求超时（${timeoutMs}ms）：${url}`, { cause: error });
    }
    throw error;
  } finally {
    clearTimeout(timer);
  }
}

export async function waitForService(url = baseUrl, timeoutMs = 30000) {
  const deadline = Date.now() + timeoutMs;
  let lastError = "";
  let attempts = 0;
  while (Date.now() < deadline) {
    attempts++;
    try {
      const requestTimeoutMs = Math.min(3000, Math.max(1, deadline - Date.now()));
      const response = await fetchWithTimeout(url + "api/status", {}, requestTimeoutMs);
      if (response.ok) return;
      lastError = `HTTP ${response.status}`;
    } catch (error) {
      lastError = error.message;
    }
    const remainingMs = deadline - Date.now();
    if (remainingMs > 0) await sleep(Math.min(250, remainingMs));
  }
  throw new Error(`System Smoke 服务未启动：${url}；尝试 ${attempts} 次；${lastError}\n${runtimeDiagnostic()}`);
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
  return fetchWithTimeout(url + pathName.replace(/^\/+/, ""), options);
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
