import fs from "node:fs";
import net from "node:net";
import path from "node:path";
import { randomUUID } from "node:crypto";
import {
  isProcessAlive,
  killProcessTree,
  readPidFile,
  readProcessIdentity,
  waitForExit,
} from "./windows-process.mjs";

/**
 * System Smoke 与 e2e UI Smoke 共享的测试运行时辅助：
 * 模式解析、Test Host 路径、release 产物复制、stub 安装、受控停止。
 * 两层的差异化逻辑（Playwright 服务编排 / web.port 探测、诊断输出）留在各自的 helper。
 */

export function requireExecutionMode(layerLabel) {
  const mode = process.env.NEXUS_TEST_MODE?.trim().toLowerCase();
  if (mode !== "test-host") {
    throw new Error(`${layerLabel} 必须使用 NexusTestHost；请通过 tests\\run.mjs 的 dev/release 入口启动。`);
  }
  return mode;
}

export function resolveTestHostDir(projectRoot) {
  const configured = process.env.NEXUS_TEST_HOST_DIR?.trim();
  const runId = process.env.NEXUS_TEST_RUN_ID?.trim() || "standalone";
  return configured
    ? (path.isAbsolute(configured) ? configured : path.resolve(projectRoot, configured))
    : path.join(projectRoot, "tests", ".artifacts", "runs", runId, "test-host");
}

export function resolveTestHostExitFile(projectRoot, fallbackPath) {
  const configured = process.env.NEXUS_TEST_HOST_EXIT_FILE?.trim();
  return configured
    ? (path.isAbsolute(configured) ? configured : path.resolve(projectRoot, configured))
    : fallbackPath;
}

export const sleep = ms => new Promise(resolve => setTimeout(resolve, ms));

export async function findAvailablePort(host = "127.0.0.1") {
  const server = net.createServer();
  await new Promise((resolve, reject) => {
    server.once("error", reject);
    server.listen(0, host, resolve);
  });
  const address = server.address();
  const port = typeof address === "object" && address ? address.port : null;
  await new Promise((resolve, reject) => server.close(error => error ? reject(error) : resolve()));
  if (!Number.isInteger(port) || port < 1024 || port > 65535) throw new Error(`无法分配隔离端口：${port}`);
  return port;
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

function assertSafeTree(source, root) {
  const relative = path.relative(root, source);
  if (relative.startsWith("..") || path.isAbsolute(relative)) {
    throw new Error(`产物路径越出根目录：${source}`);
  }
  const stat = fs.lstatSync(source);
  if (stat.isSymbolicLink()) {
    throw new Error(`产物清单拒绝符号链接：${source}`);
  }
  if (stat.isDirectory()) {
    for (const entry of fs.readdirSync(source)) assertSafeTree(path.join(source, entry), root);
  }
}

function copySafe(source, target, root) {
  assertSafeTree(source, root);
  const stat = fs.lstatSync(source);
  if (stat.isDirectory()) {
    fs.mkdirSync(target, { recursive: true });
    for (const entry of fs.readdirSync(source)) copySafe(path.join(source, entry), path.join(target, entry), root);
    return;
  }
  fs.mkdirSync(path.dirname(target), { recursive: true });
  fs.copyFileSync(source, target);
}

function isRuntimeFile(name) {
  return name === "nexus-pipeline.exe"
    || /\.(?:dll|deps\.json|runtimeconfig\.json)$/iu.test(name);
}

/**
 * 把本次构建清单中的 runtime 产物复制进隔离目录。
 * 不复制 config/data/history/logs/.nxp、压缩包、校验文件或未知旁车文件。
 */
export function copyReleaseArtifacts(releaseDir, runtimeDir, { pluginDirectories = [] } = {}) {
  const sourceExe = path.join(releaseDir, "nexus-pipeline.exe");
  if (!fs.existsSync(sourceExe)) {
    throw new Error(`${releaseDir}/nexus-pipeline.exe 不存在（runtime 缺构建产物）。`);
  }
  fs.mkdirSync(runtimeDir, { recursive: true });
  for (const entry of fs.readdirSync(releaseDir, { withFileTypes: true })) {
    const source = path.join(releaseDir, entry.name);
    if (entry.isDirectory() && entry.name === "wwwroot") {
      copySafe(source, path.join(runtimeDir, "wwwroot"), releaseDir);
      continue;
    }
    if (entry.isFile() && isRuntimeFile(entry.name)) {
      copySafe(source, path.join(runtimeDir, entry.name), releaseDir);
      continue;
    }
    if (entry.isFile() && entry.name === ".complete.json") continue;
    if (entry.name === "plugins") continue;
    throw new Error(`产物清单含未允许的旁车文件：${entry.name}`);
  }
  const pluginsTarget = path.join(runtimeDir, "plugins");
  for (const directory of pluginDirectories) {
    const source = path.resolve(directory);
    if (!fs.existsSync(source)) throw new Error(`显式 fixture 插件不存在：${source}`);
    const target = path.join(pluginsTarget, path.basename(source));
    copySafe(source, target, path.dirname(source));
  }
}

const spawnedRegistrations = new WeakMap();

export function createRunMarker(markerPath, executablePath, child, { nonce = randomUUID(), identityFile = null, handoffExecutablePath = executablePath, runId = process.env.NEXUS_TEST_RUN_ID || "", identityReader = readProcessIdentity } = {}) {
  const pid = child?.pid;
  const marker = {
    schemaVersion: 1,
    nonce,
    identityFile,
    runId,
    executablePath: path.normalize(path.resolve(executablePath)),
    handoffExecutablePath: path.normalize(path.resolve(handoffExecutablePath)),
    pid: Number(pid),
    startedAtUtc: new Date().toISOString(),
    processStartTimeUtc: "",
  };
  if (!Number.isInteger(marker.pid) || marker.pid <= 0) throw new Error(`非法运行 PID：${pid}`);
  fs.mkdirSync(path.dirname(markerPath), { recursive: true });
  fs.writeFileSync(markerPath, `${JSON.stringify(marker, null, 2)}\n`, "utf8");
  // This registration is tied to the live ChildProcess, never to a PID read
  // later during cleanup. Once captured, creation time is immutable.
  const registration = (async () => {
    const deadline = Date.now() + 5000;
    do {
      if (child.exitCode !== null || child.signalCode !== null) return;
      const identity = identityReader(pid);
      if (child.exitCode !== null || child.signalCode !== null) return;
      if (identity?.startTime && sameExecutable(identity.executablePath, executablePath)) {
        const current = readRunMarker(markerPath);
        if (current?.nonce !== nonce || current.pid !== pid) throw new Error("启动身份登记时 marker 已变化");
        fs.writeFileSync(markerPath, `${JSON.stringify({ ...marker, processStartTimeUtc: identity.startTime }, null, 2)}\n`, "utf8");
        return;
      }
      await sleep(50);
    } while (Date.now() < deadline);
    throw new Error(`无法确认本次子进程启动身份：PID=${pid}`);
  })();
  // Keep rejection observable by waitForRunMarker without unhandled rejection.
  spawnedRegistrations.set(child, registration.then(() => null, error => error));
  return marker;
}

export async function waitForRunMarker(child) {
  const error = child ? await spawnedRegistrations.get(child) : null;
  if (error) throw error;
}

export function readRunMarker(markerPath) {
  try {
    const marker = JSON.parse(fs.readFileSync(markerPath, "utf8"));
    if (marker?.schemaVersion !== 1 || !marker.nonce || !Number.isInteger(marker.pid) || marker.pid <= 0) return null;
    return marker;
  } catch {
    return null;
  }
}

export const OWNERSHIP = Object.freeze({
  OWNED: "OWNED",
  NOT_OWNED: "NOT_OWNED",
  UNKNOWN: "UNKNOWN",
  EXITED: "EXITED",
});

function sameExecutable(left, right) {
  if (typeof left !== "string" || typeof right !== "string" || !left || !right) return false;
  try {
    return fs.realpathSync.native(left).toLowerCase() === fs.realpathSync.native(right).toLowerCase();
  } catch { return false; }
}

/** Register only a process presenting this run's receipt and matching OS identity. */
export function registerHandoffProcess(markerPath, pid, { identityReader = readProcessIdentity, expectedInstanceId, expectedHandoffId } = {}) {
  const marker = readRunMarker(markerPath);
  if (!marker || pid === marker.pid || marker.handoffProcesses?.some(item => item.pid === pid)) return;
  let receipt;
  try { receipt = JSON.parse(fs.readFileSync(marker.identityFile, "utf8")); } catch { return; }
  let identity;
  try { identity = identityReader(pid); } catch { return; }
  if (!receipt?.instanceId || receipt.nonce !== marker.nonce || receipt.runId !== marker.runId
      || receipt.pid !== pid || !receipt.processStartTimeUtc || !identity?.startTime
      || receipt.processStartTimeUtc !== identity.startTime
      || !sameExecutable(identity.executablePath, marker.handoffExecutablePath || marker.executablePath)
      || !sameExecutable(receipt.executablePath, marker.handoffExecutablePath || marker.executablePath)
      || (expectedInstanceId && receipt.instanceId !== expectedInstanceId)
      || (expectedHandoffId && receipt.restartHandoffId !== expectedHandoffId)) return;
  fs.writeFileSync(markerPath, `${JSON.stringify({ ...marker, handoffProcesses: [...(marker.handoffProcesses || []), receipt] }, null, 2)}\n`, "utf8");
}

function processAlive(pid, reader) {
  try {
    return Boolean(reader(Number(pid)));
  } catch {
    return null;
  }
}

export function inspectProcessOwnership(markerPath, pid, { identityReader = readProcessIdentity, aliveReader = isProcessAliveSafe } = {}) {
  const marker = readRunMarker(markerPath);
  const numericPid = Number(pid);
  if (!Number.isInteger(numericPid) || numericPid <= 0) return OWNERSHIP.UNKNOWN;
  const alive = processAlive(numericPid, aliveReader);
  if (alive === false) return OWNERSHIP.EXITED;
  if (alive === null) return OWNERSHIP.UNKNOWN;
  if (!marker || marker.pid !== numericPid) return OWNERSHIP.UNKNOWN;
  let identity;
  try { identity = identityReader(numericPid); } catch { return OWNERSHIP.UNKNOWN; }
  // The process can finish between the liveness probe and the OS identity query.
  // Only a fresh, definitive absence is EXITED; inaccessible live identities stay UNKNOWN.
  if (!identity?.executablePath || !identity.startTime) {
    return processAlive(numericPid, aliveReader) === false ? OWNERSHIP.EXITED : OWNERSHIP.UNKNOWN;
  }
  if (!identity?.executablePath || !marker.executablePath || !marker.processStartTimeUtc || !identity.startTime) return OWNERSHIP.UNKNOWN;
  if (!sameExecutable(identity.executablePath, marker.executablePath)) return OWNERSHIP.NOT_OWNED;
  return marker.processStartTimeUtc === identity.startTime ? OWNERSHIP.OWNED : OWNERSHIP.NOT_OWNED;
}

/** Parent PID alone never authorizes a restarted process. */
export function inspectRestartedProcessOwnership(markerPath, pid, { identityReader = readProcessIdentity, aliveReader = isProcessAliveSafe } = {}) {
  return inspectHandoffProcessOwnership(markerPath, pid, { identityReader, aliveReader });
}

/** 重启交接确认后记录的新 PID：完整路径和 OS 启动时间均来自本次 status 已核验实例。 */
export function inspectHandoffProcessOwnership(markerPath, pid, { identityReader = readProcessIdentity, aliveReader = isProcessAliveSafe } = {}) {
  const marker = readRunMarker(markerPath);
  const numericPid = Number(pid);
  if (!Number.isInteger(numericPid) || numericPid <= 0) return OWNERSHIP.UNKNOWN;
  const alive = processAlive(numericPid, aliveReader);
  if (alive === false) return OWNERSHIP.EXITED;
  if (alive === null) return OWNERSHIP.UNKNOWN;
  const handoff = marker?.handoffProcesses?.find(item => item?.pid === numericPid);
  if (!handoff || !handoff.executablePath || !handoff.processStartTimeUtc) return OWNERSHIP.UNKNOWN;
  let identity;
  try { identity = identityReader(numericPid); } catch { return OWNERSHIP.UNKNOWN; }
  if (!identity?.executablePath || !identity.startTime) {
    return processAlive(numericPid, aliveReader) === false ? OWNERSHIP.EXITED : OWNERSHIP.UNKNOWN;
  }
  if (!sameExecutable(identity.executablePath, handoff.executablePath)) return OWNERSHIP.NOT_OWNED;
  return handoff.processStartTimeUtc === identity.startTime ? OWNERSHIP.OWNED : OWNERSHIP.NOT_OWNED;
}

export function ownsProcess(markerPath, pid) {
  return inspectProcessOwnership(markerPath, pid) === OWNERSHIP.OWNED;
}

export function ownsRestartedProcess(markerPath, pid) {
  return inspectRestartedProcessOwnership(markerPath, pid) === OWNERSHIP.OWNED;
}

export function ownsHandoffProcess(markerPath, pid) {
  return inspectHandoffProcessOwnership(markerPath, pid) === OWNERSHIP.OWNED;
}

function runtimeOwnershipStatus(markerPath, marker, pid, readers) {
  if (Number(pid) === marker.pid) return inspectProcessOwnership(markerPath, pid, readers);
  if (marker.handoffProcesses?.some(item => item?.pid === Number(pid))) return inspectHandoffProcessOwnership(markerPath, pid, readers);
  return inspectRestartedProcessOwnership(markerPath, pid, readers);
}

export async function ensureOwnedRuntimeDirectory(runtimeDir, markerPath, pidFilePath, { identityReader = readProcessIdentity, aliveReader = isProcessAliveSafe, terminator = killProcessTree, exitWaiter = waitForExit } = {}) {
  if (!fs.existsSync(runtimeDir)) {
    fs.mkdirSync(runtimeDir, { recursive: true });
    return;
  }
  let marker = readRunMarker(markerPath);
  if (!marker) {
    throw new Error(`拒绝清理缺少本次运行 marker 的目录：${runtimeDir}`);
  }
  const handoffPids = Array.isArray(marker.handoffProcesses)
    ? marker.handoffProcesses.map(item => item?.pid)
    : [];
  const pids = [...new Set([marker.pid, readPidFile(pidFilePath), ...handoffPids]
    .filter(pid => Number.isInteger(pid) && pid > 0))];
  for (const pid of pids) registerHandoffProcess(markerPath, pid, { identityReader });
  marker = readRunMarker(markerPath);
  for (const pid of pids) {
    if (!aliveReader(pid)) continue;
    const status = runtimeOwnershipStatus(markerPath, marker, pid, { identityReader, aliveReader });
    if (status === OWNERSHIP.EXITED) continue;
    if (status !== OWNERSHIP.OWNED) {
      throw new Error(`拒绝清理运行进程：PID=${pid}，ownership=${status}`);
    }
    if (!terminator(pid)) {
      throw new Error(`运行进程树终止未确认：PID=${pid}`);
    }
  }
  for (const pid of pids) {
    if (!await exitWaiter(pid, 10000, 250)) {
      throw new Error(`运行进程退出未确认，保留现场：PID=${pid}`);
    }
  }
  fs.rmSync(runtimeDir, { recursive: true, force: true, maxRetries: 120, retryDelay: 250 });
  fs.mkdirSync(runtimeDir, { recursive: true });
}

/** 安装 Generic ADB 与 MuMu 桩（fixture 位于 tests/e2e/tests/fixtures，两层共用）。 */
export function installEmulatorStubs(runtimeDir, fixtureDir) {
  const foreground = "  mCurrentFocus=Window{test u0 app.lawnchair/app.lawnchair.LawnchairLauncher}";
  const adbDir = path.join(runtimeDir, "adb-stub");
  fs.mkdirSync(adbDir, { recursive: true });
  fs.copyFileSync(path.join(fixtureDir, "adb-stub.cmd"), path.join(adbDir, "adb-stub.cmd"));
  fs.writeFileSync(path.join(adbDir, "foreground.txt"), foreground, "utf8");
  const mumuDir = path.join(runtimeDir, "mumu-stub");
  fs.mkdirSync(mumuDir, { recursive: true });
  fs.copyFileSync(path.join(fixtureDir, "mumu-manager-stub.cmd"), path.join(mumuDir, "mumu-manager-stub.cmd"));
  fs.writeFileSync(path.join(mumuDir, "foreground.txt"), foreground, "utf8");

}

/**
 * 受控停止本层拉起的服务进程：Test Host 模式先写退出文件，
 * 再走 stdin EOF 退出，最后按 service.pid 与子进程 PID 做隔离进程树清理。
 */
export async function stopSpawnedService({ child, exitFile, pidFilePath, markerPath, exitWaitPollMs = 250, identityReader = readProcessIdentity, aliveReader = isProcessAliveSafe, terminator = killProcessTree, exitWaiter = waitForExit }) {
  await waitForRunMarker(child);
  // 在发出退出信号前固定当前 service.pid；服务优雅退出时可能先删除 PID 文件，
  // 仅在等待 child 后重新读取会漏掉仍在收尾的更新重拉服务。
  const initialMarked = readPidFile(pidFilePath);
  const hasKnownProcess = Number.isInteger(initialMarked) || Number.isInteger(child?.pid);
  let marker = markerPath ? readRunMarker(markerPath) : null;
  const candidatePids = [...new Set([initialMarked, child?.pid].filter(pid => Number.isInteger(pid) && pid > 0))];
  if (candidatePids.some(pid => aliveReader(pid)) && (!markerPath || !marker)) {
    throw new Error(`拒绝清理缺少有效运行 marker 的进程：markerPath=${markerPath || "missing"}`);
  }
  for (const pid of candidatePids) {
    if (!aliveReader(pid)) continue;
    registerHandoffProcess(markerPath, pid, { identityReader });
    const inspector = pid === marker.pid ? inspectProcessOwnership : inspectHandoffProcessOwnership;
    const status = inspector(markerPath, pid, { identityReader, aliveReader });
    if (status !== OWNERSHIP.OWNED && status !== OWNERSHIP.EXITED) throw new Error(`拒绝发送退出信号：PID=${pid}，ownership=${status}`);
  }
  if (hasKnownProcess && process.env.NEXUS_TEST_MODE?.trim().toLowerCase() === "test-host") {
    fs.mkdirSync(path.dirname(exitFile), { recursive: true });
    fs.writeFileSync(exitFile, "stop\n", "utf8");
  }
  if (child?.stdin && !child.stdin.destroyed) {
    try {
      child.stdin.end();
      await exitWaiter(child.pid, 5000, exitWaitPollMs);
    } catch {
      // 受控退出失败时继续使用隔离 PID 清理。
    }
  }
  const pids = new Set();
  if (initialMarked) pids.add(initialMarked);
  const marked = readPidFile(pidFilePath);
  if (marked) pids.add(marked);
  if (child?.pid) pids.add(Number(child.pid));
  const owned = [...pids].filter(pid => Number.isInteger(pid) && pid > 0);
  for (const pid of owned) {
    if (!aliveReader(pid)) continue;
    const inspector = pid === marker.pid ? inspectProcessOwnership : inspectHandoffProcessOwnership;
    const status = inspector(markerPath, pid, { identityReader, aliveReader });
    if (status === OWNERSHIP.EXITED) continue;
    if (status !== OWNERSHIP.OWNED) {
      throw new Error(`拒绝清理未通过运行 marker 身份核验的进程：PID=${pid}，ownership=${status}`);
    }
    if (!terminator(pid)) {
      throw new Error(`受控停止未确认进程树已终止：PID=${pid}`);
    }
  }
  for (const pid of owned) {
    if (!await exitWaiter(pid, 10000, 250)) {
      throw new Error(`受控停止未确认进程已退出：PID=${pid}`);
    }
  }
  // 进程被强制终止时可能来不及自行删除 service.pid。只有本次已确认退出的
  // PID 仍写在文件中，才清理这个过期指针；新进程改写的 PID 必须保留。
  const finalMarked = readPidFile(pidFilePath);
  if (finalMarked && owned.includes(finalMarked)) fs.rmSync(pidFilePath, { force: true });
  // 保留 marker 作为下一次 prepareRuntime 的 ownership 证据；下一次会先核验所有
  // 记录的 PID 已退出，再清理整个隔离目录。无 marker 的目录永远不因本流程被删除。
}

function isProcessAliveSafe(pid) {
  return isProcessAlive(Number(pid));
}
