import { spawn } from "node:child_process";
import { createHash, randomUUID } from "node:crypto";
import fs from "node:fs";
import path from "node:path";
import { fileURLToPath } from "node:url";
import {
  isProcessAlive,
} from "../../support/windows-process.mjs";
import {
  copyReleaseArtifacts,
  createRunMarker,
  waitForRunMarker,
  ensureOwnedRuntimeDirectory,
  findAvailablePort,
  installEmulatorStubs,
  requireExecutionMode,
  resolveTestHostDir,
  resolveTestHostExitFile,
  sleep,
  stopSpawnedService,
} from "../../support/test-runtime.mjs";

const __dirname = path.dirname(fileURLToPath(import.meta.url));
export const projectRoot = path.resolve(__dirname, "..", "..", "..");
const testMode = requireExecutionMode("E2E");
export const executionMode = testMode;
export const runId = process.env.NEXUS_TEST_RUN_ID?.trim()
  || `standalone-${process.pid}-${Date.now()}-${Math.random().toString(36).slice(2, 8)}`;
export const testHostDir = resolveTestHostDir(projectRoot);
export const runtimeDir = path.join(projectRoot, "tests", ".artifacts", "runs", runId, "ui");
export const runtimeExe = path.join(runtimeDir, "nexus-pipeline.exe");
export const servicePidPath = path.join(runtimeDir, ".nxp", "runtime", "service.pid");
export const runMarkerPath = path.join(runtimeDir, ".nxp", "test-run-marker.json");
export const testHostExitFile = resolveTestHostExitFile(
  projectRoot,
  path.join(runtimeDir, ".nxp", "test-host.exit"),
);
export const releaseDir = testHostDir;
const configuredBaseUrl = process.env.NEXUS_E2E_BASE_URL?.trim();
const configuredWebPort = (() => {
  if (!configuredBaseUrl) return null;
  let parsed;
  try {
    parsed = new URL(configuredBaseUrl);
  } catch {
    throw new Error(`非法 NEXUS_E2E_BASE_URL：${configuredBaseUrl}`);
  }
  const port = Number(parsed.port);
  if (!Number.isInteger(port) || port < 1024 || port > 65535) {
    throw new Error(`NEXUS_E2E_BASE_URL 必须包含 1024-65535 之间的端口：${configuredBaseUrl}`);
  }
  return port;
})();
export let baseUrl = configuredBaseUrl
  ? `${configuredBaseUrl.replace(/\/+$/u, "")}/`
  : "";
export const JSON_HDR = { "Content-Type": "application/json" };
export const PING_GAME = "C:\\Windows\\System32\\PING.EXE";

/** 测试插件仓库与 contract 使用同一个显式来源。 */
export function pluginRepositoryRoot() {
  const configured = process.env.NEXUS_OFFICIAL_PLUGINS_ROOT?.trim();
  if (!configured) throw new Error("必须显式设置 NEXUS_OFFICIAL_PLUGINS_ROOT");
  const repository = path.resolve(projectRoot, configured);
  if (!fs.existsSync(path.join(repository, "catalog.json"))) throw new Error(`插件仓库缺少 catalog.json：${repository}`);
  return repository;
}

let child = null;

export async function setupRuntime() {
  await ensureOwnedRuntimeDirectory(runtimeDir, runMarkerPath, servicePidPath);
  const webPort = configuredWebPort ?? await findAvailablePort();
  if (!baseUrl) baseUrl = `http://127.0.0.1:${webPort}/`;
  fs.mkdirSync(path.join(runtimeDir, "config"), { recursive: true });
  fs.writeFileSync(
    path.join(runtimeDir, "config", "settings.json"),
    JSON.stringify({ WebPort: webPort }, null, 2),
    "utf8",
  );
  const sourceExe = path.join(releaseDir, "nexus-pipeline.exe");
  if (!fs.existsSync(sourceExe)) throw new Error(`${releaseDir}/nexus-pipeline.exe 不存在，请先运行 node tests/run.mjs release ui-runtime`);
  const repositoryPlugins = path.join(pluginRepositoryRoot(), "plugins");
  const frontendFixture = path.join(__dirname, "fixtures", "frontend-plugin");
  const pluginDirectories = fs.existsSync(repositoryPlugins)
    ? fs.readdirSync(repositoryPlugins, { withFileTypes: true })
      .filter(entry => entry.isDirectory())
      .map(entry => path.join(repositoryPlugins, entry.name))
    : [];
  if (fs.existsSync(frontendFixture)) pluginDirectories.push(frontendFixture);
  copyReleaseArtifacts(releaseDir, runtimeDir, { pluginDirectories });

  installEmulatorStubs(runtimeDir, path.join(__dirname, "fixtures"));
}

export function startService() {
  fs.rmSync(servicePidPath, { force: true });
  fs.rmSync(testHostExitFile, { force: true });
  const env = {
    ...process.env,
    NEXUS_SYSTEM_ACTION_DRYRUN: process.env.NEXUS_SYSTEM_ACTION_DRYRUN || "1",
    NEXUS_ADB_EXE: process.env.NEXUS_ADB_EXE || path.join(runtimeDir, "adb-stub", "adb-stub.cmd"),
    NEXUS_MUMU_MANAGER_EXE: process.env.NEXUS_MUMU_MANAGER_EXE || path.join(runtimeDir, "mumu-stub", "mumu-manager-stub.cmd"),
  };
  env.NEXUS_TEST_MODE = "test-host";
  env.NEXUS_TEST_RUN_ID = runId;
  env.NEXUS_TEST_HOST = "1";
  env.NEXUS_TEST_HOST_DIR = testHostDir;
  env.NEXUS_TEST_HOST_EXIT_FILE = testHostExitFile;
  env.NEXUS_TEST_OWNERSHIP_NONCE = randomUUID();
  env.NEXUS_SYSTEM_RUNTIME_NAME = `ui-${createHash("sha256").update(fs.realpathSync.native(runtimeDir).toLowerCase()).digest("hex").slice(0, 24)}`;
  child = spawn(runtimeExe, ["web"], {
    cwd: runtimeDir,
    stdio: ["pipe", "ignore", "ignore"],
    env,
    windowsHide: true,
  });
  createRunMarker(runMarkerPath, runtimeExe, child, { nonce: env.NEXUS_TEST_OWNERSHIP_NONCE, identityFile: `${testHostExitFile}.identity.json`, runId });
}

export async function waitForService(timeoutMs = 30000) {
  await waitForRunMarker(child);
  const deadline = Date.now() + timeoutMs;
  let lastFailure = "未尝试";
  while (Date.now() < deadline) {
    try {
      const response = await fetch(baseUrl + "api/status");
      // 必须消费响应体：不消费会让 keep-alive 连接挂起未读完的数据，
      // Node 24 的 undici 在该连接关闭时会触发内部断言崩溃，宿主侧写响应也会因连接中断失败。
      await response.arrayBuffer();
      if (response.ok) return;
      lastFailure = `HTTP ${response.status}`;
    } catch (error) {
      // 启动窗口内端口尚未监听。
      lastFailure = error instanceof Error ? error.message : String(error);
    }
    await sleep(250);
  }
  throw new Error(`服务未在 ${timeoutMs}ms 内启动：baseUrl=${baseUrl || "<empty>"}，last=${lastFailure}`);
}

export async function stopService() {
  const current = child;
  await stopSpawnedService({ child: current, exitFile: testHostExitFile, pidFilePath: servicePidPath, markerPath: runMarkerPath });
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
