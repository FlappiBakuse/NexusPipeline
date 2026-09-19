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

export function createRunMarker(markerPath, executablePath, pid) {
  const identity = readProcessIdentity(pid);
  const marker = {
    schemaVersion: 1,
    nonce: randomUUID(),
    executablePath: path.normalize(path.resolve(executablePath)),
    pid: Number(pid),
    startedAtUtc: new Date().toISOString(),
    processStartTimeUtc: identity?.startTime || "",
  };
  if (!Number.isInteger(marker.pid) || marker.pid <= 0) throw new Error(`非法运行 PID：${pid}`);
  fs.mkdirSync(path.dirname(markerPath), { recursive: true });
  fs.writeFileSync(markerPath, `${JSON.stringify(marker, null, 2)}\n`, "utf8");
  return marker;
}

export function readRunMarker(markerPath) {
  try {
    const marker = JSON.parse(fs.readFileSync(markerPath, "utf8"));
    if (marker?.schemaVersion !== 1 || !marker.nonce || !Number.isInteger(marker.pid)) return null;
    return marker;
  } catch {
    return null;
  }
}

export function ownsProcess(markerPath, pid) {
  const marker = readRunMarker(markerPath);
  if (!marker || marker.pid !== Number(pid)) return false;
  const identity = readProcessIdentity(pid);
  if (!identity || !identity.executablePath) return false;
  if (path.normalize(path.resolve(identity.executablePath)).toLowerCase()
      !== path.normalize(path.resolve(marker.executablePath)).toLowerCase()) {
    return false;
  }
  return !marker.processStartTimeUtc
    || !identity.startTime
    || marker.processStartTimeUtc === identity.startTime;
}

/** 重启会把同一 runtime executable 作为旧宿主的子进程拉起；用父 PID + 完整路径核对交接身份。 */
export function ownsRestartedProcess(markerPath, pid) {
  const marker = readRunMarker(markerPath);
  if (!marker || marker.pid === Number(pid)) return false;
  const identity = readProcessIdentity(pid);
  if (!identity || identity.parentPid !== marker.pid || !identity.executablePath) return false;
  return path.normalize(path.resolve(identity.executablePath)).toLowerCase()
    === path.normalize(path.resolve(marker.executablePath)).toLowerCase();
}

/** 重启交接确认后记录的新 PID：完整路径和 OS 启动时间均来自本次 status 已核验实例。 */
export function ownsHandoffProcess(markerPath, pid) {
  const marker = readRunMarker(markerPath);
  const handoff = marker?.handoffProcesses?.find(item => item?.pid === Number(pid));
  if (!handoff || !handoff.executablePath) return false;
  const identity = readProcessIdentity(pid);
  if (!identity?.executablePath
      || path.normalize(path.resolve(identity.executablePath)).toLowerCase()
        !== path.normalize(path.resolve(handoff.executablePath)).toLowerCase()) {
    return false;
  }
  return !handoff.processStartTimeUtc
    || !identity.startTime
    || handoff.processStartTimeUtc === identity.startTime;
}

export async function ensureOwnedRuntimeDirectory(runtimeDir, markerPath, pidFilePath) {
  if (!fs.existsSync(runtimeDir)) {
    fs.mkdirSync(runtimeDir, { recursive: true });
    return;
  }
  const marker = readRunMarker(markerPath);
  if (!marker) {
    throw new Error(`拒绝清理缺少本次运行 marker 的目录：${runtimeDir}`);
  }
  const handoffPids = Array.isArray(marker.handoffProcesses)
    ? marker.handoffProcesses.map(item => item?.pid)
    : [];
  const pids = [...new Set([marker.pid, readPidFile(pidFilePath), ...handoffPids]
    .filter(pid => Number.isInteger(pid) && pid > 0))];
  for (const pid of pids) {
    if (!isProcessAliveSafe(pid)) continue;
    if (!ownsProcess(markerPath, pid)
        && !ownsRestartedProcess(markerPath, pid)
        && !ownsHandoffProcess(markerPath, pid)) {
      throw new Error(`拒绝清理 marker 未匹配的运行进程：PID=${pid}`);
    }
    killProcessTree(pid);
  }
  for (const pid of pids) await waitForExit(pid, 10000, 250);
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
export async function stopSpawnedService({ child, exitFile, pidFilePath, markerPath, exitWaitPollMs = 250 }) {
  // 在发出退出信号前固定当前 service.pid；服务优雅退出时可能先删除 PID 文件，
  // 仅在等待 child 后重新读取会漏掉仍在收尾的更新重拉服务。
  const initialMarked = readPidFile(pidFilePath);
  const hasKnownProcess = Number.isInteger(initialMarked) || Number.isInteger(child?.pid);
  if (hasKnownProcess && process.env.NEXUS_TEST_MODE?.trim().toLowerCase() === "test-host") {
    fs.mkdirSync(path.dirname(exitFile), { recursive: true });
    fs.writeFileSync(exitFile, "stop\n", "utf8");
  }
  if (child?.stdin && !child.stdin.destroyed) {
    try {
      child.stdin.end();
      await waitForExit(child.pid, 5000, exitWaitPollMs);
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
    if (!isProcessAliveSafe(pid)) continue;
    if (!markerPath
        || ownsProcess(markerPath, pid)
        || ownsRestartedProcess(markerPath, pid)
        || ownsHandoffProcess(markerPath, pid)) {
      killProcessTree(pid);
    } else {
      // 受控退出信号可能正好在身份读取之间让进程退出；再次确认后可安全忽略已退出 PID。
      if (!isProcessAliveSafe(pid)) continue;
      const marker = readRunMarker(markerPath);
      const identity = readProcessIdentity(pid);
      throw new Error([
        `拒绝清理未通过运行 marker 身份核验的进程：PID=${pid}`,
        `markerPath=${markerPath}`,
        `initialMarked=${initialMarked ?? "unknown"}`,
        `marked=${marked ?? "unknown"}`,
        `childPid=${child?.pid ?? "unknown"}`,
        `marker=${marker ? JSON.stringify(marker) : "missing"}`,
        `identity=${identity ? JSON.stringify(identity) : "unavailable"}`,
      ].join("；"));
    }
  }
  for (const pid of owned) await waitForExit(pid, 10000, 250);
  // 保留 marker 作为下一次 prepareRuntime 的 ownership 证据；下一次会先核验所有
  // 记录的 PID 已退出，再清理整个隔离目录。无 marker 的目录永远不因本流程被删除。
}

function isProcessAliveSafe(pid) {
  return isProcessAlive(Number(pid));
}
