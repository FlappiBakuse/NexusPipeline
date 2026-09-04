import test, { after, before } from "node:test";
import assert from "node:assert/strict";
import { spawn } from "node:child_process";
import fs from "node:fs";
import path from "node:path";
import {
  api,
  isAdminMode,
  isAdministrator,
  prepareRuntime,
  projectRoot,
  releaseDir,
  runtimeDir,
  runtimeExe,
  sleep,
  startRuntime,
  systemWebPort,
  stopRuntime,
  waitFor,
  waitForService,
} from "./runtime-helper.mjs";
import { deriveCandidateVersion, readProjectVersion } from "../support/project-version.mjs";

/**
 * 内建更新 System Smoke（下一候选版本）：在隔离安装副本上验证 apply-update 的
 * 「备份 → 交换 → 重拉」、config/history/用户插件目录原样保留、启动收尾清理、失败回滚与 defer 自动应用。
 * 运行方式：通过 `node tests\\run.mjs codex system` 或 `node tests\\run.mjs admin system` 执行。
 */
const enabled = process.env.NEXUS_SYSTEM_SMOKE === "1";
const skipReason = process.env.NEXUS_SYSTEM_SMOKE !== "1"
  ? "设置 NEXUS_SYSTEM_SMOKE=1 后运行"
  : "";
const skip = enabled ? false : skipReason;

const updateDir = path.join(runtimeDir, ".nxp-update");
const backupDir = path.join(runtimeDir, ".nxp-backup", "previous");
const versionFile = path.join(runtimeDir, ".nxp-version");
const taskFile = path.join(updateDir, "task.json");
const updateVersion = deriveCandidateVersion(readProjectVersion(projectRoot));
const stagingRoot = path.join(updateDir, "staging", updateVersion);

function writeTask(mode, stagedDir = stagingRoot) {
  fs.mkdirSync(updateDir, { recursive: true });
  fs.writeFileSync(taskFile, JSON.stringify({ Mode: mode, Version: updateVersion, StagedDir: stagedDir }), "utf8");
}

/** 从发布构建构造「新版本」staging：exe + wwwroot（release 干净源，无旧安装标记）+ plugins（验证更新器不接管该目录）。 */
function prepareStaging() {
  fs.rmSync(stagingRoot, { recursive: true, force: true });
  fs.mkdirSync(stagingRoot, { recursive: true });
  fs.copyFileSync(runtimeExe, path.join(stagingRoot, "nexus-pipeline.exe"));
  fs.cpSync(path.join(releaseDir, "wwwroot"), path.join(stagingRoot, "wwwroot"), { recursive: true });
  fs.writeFileSync(path.join(stagingRoot, "wwwroot", "candidate-install-marker.txt"), "candidate-install-marker", "utf8");
  if (fs.existsSync(path.join(runtimeDir, "plugins"))) {
    fs.cpSync(path.join(runtimeDir, "plugins"), path.join(stagingRoot, "plugins"), { recursive: true });
  }
}

/** 预先制造旧安装现场：config 与 history 内容、旧 wwwroot 标记、用户自加插件目录。 */
function prepareLegacyInstall() {
  fs.mkdirSync(path.join(runtimeDir, "config"), { recursive: true });
  fs.writeFileSync(
    path.join(runtimeDir, "config", "settings.json"),
    JSON.stringify({ WebPort: systemWebPort }),
    "utf8",
  );
  fs.mkdirSync(path.join(runtimeDir, "history", "2099-01-01"), { recursive: true });
  fs.writeFileSync(path.join(runtimeDir, "history", "2099-01-01", "00-00-00.json"), "{\"FinalStatus\":\"success\"}", "utf8");
  fs.writeFileSync(path.join(runtimeDir, "wwwroot", "legacy-install-marker.txt"), "legacy-install-marker", "utf8");
  fs.mkdirSync(path.join(runtimeDir, "plugins", "user-custom"), { recursive: true });
  fs.writeFileSync(path.join(runtimeDir, "plugins", "user-custom", "note.txt"), "keep-me", "utf8");
}

async function runApplyWorker(stagedDir) {
  // worker 不能直接使用安装目录中的目标 exe，否则 worker 自身会锁住待替换镜像。
  const workerExe = path.join(runtimeDir, `.nxp-update-worker-test-${Date.now()}-${Math.random().toString(36).slice(2)}.exe`);
  fs.copyFileSync(runtimeExe, workerExe);
  try {
    const result = await new Promise(resolve => {
      const worker = spawn(workerExe, ["apply-update", "--staged", stagedDir], {
        cwd: runtimeDir,
        stdio: "ignore",
        windowsHide: true,
      });
      worker.once("error", error => resolve({ status: null, error }));
      worker.once("exit", (status, signal) => resolve({ status, signal }));
    });
    return {
      status: Number.isInteger(result.status) ? result.status : null,
      signal: result.signal || null,
      error: result.error,
      stderr: "",
    };
  } finally {
    fs.rmSync(workerExe, { force: true, maxRetries: 20, retryDelay: 250 });
  }
}

function logTail() {
  const logDir = path.join(runtimeDir, "logs");
  if (!fs.existsSync(logDir)) return "";
  const files = fs.readdirSync(logDir).filter(name => name.endsWith(".log"));
  if (files.length === 0) return "";
  const latest = files.sort().at(-1);
  return fs.readFileSync(path.join(logDir, latest), "utf8");
}

function assertMarkersCleaned() {
  assert.equal(fs.existsSync(versionFile), false, ".nxp-version 应已清理");
  assert.equal(fs.existsSync(taskFile), false, "task.json 应已清理");
  assert.equal(fs.existsSync(updateDir), false, ".nxp-update 应已清理");
  assert.equal(fs.existsSync(backupDir), false, ".nxp-backup 应已清理");
}

/** 兜底清理：更新重拉的服务由新 runtime 自身 PID 标记接管。 */
async function stopRuntimeHard() {
  await stopRuntime();
  await sleep(500);
  await stopRuntime();
}

before(async () => {
  if (!enabled) return;
  if (isAdminMode) {
    assert.ok(isAdministrator(), "管理员 System Smoke 必须在 Administrator / High Integrity 终端运行");
  }
  // 先兜底清理上一轮残留的 runtime 进程（apply 重拉的服务进程若未清完会锁死 runtime 目录，导致 rmSync EPERM）。
  await stopRuntimeHard();
  prepareRuntime();
  prepareLegacyInstall();
});

after(async () => {
  if (enabled) {
    await stopRuntimeHard();
  }
});

test("apply-update：备份→交换→保留插件与数据→重拉宿主→启动收尾清理", { skip }, async () => {
  prepareStaging();
  writeTask("apply");

  const result = await runApplyWorker(stagingRoot);

  const resultSummary = JSON.stringify({
    status: result.status,
    signal: result.signal,
    error: result.error?.message,
    errno: result.error?.code,
  });
  assert.equal(result.status, 0, `apply-update 退出码非 0：${resultSummary}`);
  // 交换完成：新 wwwroot 标记到位、旧标记消失、用户自加插件保留、数据目录原样。
  // （versionFile/backup 是交换后到收尾前的中间态，重拉宿主启动即清理，不做时序性断言）
  assert.equal(fs.readFileSync(path.join(runtimeDir, "wwwroot", "candidate-install-marker.txt"), "utf8"), "candidate-install-marker");
  assert.equal(fs.existsSync(path.join(runtimeDir, "wwwroot", "legacy-install-marker.txt")), false);
  assert.equal(fs.existsSync(path.join(runtimeDir, "plugins", "user-custom", "note.txt")), true);
  assert.equal(
    fs.readFileSync(path.join(runtimeDir, "config", "settings.json"), "utf8").includes(String(systemWebPort)),
    true,
  );
  assert.equal(fs.existsSync(path.join(runtimeDir, "history", "2099-01-01", "00-00-00.json")), true);

  // 新实例启动：收尾清理 + 服务可达。
  startRuntime(["web"]);
  await waitForService(null, 60000);
  await waitFor(() => !fs.existsSync(versionFile), 30000);
  assertMarkersCleaned();
  const status = await (await api("GET", "/api/status")).json();
  assert.match(status.version, /^\d+\.\d+\.\d+$/);
  const audit = logTail();
  assert.match(audit, /更新完成/, "日志应包含「更新完成」审计");
  await stopRuntimeHard();
});

test("启动收尾：apply 标记无成功标记时从备份回滚", { skip }, async () => {
  // 制造「切换未完成」现场：备份中有旧 wwwroot，安装目录为半成品新 wwwroot。
  fs.rmSync(backupDir, { recursive: true, force: true });
  fs.mkdirSync(path.join(backupDir, "wwwroot"), { recursive: true });
  fs.writeFileSync(path.join(backupDir, "wwwroot", "legacy-install-marker.txt"), "legacy-install-marker", "utf8");
  fs.writeFileSync(path.join(runtimeDir, "wwwroot", "partial-marker.txt"), "partial", "utf8");
  prepareStaging();
  writeTask("apply");

  startRuntime(["web"]);
  await waitForService(null, 60000);

  // 回滚后：旧 wwwroot 还原、staging/task 清理、服务正常运行。
  assert.equal(fs.readFileSync(path.join(runtimeDir, "wwwroot", "legacy-install-marker.txt"), "utf8"), "legacy-install-marker");
  assert.equal(fs.existsSync(path.join(runtimeDir, "wwwroot", "partial-marker.txt")), false);
  assertMarkersCleaned();
  await stopRuntimeHard();
  fs.rmSync(path.join(runtimeDir, "wwwroot", "legacy-install-marker.txt"), { force: true });
});

test("defer 标记：下次启动自动应用并重拉服务", { skip }, async () => {
  prepareStaging();
  writeTask("defer");
  fs.writeFileSync(path.join(runtimeDir, "wwwroot", "legacy-install-marker.txt"), "legacy-install-marker", "utf8");

  // 启动（web 模式）：收尾检测到 defer → 转 apply → 拉起子进程 → 本进程退出 → 子进程交换 → 重拉服务。
  startRuntime(["web"]);
  await waitForService(null, 90000);

  assert.equal(fs.readFileSync(path.join(runtimeDir, "wwwroot", "candidate-install-marker.txt"), "utf8"), "candidate-install-marker");
  await waitFor(() => !fs.existsSync(versionFile), 30000);
  assertMarkersCleaned();
  const audit = logTail();
  assert.match(audit, /更新完成/, "日志应包含「更新完成」审计");
  await stopRuntimeHard();
});
