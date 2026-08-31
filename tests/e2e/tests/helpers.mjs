import { spawn } from "node:child_process";
import fs from "node:fs";
import path from "node:path";
import { fileURLToPath } from "node:url";
import {
  isProcessAlive,
  killProcessTree,
  readPidFile,
} from "../../support/windows-process.mjs";
import {
  copyReleaseArtifacts,
  installEmulatorStubs,
  requireExecutionMode,
  resolveTestHostDir,
  resolveTestHostExitFile,
  sleep,
  stopSpawnedService,
} from "../../support/test-runtime.mjs";

const __dirname = path.dirname(fileURLToPath(import.meta.url));
export const projectRoot = path.resolve(__dirname, "..", "..", "..");
const productionReleaseDir = path.join(projectRoot, "release");
const testMode = requireExecutionMode("E2E");
export const executionMode = testMode;
export const isCodexMode = testMode === "codex";
export const isAdminMode = testMode === "admin";
export const testHostDir = resolveTestHostDir(projectRoot);
export const runtimeDir = path.join(__dirname, "..", "runtime");
export const runtimeExe = path.join(runtimeDir, "nexus-pipeline.exe");
export const servicePidPath = path.join(runtimeDir, ".nxp", "runtime", "service.pid");
export const testHostExitFile = resolveTestHostExitFile(
  projectRoot,
  path.join(runtimeDir, ".nxp", "test-host.exit"),
);
export const releaseDir = isCodexMode ? testHostDir : productionReleaseDir;
export const baseUrl = "http://127.0.0.1:58731/";
export const JSON_HDR = { "Content-Type": "application/json" };
export const PING_GAME = "C:\\Windows\\System32\\PING.EXE";

/** 测试插件仓库：兼容 CI 工作区子目录、本地相邻仓库和显式路径。 */
export function pluginRepositoryRoot() {
  const configured = process.env.NEXUS_PLUGIN_REPO_ROOT?.trim();
  const candidates = [
    configured ? (path.isAbsolute(configured) ? configured : path.resolve(projectRoot, configured)) : null,
    path.join(projectRoot, "NexusPipeline-Plugins"),
    path.resolve(projectRoot, "..", "NexusPipeline-Plugins"),
  ].filter(Boolean);
  return candidates.find(candidate => fs.existsSync(path.join(candidate, "catalog.json"))) || candidates[0];
}

let child = null;

function ownedPids() {
  const pids = new Set();
  const marked = readPidFile(servicePidPath);
  if (marked) pids.add(marked);
  if (child?.pid) pids.add(Number(child.pid));
  return [...pids].filter(pid => Number.isInteger(pid) && pid > 0);
}

export function setupRuntime() {
  for (const pid of ownedPids()) killProcessTree(pid);
  fs.rmSync(runtimeDir, { recursive: true, force: true, maxRetries: 120, retryDelay: 250 });
  fs.mkdirSync(runtimeDir, { recursive: true });
  const sourceExe = path.join(releaseDir, "nexus-pipeline.exe");
  if (!fs.existsSync(sourceExe)) throw new Error(`${releaseDir}/nexus-pipeline.exe 不存在，请先运行 node tests/run.mjs ${executionMode} ui`);
  copyReleaseArtifacts(releaseDir, runtimeDir);
  const repositoryPlugins = path.join(pluginRepositoryRoot(), "plugins");
  if (fs.existsSync(repositoryPlugins)) fs.cpSync(repositoryPlugins, path.join(runtimeDir, "plugins"), { recursive: true });

  installEmulatorStubs(runtimeDir, path.join(__dirname, "fixtures"));
}

export function startService() {
  fs.rmSync(servicePidPath, { force: true });
  if (isCodexMode) fs.rmSync(testHostExitFile, { force: true });
  const env = {
    ...process.env,
    NEXUS_SYSTEM_ACTION_DRYRUN: process.env.NEXUS_SYSTEM_ACTION_DRYRUN || "1",
    NEXUS_ADB_EXE: process.env.NEXUS_ADB_EXE || path.join(runtimeDir, "adb-stub", "adb-stub.cmd"),
    NEXUS_MUMU_MANAGER_EXE: process.env.NEXUS_MUMU_MANAGER_EXE || path.join(runtimeDir, "mumu-stub", "mumu-manager-stub.cmd"),
  };
  for (const key of ["NEXUS_TEST_HOST", "NEXUS_TEST_HOST_DIR", "NEXUS_TEST_HOST_EXIT_FILE"]) delete env[key];
  env.NEXUS_TEST_MODE = testMode;
  if (isCodexMode) {
    env.NEXUS_TEST_HOST = "1";
    env.NEXUS_TEST_HOST_DIR = testHostDir;
    env.NEXUS_TEST_HOST_EXIT_FILE = testHostExitFile;
  }
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
  const current = child;
  await stopSpawnedService({ child: current, exitFile: testHostExitFile, pidFilePath: servicePidPath });
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
  const userName = `E2E 用户-${Date.now()}`;
  const userResponse = await api("POST", "/api/users", { name: userName });
  if (!userResponse.ok) throw new Error(`创建 E2E 用户失败：HTTP ${userResponse.status}`);
  const user = await userResponse.json();
  const bindingResponse = await api("POST", `/api/users/${encodeURIComponent(user.id)}/bindings`, {
    scriptInstanceId: script.id,
    enabled: true,
  });
  if (!bindingResponse.ok) throw new Error(`创建 E2E 用户绑定失败：HTTP ${bindingResponse.status}`);
  return { ok: true, id: script.id, userId: user.id, userName };
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
