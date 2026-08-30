import fs from "node:fs";
import path from "node:path";
import {
  killProcessTree,
  readPidFile,
  waitForExit,
} from "./windows-process.mjs";

/**
 * System Smoke 与 e2e UI Smoke 共享的测试运行时辅助：
 * 模式解析、Test Host 路径、release 产物复制、stub 安装、受控停止。
 * 两层的差异化逻辑（Playwright 服务编排 / web.port 探测、诊断输出）留在各自的 helper。
 */

export function requireExecutionMode(layerLabel) {
  const mode = process.env.NEXUS_TEST_MODE?.trim().toLowerCase();
  if (mode !== "codex" && mode !== "admin") {
    throw new Error(`${layerLabel} 必须通过 tests\\run.mjs 显式选择模式：codex 或 admin。`);
  }
  return mode;
}

export function resolveTestHostDir(projectRoot) {
  const configured = process.env.NEXUS_TEST_HOST_DIR?.trim();
  return configured
    ? (path.isAbsolute(configured) ? configured : path.resolve(projectRoot, configured))
    : path.join(projectRoot, "tests", ".artifacts", "test-host");
}

export function resolveTestHostExitFile(projectRoot, fallbackPath) {
  const configured = process.env.NEXUS_TEST_HOST_EXIT_FILE?.trim();
  return configured
    ? (path.isAbsolute(configured) ? configured : path.resolve(projectRoot, configured))
    : fallbackPath;
}

export const sleep = ms => new Promise(resolve => setTimeout(resolve, ms));

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

/** 把 release（或 Test Host）产物的 exe / wwwroot / plugins 复制进隔离 runtime 目录。 */
export function copyReleaseArtifacts(releaseDir, runtimeDir) {
  const sourceExe = path.join(releaseDir, "nexus-pipeline.exe");
  if (!fs.existsSync(sourceExe)) {
    throw new Error(`${releaseDir}/nexus-pipeline.exe 不存在（runtime 缺构建产物）。`);
  }
  fs.copyFileSync(sourceExe, path.join(runtimeDir, "nexus-pipeline.exe"));
  fs.cpSync(path.join(releaseDir, "wwwroot"), path.join(runtimeDir, "wwwroot"), { recursive: true });
  const plugins = path.join(releaseDir, "plugins");
  if (fs.existsSync(plugins)) fs.cpSync(plugins, path.join(runtimeDir, "plugins"), { recursive: true });
}

/** 安装 ADB / MuMu 桩（fixture 位于 tests/e2e/tests/fixtures，两层共用）。 */
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
 * 受控停止本层拉起的服务进程：codex 模式先写 Test Host 退出文件，
 * 再走 stdin EOF 退出，最后按 service.pid 与子进程 PID 做隔离进程树清理。
 */
export async function stopSpawnedService({ child, exitFile, pidFilePath, exitWaitPollMs = 250 }) {
  if (process.env.NEXUS_TEST_MODE?.trim().toLowerCase() === "codex") {
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
  const marked = readPidFile(pidFilePath);
  if (marked) pids.add(marked);
  if (child?.pid) pids.add(Number(child.pid));
  const owned = [...pids].filter(pid => Number.isInteger(pid) && pid > 0);
  for (const pid of owned) killProcessTree(pid);
  for (const pid of owned) await waitForExit(pid, 10000, 250);
}
