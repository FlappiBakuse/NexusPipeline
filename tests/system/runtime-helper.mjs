import { spawn } from "node:child_process";
import fs from "node:fs";
import path from "node:path";
import { fileURLToPath } from "node:url";
import {
  isAdministrator,
  isProcessAlive,
  killProcessTree,
  readPidFile,
} from "../support/windows-process.mjs";
import {
  copyReleaseArtifacts,
  fetchWithTimeout,
  installEmulatorStubs,
  requireExecutionMode,
  resolveTestHostDir,
  resolveTestHostExitFile,
  sleep,
  stopSpawnedService,
} from "../support/test-runtime.mjs";

export { isAdministrator, fetchWithTimeout };

const here = path.dirname(fileURLToPath(import.meta.url));
export const projectRoot = path.resolve(here, "..", "..");
const productionReleaseDir = path.join(projectRoot, "release");
const executionMode = requireExecutionMode("System Smoke");
export { executionMode };
export const isCodexMode = executionMode === "codex";
export const isAdminMode = executionMode === "admin";
export const testHostDir = resolveTestHostDir(projectRoot);
export const releaseDir = isCodexMode ? testHostDir : productionReleaseDir;
const configuredWebPort = Number(process.env.NEXUS_SYSTEM_WEB_PORT?.trim() || "58731");
if (!Number.isInteger(configuredWebPort) || configuredWebPort < 1024 || configuredWebPort > 65535) {
  throw new Error(`非法 NEXUS_SYSTEM_WEB_PORT：${process.env.NEXUS_SYSTEM_WEB_PORT}`);
}
export const systemWebPort = configuredWebPort;
const runtimeName = process.env.NEXUS_SYSTEM_RUNTIME_NAME || "runtime";
if (!/^[A-Za-z0-9_-]+$/.test(runtimeName)) {
  throw new Error(`非法 NEXUS_SYSTEM_RUNTIME_NAME：${runtimeName}`);
}
export const runtimeDir = path.join(here, runtimeName);
export const runtimeExe = path.join(runtimeDir, "nexus-pipeline.exe");
export const servicePidPath = path.join(runtimeDir, ".nxp", "runtime", "service.pid");
export const testHostExitFile = resolveTestHostExitFile(
  projectRoot,
  path.join(runtimeDir, ".nxp", "test-host.exit"),
);
const webPortPath = path.join(runtimeDir, ".nxp", "runtime", "web.port");
export const baseUrl = `http://127.0.0.1:${systemWebPort}/`;
export const adbStub = path.join(runtimeDir, "adb-stub", "adb-stub.cmd");
export const mumuStub = path.join(runtimeDir, "mumu-stub", "mumu-manager-stub.cmd");

let child = null;
const scriptUserIds = new Map();
let stdout = "";
let stderr = "";

export { sleep };

export function serviceUrl() {
  try {
    const port = Number(fs.readFileSync(webPortPath, "utf8").trim());
    if (Number.isInteger(port) && port >= 1024 && port <= 65535) {
      return `http://127.0.0.1:${port}/`;
    }
  } catch {
    // 服务启动前尚未生成 web.port，继续使用配置端口轮询。
  }
  return baseUrl;
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
    throw new Error(`${releaseDir}/nexus-pipeline.exe 不存在，请先运行 node tests/run.mjs ${executionMode} system`);
  }
  copyReleaseArtifacts(releaseDir, runtimeDir);
  if (process.env.NEXUS_SYSTEM_WEB_PORT?.trim()) {
    fs.mkdirSync(path.join(runtimeDir, "config"), { recursive: true });
    fs.writeFileSync(
      path.join(runtimeDir, "config", "settings.json"),
      JSON.stringify({ WebPort: systemWebPort }),
      "utf8",
    );
  }

  installEmulatorStubs(runtimeDir, path.join(projectRoot, "tests", "e2e", "tests", "fixtures"));
}

export function isRuntimeAlive(pid) {
  return isProcessAlive(Number(pid));
}

export function startRuntime(args = [], extraEnv = {}) {
  stdout = "";
  stderr = "";
  if (isCodexMode) fs.rmSync(testHostExitFile, { force: true });
  const localNoProxy = [process.env.NO_PROXY, process.env.no_proxy, "127.0.0.1", "localhost"]
    .filter(Boolean)
    .join(",");
  const env = {
    ...process.env,
    NEXUS_SYSTEM_ACTION_DRYRUN: "1",
    NEXUS_ADB_EXE: adbStub,
    NEXUS_MUMU_MANAGER_EXE: mumuStub,
    NO_PROXY: localNoProxy,
    no_proxy: localNoProxy,
    HTTP_PROXY: "",
    HTTPS_PROXY: "",
    http_proxy: "",
    https_proxy: "",
    NEXUS_TEST_MODE: executionMode,
    ...extraEnv,
  };
  for (const key of ["NEXUS_TEST_HOST", "NEXUS_TEST_HOST_DIR", "NEXUS_TEST_HOST_EXIT_FILE"]) delete env[key];
  if (isCodexMode) {
    env.NEXUS_TEST_HOST = "1";
    env.NEXUS_TEST_HOST_DIR = testHostDir;
    env.NEXUS_TEST_HOST_EXIT_FILE = testHostExitFile;
  }
  // System Smoke 统一使用 web 模式：stdin EOF 可触发受控退出，重启测试不依赖管理员 taskkill。
  const launchArgs = args.length === 0 ? ["web"] : args;
  child = spawn(runtimeExe, launchArgs, {
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
  const currentChild = child;
  await stopSpawnedService({
    child: currentChild,
    exitFile: testHostExitFile,
    pidFilePath: servicePidPath,
    exitWaitPollMs: 100,
  });
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

export async function waitForService(url = null, timeoutMs = 30000) {
  const deadline = Date.now() + timeoutMs;
  let lastError = "";
  let attempts = 0;
  while (Date.now() < deadline) {
    attempts++;
    try {
      const requestTimeoutMs = Math.min(3000, Math.max(1, deadline - Date.now()));
      const targetUrl = url ?? serviceUrl();
      const response = await fetchWithTimeout(targetUrl + "api/status", {}, requestTimeoutMs);
      // 消费响应体，避免 keep-alive 连接挂起未读完的数据（Node 24 undici 断言崩溃 / 宿主写响应失败）。
      await response.arrayBuffer();
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

const restartServiceName = "NexusPipeline";
const restartObservationLimit = 8;

function normalizePort(value) {
  const port = Number(value);
  return Number.isInteger(port) && port >= 1024 && port <= 65535 ? port : null;
}

function normalizePorts(values) {
  return [...new Set(values.map(normalizePort).filter(port => port !== null))];
}

function restartStatusUrl(baseUrl, port = null) {
  const target = new URL(baseUrl);
  if (port !== null) target.port = String(port);
  target.pathname = "/";
  target.search = "";
  target.hash = "";
  return new URL("api/status", target).toString();
}

function rememberRestartObservation(observations, observation) {
  observations.push(observation);
  if (observations.length > restartObservationLimit) observations.shift();
}

function formatRestartObservation(observation) {
  return [
    `+${observation.elapsedMs}ms`,
    `target=${observation.target}`,
    `http=${observation.httpStatus ?? "-"}`,
    `service=${observation.service || "-"}`,
    `instance=${observation.instanceId || "-"}`,
    `handoff=${observation.restartHandoffId || "-"}`,
    `actualPort=${observation.actualPort || "-"}`,
    `result=${observation.result || "-"}`,
  ].join(" ");
}

/**
 * 等待本次重启交接的新服务实例，不把旧实例的 HTTP 200 当作恢复完成。
 * 成功必须同时满足 NexusPipeline 身份、新 instanceId、本次 handoffId，以及（如提供）候选端口约束。
 */
export async function waitForRestartedService(options = {}) {
  const previousInstanceId = String(options.previousInstanceId || "");
  const expectedHandoffId = String(options.expectedHandoffId || "");
  if (!previousInstanceId || !expectedHandoffId) {
    throw new Error("System Smoke 重启等待必须提供 previousInstanceId 和 expectedHandoffId");
  }

  const timeoutValue = Number(options.timeoutMs);
  const intervalValue = Number(options.intervalMs);
  const timeoutMs = Number.isFinite(timeoutValue) && timeoutValue > 0 ? timeoutValue : 30000;
  const intervalMs = Number.isFinite(intervalValue) && intervalValue > 0 ? intervalValue : 250;
  const expectedPorts = normalizePorts([
    ...(Array.isArray(options.candidatePorts) ? options.candidatePorts : []),
    options.expectedPort,
  ]);
  const startedAt = Date.now();
  const deadline = startedAt + timeoutMs;
  const observations = [];
  let attempts = 0;
  let lastError = "";

  while (Date.now() < deadline) {
    attempts++;
    const baseUrl = options.url ?? serviceUrl();
    const targets = expectedPorts.length > 0
      ? expectedPorts.map(port => restartStatusUrl(baseUrl, port))
      : [restartStatusUrl(baseUrl)];
    const remainingMs = Math.max(1, deadline - Date.now());
    const results = await Promise.all(targets.map(async target => {
      const observation = {
        elapsedMs: Date.now() - startedAt,
        target,
        httpStatus: null,
        service: "",
        instanceId: "",
        restartHandoffId: "",
        actualPort: "",
        result: "",
        payload: null,
        matched: false,
      };
      try {
        const requestTimeoutMs = Math.min(3000, remainingMs);
        const response = await fetchWithTimeout(target, {}, requestTimeoutMs);
        observation.httpStatus = response.status;
        const body = await response.text();
        if (!response.ok) {
          observation.result = `HTTP ${response.status}`;
          return observation;
        }

        let payload;
        try {
          payload = JSON.parse(body);
        } catch {
          observation.result = "响应不是 JSON";
          return observation;
        }
        const status = payload && typeof payload === "object" ? payload : {};
        observation.service = String(status.service || "");
        observation.instanceId = String(status.instanceId || "");
        observation.restartHandoffId = String(status.restartHandoffId || "");
        observation.actualPort = String(status.actualPort || "");
        const actualPort = normalizePort(status.actualPort);
        const serviceMatches = observation.service === restartServiceName;
        const instanceMatches = Boolean(observation.instanceId)
          && observation.instanceId !== previousInstanceId;
        const handoffMatches = observation.restartHandoffId === expectedHandoffId;
        const portMatches = expectedPorts.length === 0
          || (actualPort !== null && expectedPorts.includes(actualPort));
        observation.matched = serviceMatches && instanceMatches && handoffMatches && portMatches;
        observation.payload = payload;
        observation.result = observation.matched
          ? "匹配本次重启的新实例"
          : "服务身份或重启交接标识未匹配";
        return observation;
      } catch (error) {
        observation.result = error?.message || String(error);
        return observation;
      }
    }));

    for (const observation of results) {
      rememberRestartObservation(observations, observation);
      lastError = observation.result;
      if (observation.matched) return observation.payload;
    }

    const waitMs = deadline - Date.now();
    if (waitMs > 0) await sleep(Math.min(intervalMs, waitMs));
  }

  const latest = observations.at(-1) || {};
  const expectedPortText = expectedPorts.length > 0 ? expectedPorts.join(",") : "任意有效端口";
  const observationText = observations.length > 0
    ? observations.map(formatRestartObservation).join("\n")
    : "（无响应观察记录）";
  throw new Error([
    `System Smoke 未确认本次重启的新服务实例：尝试 ${attempts} 次；已耗时 ${Date.now() - startedAt}ms；${lastError}`,
    `previous instanceId: ${previousInstanceId}`,
    `expected handoffId: ${expectedHandoffId}`,
    `expected actualPort: ${expectedPortText}`,
    `observed instanceId: ${latest.instanceId || "unknown"}`,
    `observed restartHandoffId: ${latest.restartHandoffId || "unknown"}`,
    `observed actualPort: ${latest.actualPort || "unknown"}`,
    `observed HTTP status: ${latest.httpStatus ?? "unknown"}`,
    `service.pid: ${readPidFile(servicePidPath) ?? "unknown"}`,
    `current PID: ${child?.pid ?? "unknown"}`,
    "recent observations:",
    observationText,
    runtimeDiagnostic(),
  ].join("\n"));
}

export async function waitFor(predicate, timeoutMs = 30000, intervalMs = 250) {
  const deadline = Date.now() + timeoutMs;
  while (Date.now() < deadline) {
    if (await predicate()) return true;
    await sleep(intervalMs);
  }
  return !!(await predicate());
}

export async function api(method, pathName, body, url = null) {
  const options = { method };
  if (body !== undefined) {
    options.headers = { "Content-Type": "application/json" };
    options.body = JSON.stringify(body);
  }
  return fetchWithTimeout((url ?? serviceUrl()) + pathName.replace(/^\/+/, ""), options);
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

export async function createUserBinding(scriptId, name, binding = {}) {
  const userResponse = await api("POST", "/api/users", { name });
  if (!userResponse.ok) {
    throw new Error(`创建全局用户失败：HTTP ${userResponse.status} ${await userResponse.text()}`);
  }
  const user = await userResponse.json();
  const userIds = scriptUserIds.get(scriptId) || [];
  userIds.push(user.id);
  scriptUserIds.set(scriptId, userIds);

  const bindingResponse = await api(
    "POST",
    `/api/users/${encodeURIComponent(user.id)}/bindings`,
    { scriptInstanceId: scriptId, enabled: true, ...binding },
  );
  if (!bindingResponse.ok) {
    throw new Error(`创建用户绑定失败：HTTP ${bindingResponse.status} ${await bindingResponse.text()}`);
  }
  return user;
}

export async function deleteScript(scriptId) {
  if (!scriptId) return;
  const userIds = scriptUserIds.get(scriptId) || [];
  for (const userId of userIds) {
    const userResponse = await api("GET", `/api/users/${encodeURIComponent(userId)}`);
    const user = userResponse.ok ? await userResponse.json() : null;
    if (user) {
      await api("DELETE", `/api/users/${encodeURIComponent(userId)}`, { confirmName: user.name });
    }
  }
  await api("DELETE", `/api/scripts/${encodeURIComponent(scriptId)}`);
  scriptUserIds.delete(scriptId);
}
