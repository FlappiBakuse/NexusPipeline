import { spawn } from "node:child_process";
import fs from "node:fs";
import path from "node:path";
import os from "node:os";
import { fileURLToPath } from "node:url";
import { FRONTEND_TEST_GROUPS, GOVERNANCE_DOMAINS, HOST_TEST_AREAS, SYSTEM_TEST_GROUPS, logicalGroupId } from "../tools/ci-domains.mjs";
import { globToRegExp } from "../tools/ci-changes.mjs";
import { createBuildFingerprint } from "../tools/ci-fingerprint.mjs";
import { expectedArtifactDigest, validateExecutionPlan } from "../tools/ci-summary.mjs";
import { parseTapResults, parseTrxResults, parseVitestResults, parsePlaywrightResults } from "../tools/test-results.mjs";
import { getIntegrityLevel, isAdministrator, killProcessTree } from "./support/windows-process.mjs";

const projectRoot = path.resolve(path.dirname(fileURLToPath(import.meta.url)), "..");
const frontendDir = path.join(projectRoot, "frontend");
const e2eDir = path.join(projectRoot, "tests", "e2e");
const systemDir = path.join(projectRoot, "tests", "system");
const testHostDir = path.join(projectRoot, "tests", ".artifacts", "test-host");
const emulatorFixturePluginDir = path.join(projectRoot, "tests", ".artifacts", "emulator-test-plugin");
const nodeCommand = process.execPath;
const playwrightCli = path.join(e2eDir, "node_modules", "playwright", "cli.js");
const npmCommand = process.platform === "win32" ? "npm.cmd" : "npm";
const TEST_HOST_ENV_KEYS = ["NEXUS_TEST_HOST", "NEXUS_TEST_HOST_DIR", "NEXUS_TEST_HOST_EXIT_FILE"];
const MODE_SUITES = new Set(["default", "ui", "system", "all"]);
const CI_MANIFEST_COMMANDS = new Set(["unit", "frontend", "contract", "docs", "tooling", "syntax", "build", "codex", "admin"]);
const ciExecution = {
  testCount: 0,
  passed: 0,
  failed: 0,
  skipped: 0,
  domainId: process.env.NEXUS_CI_DOMAIN || "",
  groups: [],
  checks: [],
};
let productionBuildPromise = null;
let testHostBuildPromise = null;
let retainTestHostForRun = false;
let nativeReportSequence = 0;

function ciManifestEnabled() {
  return Boolean(process.env.NEXUS_CI_MANIFEST);
}

function readCiPlan() {
  if (!process.env.NEXUS_CI_EXECUTION_PLAN) return null;
  try {
    const plan = JSON.parse(process.env.NEXUS_CI_EXECUTION_PLAN);
    return plan && typeof plan === "object" ? plan : null;
  } catch (error) {
    console.error(`[CI manifest] 执行计划解析失败：${error.message}`);
    return null;
  }
}

function ciJobId() {
  if (process.env.NEXUS_CI_JOB_ID) return process.env.NEXUS_CI_JOB_ID;
  return {
    frontend: "frontend-unit",
    host: "host-core",
    docs: "docs-i18n",
    plugin: "plugin-contract",
    ui: "ui-smoke",
    system_runtime: "system-runtime-mcp",
    system_execution: "system-execution",
    system_emulator: "system-emulator",
    system_update: "system-update",
    "full-regression": "full-regression",
  }[process.env.NEXUS_CI_DOMAIN || ""] || "runner";
}

function ciPlannedGroup(groupId) {
  return readCiPlan()?.selectedGroups?.find(group => group.groupId === groupId) || null;
}

function recordCiCheck(checkId, status, detail = "") {
  if (!ciManifestEnabled()) return;
  const normalized = String(status || "failure").toLowerCase();
  const existing = ciExecution.checks.find(check => check.checkId === checkId);
  if (existing) {
    if (existing.status !== "success") return;
    if (normalized !== "success") {
      existing.status = normalized;
      existing.detail = detail;
    }
    return;
  }
  ciExecution.checks.push({ checkId, status: normalized, ...(detail ? { detail } : {}) });
}

function upsertCiGroup(record) {
  if (!ciManifestEnabled() || !record.groupId) return;
  const existing = ciExecution.groups.find(group => group.groupId === record.groupId);
  if (!existing) {
    const reportPaths = [...new Set([...(record.reportPaths || []), ...(record.reportPath ? [record.reportPath] : [])])];
    ciExecution.groups.push({
      ...record,
      ...(reportPaths.length ? { reportPaths } : {}),
    });
    return;
  }
  existing.selectedTests = [...new Set([...(existing.selectedTests || []), ...(record.selectedTests || [])])];
  existing.selection = {
    plannedTests: existing.plannedTests,
    actualTests: existing.selectedTests,
  };
  for (const key of ["testCount", "passed", "failed", "skipped", "nativeTotal"]) {
    existing[key] = Number(existing[key] || 0) + Number(record[key] || 0);
  }
  if (record.result !== "success") existing.result = "failure";
  if (record.exitCode !== 0) existing.exitCode = record.exitCode;
  if (record.error) existing.error = record.error;
  const reportPaths = [...new Set([...(record.reportPaths || []), ...(record.reportPath ? [record.reportPath] : [])])];
  if (reportPaths.length) {
    existing.reportPaths = [...new Set([...(existing.reportPaths || []), ...reportPaths])];
    existing.reportPath ||= reportPaths[0];
  }
}

function recordCiTests(result, { groupIds = [], selectedTests = [], format = "", exitCode = 0, reportPath = "" } = {}) {
  for (const key of ["testCount", "passed", "failed", "skipped"]) ciExecution[key] += result[key];
  if (!ciManifestEnabled()) return;
  const plan = readCiPlan();
  for (const groupId of groupIds) {
    const planned = plan?.selectedGroups?.find(group => group.groupId === groupId) || {};
    const actualTests = Array.isArray(selectedTests) ? selectedTests : [];
    upsertCiGroup({
      groupId,
      physicalJobId: planned.physicalJobId || ciJobId(),
      mode: planned.mode || process.env.NEXUS_CI_MODE || "ci",
      integrityLevel: process.env.NEXUS_CI_INTEGRITY_LEVEL || getIntegrityLevel(),
      testSelectionIdentity: planned.testSelectionIdentity || "",
      plannedTests: planned.expectedTests || [],
      selectedTests: actualTests,
      selection: { plannedTests: planned.expectedTests || [], actualTests },
      result: exitCode === 0 && result.failed === 0 ? "success" : "failure",
      testCount: result.testCount,
      passed: result.passed,
      failed: result.failed,
      skipped: result.skipped,
      nativeTotal: result.testCount,
      exitCode: exitCode || (result.failed > 0 ? 1 : 0),
      exclusions: planned.exclusions || [],
      framework: format,
      ...(reportPath ? { reportPath } : {}),
    });
  }
}

function recordCiGroupFailure(groupIds, { selectedTests = [], format = "", error = "" } = {}) {
  if (!ciManifestEnabled()) return;
  const plan = readCiPlan();
  for (const groupId of groupIds) {
    const planned = plan?.selectedGroups?.find(group => group.groupId === groupId) || {};
    upsertCiGroup({
      groupId,
      physicalJobId: planned.physicalJobId || ciJobId(),
      mode: planned.mode || process.env.NEXUS_CI_MODE || "ci",
      integrityLevel: process.env.NEXUS_CI_INTEGRITY_LEVEL || getIntegrityLevel(),
      testSelectionIdentity: planned.testSelectionIdentity || "",
      plannedTests: planned.expectedTests || [],
      selectedTests,
      selection: { plannedTests: planned.expectedTests || [], actualTests: selectedTests },
      result: "failure",
      testCount: 0,
      passed: 0,
      failed: 0,
      skipped: 0,
      nativeTotal: 0,
      exitCode: 1,
      exclusions: planned.exclusions || [],
      framework: format,
      error,
    });
  }
}

function retainNativeReport({ reportFile, rawOutput, format, groupIds = [] }) {
  const root = process.env.NEXUS_CI_REPORT_DIR;
  if (!root) return "";
  try {
    fs.mkdirSync(root, { recursive: true });
    const identity = (groupIds.length > 0 ? groupIds.join("__") : "runner")
      .replace(/[^A-Za-z0-9_.-]+/gu, "_");
    const extension = format === "trx" ? "trx" : format === "vitest" || format === "playwright" ? "json" : format === "command" ? "log" : "tap";
    nativeReportSequence += 1;
    const destination = path.join(root, `${identity}__${String(nativeReportSequence).padStart(3, "0")}.${extension}`);
    if (format === "tap") {
      fs.writeFileSync(destination, String(rawOutput || ""), "utf8");
    } else if (fs.existsSync(reportFile)) {
      fs.copyFileSync(reportFile, destination);
    } else {
      fs.writeFileSync(destination, String(rawOutput || ""), "utf8");
    }
    return destination;
  } catch (error) {
    console.error(`[测试结果] 原始 ${format} 报告保留失败：${error.message}`);
    recordCiCheck("reports", "failure", error.message);
    return "";
  }
}

async function runReported(command, args, options = {}, format = "tap", context = {}) {
  const reportDir = fs.mkdtempSync(path.join(os.tmpdir(), "nxp-test-results-"));
  const reportFile = path.join(reportDir, format === "trx" ? "results.trx" : "results.json");
  let tail = "";
  const env = { ...(options.env || process.env) };
  const reportedArgs = [...args];
  if (format === "tap") reportedArgs.splice(1, 0, "--test-reporter=tap");
  if (format === "trx") reportedArgs.push("--logger", "trx;LogFileName=results.trx", "--results-directory", reportDir);
  if (format === "vitest") reportedArgs.push("--reporter=default", "--reporter=json", `--outputFile=${reportFile}`);
  if (format === "playwright") {
    reportedArgs.push("--reporter=line,json");
    env.PLAYWRIGHT_JSON_OUTPUT_NAME = reportFile;
  }
  try {
    const code = await runProcess(command, reportedArgs, { ...options, env, onOutput: chunk => { tail = (tail + chunk).slice(-262144); } });
    try {
      const result = format === "tap" ? parseTapResults(tail)
        : format === "trx" ? parseTrxResults(fs.readFileSync(reportFile, "utf8"))
        : format === "vitest" ? parseVitestResults(JSON.parse(fs.readFileSync(reportFile, "utf8")))
        : parsePlaywrightResults(JSON.parse(fs.readFileSync(reportFile, "utf8")));
      const reportPath = retainNativeReport({ reportFile, rawOutput: tail, format, groupIds: context.groupIds || [] });
      recordCiTests(result, { ...context, format, exitCode: code, reportPath });
      recordCiCheck(context.checkId || "tests", code === 0 && result.failed === 0 ? "success" : "failure");
      console.error(`[测试结果] passed=${result.passed} failed=${result.failed} skipped=${result.skipped}`);
      return code || (result.failed > 0 ? 1 : 0);
    } catch (error) {
      recordCiCheck(context.checkId || "tests", "failure", error.message);
      recordCiGroupFailure(context.groupIds || [], { ...context, format, error: error.message });
      console.error(`[测试结果] ${error.message}`);
      return code || 1;
    }
  } finally {
    fs.rmSync(reportDir, { recursive: true, force: true });
  }
}
// System Smoke 影响域分组：CI 按改动范围选择分组，每个分组复用 ci-domains.mjs 的唯一 suite 定义。
const SYSTEM_SUITE_GROUPS = SYSTEM_TEST_GROUPS.map(group => [
  group.key,
  group.suitePaths.map((suitePath, index) => [
    group.runtimeNames?.[index] || `runtime-${group.key}-${index + 1}`,
    path.basename(suitePath),
  ]),
]);
const SYSTEM_GROUP_NAMES = SYSTEM_SUITE_GROUPS.map(([group]) => group);

function runProcess(command, args, options = {}) {
  return new Promise(resolve => {
    const label = [command, ...args].join(" ");
    const timeoutMs = Number.isFinite(options.timeoutMs) && options.timeoutMs > 0
      ? options.timeoutMs
      : null;
    const timeoutCode = options.timeoutCode ?? 124;
    let timeoutHandle = null;
    let timeoutCleanupHandle = null;
    let settled = false;
    let timedOut = false;
    const finish = code => {
      if (settled) return;
      settled = true;
      if (timeoutHandle) clearTimeout(timeoutHandle);
      if (timeoutCleanupHandle) clearTimeout(timeoutCleanupHandle);
      resolve(code);
    };
    // Windows Node 24 rejects direct spawn of .cmd/.bat files with EINVAL.
    // Route command shims through cmd.exe so the unified runner works both
    // from PowerShell and from the CI shells without enabling an unbounded
    // shell for every process.
    const isWindowsCommandShim = process.platform === "win32" && /\.(?:cmd|bat)$/i.test(command);
    const spawnCommand = isWindowsCommandShim ? (process.env.ComSpec || "cmd.exe") : command;
    const spawnArgs = isWindowsCommandShim
      ? ["/d", "/s", "/c", "call", command, ...args]
      : args;
    const childEnv = { ...(options.env || process.env) };
    // CI manifest/report paths belong to this runner. Test engines and build tools
    // may start probes of their own and must not inherit the parent's output owner.
    delete childEnv.NEXUS_CI_MANIFEST;
    delete childEnv.NEXUS_CI_REPORT_DIR;
    const child = spawn(spawnCommand, spawnArgs, {
      cwd: options.cwd || projectRoot,
      env: childEnv,
      stdio: options.onOutput ? ["inherit", "pipe", "pipe"] : options.stdio || "inherit",
      windowsHide: true,
    });
    if (options.onOutput) {
      child.stdout.setEncoding("utf8");
      child.stderr.setEncoding("utf8");
      child.stdout.on("data", chunk => { process.stdout.write(chunk); options.onOutput(chunk); });
      child.stderr.on("data", chunk => { process.stderr.write(chunk); });
    }
    child.once("error", error => {
      console.error(`[错误] 启动 ${command} 失败：${error.message}`);
      finish(timedOut ? timeoutCode : 1);
    });
    child.once("close", (code, signal) => {
      if (timedOut) {
        finish(timeoutCode);
        return;
      }
      if (signal) {
        console.error(`[错误] ${command} 被信号 ${signal} 终止`);
        finish(1);
      } else {
        finish(code ?? 1);
      }
    });
    if (timeoutMs !== null && !settled) {
      timeoutHandle = setTimeout(() => {
        if (settled) return;
        timedOut = true;
        console.error(`[错误] ${label} 超时（${timeoutMs}ms），正在终止进程树`);
        try {
          if (process.platform === "win32" && child.pid) {
            killProcessTree(child.pid);
          } else {
            child.kill("SIGTERM");
          }
        } catch (error) {
          console.error(`[错误] 终止超时进程失败：${error.message}`);
        }
        // 等待子进程实际退出，再让下一个 suite 开始，避免残留宿主占用端口/运行目录。
        timeoutCleanupHandle = setTimeout(() => {
          console.error(`[错误] ${label} 收尾等待超时，无法确认进程树已退出。`);
          finish(timeoutCode);
        }, options.timeoutCleanupMs ?? 15_000);
      }, timeoutMs);
    }
  });
}

async function runCheckedProcess(checkId, command, args, options = {}) {
  const code = await runProcess(command, args, options);
  recordCiCheck(checkId, code === 0 ? "success" : "failure", code === 0 ? "" : `exit=${code}`);
  return code;
}

function runCmdFile(filePath, args = [], options = {}) {
  const cwd = options.cwd || projectRoot;
  const relativePath = path.relative(cwd, filePath) || path.basename(filePath);
  const commandLine = ["call", relativePath, ...args].join(" ");
  return runProcess(process.env.ComSpec || "cmd.exe", ["/d", "/s", "/c", commandLine], { ...options, cwd });
}

function syntaxTestFiles() {
  const directory = path.join(projectRoot, "tests", "e2e", "tests");
  return fs.readdirSync(directory)
    .filter(name => name.endsWith(".smoke.spec.mjs"))
    .sort()
    .map(name => path.join(directory, name));
}

function recursiveFiles(directory) {
  if (!fs.existsSync(directory)) return [];
  const files = [];
  for (const entry of fs.readdirSync(directory, { withFileTypes: true })) {
    if (["node_modules", "dist", "bin", "obj"].includes(entry.name)) continue;
    const fullPath = path.join(directory, entry.name);
    if (entry.isDirectory()) files.push(...recursiveFiles(fullPath));
    else files.push(fullPath);
  }
  return files;
}

function frontendTestFiles(groups = []) {
  const patterns = groups.length > 0
    ? FRONTEND_TEST_GROUPS
      .filter(group => groups.includes(group.key))
      .flatMap(group => group.testPaths)
    : ["frontend/src/**/*.test.ts"];
  return recursiveFiles(frontendDir)
    .filter(file => file.endsWith(".test.ts"))
    .filter(file => {
      const relative = path.relative(projectRoot, file).replaceAll("\\", "/");
      return patterns.some(pattern => globToRegExp(pattern).test(relative));
    })
    .sort();
}

function unitTestFiles(groups = []) {
  const unitDir = path.join(projectRoot, "tests", "NexusPipeline.Tests");
  const candidates = recursiveFiles(unitDir).filter(file => file.endsWith("Tests.cs"));
  if (groups.length === 0) return candidates.sort();
  const patterns = HOST_TEST_AREAS
    .filter(area => groups.includes(area.key))
    .flatMap(area => area.testPaths ?? [])
    .map(pattern => globToRegExp(`tests/NexusPipeline.Tests/${pattern}`));
  return candidates
    .filter(file => {
      const relative = path.relative(projectRoot, file).replaceAll("\\", "/");
      return patterns.some(pattern => pattern.test(relative));
    })
    .sort();
}

function ciPlannedKeys(kind, explicitGroups = []) {
  if (explicitGroups.length > 0) return [...new Set(explicitGroups)];
  const jobId = ciJobId();
  const plan = readCiPlan();
  if (!plan || !Array.isArray(plan.selectedGroups)) return [];
  return plan.selectedGroups
    .filter(group => group.physicalJobId === jobId && group.kind === kind)
    .map(group => group.key);
}

function requireAdmin(label, command) {
  if (isAdministrator()) return true;
  console.error(`[错误] ${label}需要 Administrator / High Integrity。当前终端权限不足，正式门禁未执行。请在管理员终端执行：node tests\\run.mjs ${command}`);
  return false;
}

function modeEnvironment(mode, { system = false, exitFile = null } = {}) {
  const env = { ...process.env };
  for (const key of TEST_HOST_ENV_KEYS) delete env[key];
  delete env.NEXUS_SYSTEM_SMOKE;
  env.NEXUS_TEST_MODE = mode;
  if (mode === "codex") {
    env.NEXUS_TEST_HOST = "1";
    env.NEXUS_TEST_HOST_DIR = testHostDir;
    if (exitFile) env.NEXUS_TEST_HOST_EXIT_FILE = exitFile;
  }
  if (system) env.NEXUS_SYSTEM_SMOKE = "1";
  return env;
}

function printModeBanner(mode, suite) {
  if (mode === "codex") {
    console.error("====================================================");
    console.error(" NexusPipeline CODEX FEEDBACK TEST");
    console.error(" Runtime: Test Host");
    console.error(` Suite: ${suite}`);
    console.error(` Integrity: ${getIntegrityLevel()}`);
    console.error(" Administrator validation: deferred to GitHub CI");
    console.error("====================================================");
    return;
  }
  console.error("====================================================");
  console.error(" NexusPipeline ADMINISTRATOR GATE");
  console.error(" Runtime: Production Release");
  console.error(` Suite: ${suite}`);
  console.error(` Integrity: ${getIntegrityLevel()}`);
  console.error("====================================================");
}

async function runUnitOnce(groups = []) {
  // 管理员 UI/System 门禁使用 NEXUS_TIME_SCALE 加速墙钟等待；宿主单元测试应保持生产默认时间语义。
  const env = { ...process.env, NEXUS_TIME_SCALE: "1" };
  const selectedFiles = unitTestFiles(groups);
  if (groups.length > 0 && selectedFiles.length === 0) {
    console.error(`[Unit] 选中的 Area 没有可运行测试：${groups.join(", ")}`);
    return 1;
  }
  const args = ["test", "tests\\NexusPipeline.Tests\\NexusPipeline.Tests.csproj", "--nologo", "-m:1", "--logger", "console;verbosity=normal"];
  if (groups.length > 0) {
    const names = [...new Set(selectedFiles.map(file => path.basename(file, ".cs")))];
    args.push("--filter", names.map(name => `FullyQualifiedName~${name}`).join("|"));
    console.error(`[Unit] Area=${groups.join(",")}，选中 ${names.length} 个测试类。`);
  }
  const groupIds = groups.length === 1 ? [logicalGroupId("host", groups[0])] : [];
  return runReported("dotnet", args, { env }, "trx", {
    groupIds,
    selectedTests: selectedFiles.map(file => path.relative(projectRoot, file).replaceAll("\\", "/")),
    checkId: "tests",
  });
}

async function runUnit(groups = []) {
  const selectedGroups = ciPlannedKeys("host", groups);
  if (ciManifestEnabled() && selectedGroups.length > 0) {
    if (ciJobId() === "host-core") recordCiCheck("build", process.env.NEXUS_CI_BUILD_STATUS || "success");
    if (ciJobId() === "plugin-contract") recordCiCheck("build", process.env.NEXUS_CI_BUILD_STATUS || "success");
    for (const group of selectedGroups) {
      const code = await runUnitOnce([group]);
      if (code !== 0) return code;
    }
    return 0;
  }
  return runUnitOnce(groups);
}

async function runFrontend(groups = []) {
  const selectedGroups = ciPlannedKeys("frontend", groups);
  const hasCiGroups = ciManifestEnabled() && selectedGroups.length > 0;
  const typecheckCode = await runCheckedProcess("typecheck", npmCommand, ["run", "typecheck"], { cwd: frontendDir });
  if (typecheckCode !== 0) return typecheckCode;

  if (hasCiGroups) {
    for (const group of selectedGroups) {
      const selectedFiles = frontendTestFiles([group]);
      if (selectedFiles.length === 0) {
        console.error(`[Frontend] 选中的分组没有可运行测试：${group}`);
        recordCiGroupFailure([logicalGroupId("frontend", group)], { format: "vitest", error: "没有可运行测试" });
        return 1;
      }
      const testArgs = ["run", "test", "--", "--run", ...selectedFiles.map(file => path.relative(frontendDir, file))];
      console.error(`[Frontend] 分组=${group}，选中 ${selectedFiles.length} 个 Vitest 文件。`);
      const code = await runReported(npmCommand, testArgs, { cwd: frontendDir }, "vitest", {
        groupIds: [logicalGroupId("frontend", group)],
        selectedTests: selectedFiles.map(file => path.relative(projectRoot, file).replaceAll("\\", "/")),
        checkId: "tests",
      });
      if (code !== 0) return code;
    }
  } else {
    const selectedFiles = frontendTestFiles(groups);
    if (groups.length > 0 && selectedFiles.length === 0) {
      console.error(`[Frontend] 选中的分组没有可运行测试：${groups.join(", ")}`);
      return 1;
    }
    const testArgs = ["run", "test", "--", "--run"];
    if (groups.length > 0) {
      testArgs.push(...selectedFiles.map(file => path.relative(frontendDir, file)));
      console.error(`[Frontend] 分组=${groups.join(",")}，选中 ${selectedFiles.length} 个 Vitest 文件。`);
    } else {
      console.error(`[Frontend] 全量 Vitest：${selectedFiles.length} 个测试文件。`);
    }
    const testCode = await runReported(npmCommand, testArgs, { cwd: frontendDir }, "vitest", { checkId: "tests" });
    if (testCode !== 0) return testCode;
  }

  const shouldBuild = !ciManifestEnabled()
    ? groups.length === 0
    : ["frontend-unit", "plugin-contract", "full-regression"].includes(ciJobId());
  if (!shouldBuild) return 0;
  return runCheckedProcess("build", npmCommand, ["run", "build"], { cwd: frontendDir });
}

async function runWeb() {
  return runFrontend();
}

async function runRecordedCommand(command, args, {
  groupId = "",
  selectedTest = "",
  checkId = "tests",
} = {}) {
  let output = "";
  const code = await runProcess(command, args, {
    onOutput: chunk => { output = (output + chunk).slice(-262144); },
  });
  const planned = groupId && ciPlannedGroup(groupId);
  if (planned) {
    const passed = code === 0 ? 1 : 0;
    const reportPath = retainNativeReport({
      reportFile: "",
      rawOutput: output,
      format: "command",
      groupIds: [groupId],
    });
    recordCiTests({ testCount: 1, passed, failed: passed ? 0 : 1, skipped: 0 }, {
      groupIds: [groupId],
      selectedTests: selectedTest ? [selectedTest] : [],
      format: "command",
      exitCode: code,
      reportPath,
    });
    recordCiCheck(checkId, code === 0 ? "success" : "failure", code === 0 ? "" : `exit=${code}`);
  }
  return code;
}

async function runContracts() {
  console.error("[Plugin Contract] 加载官方构建模块与真实宿主公共元素。");
  const groupId = "domain:plugin";
  const planned = ciPlannedGroup(groupId);
  const pluginRootCandidates = [
    process.env.NEXUS_OFFICIAL_PLUGINS_ROOT,
    path.join(projectRoot, "NexusPipeline-Plugins"),
    path.resolve(projectRoot, "..", "NexusPipeline-Plugins"),
  ].filter(Boolean).map(candidate => path.resolve(candidate));
  const officialPluginsRoot = pluginRootCandidates.find(candidate => fs.existsSync(path.join(candidate, "tools", "Test-FrontendPlugins.mjs")))
    || pluginRootCandidates[0];
  let code = await runReported(npmCommand, ["run", "test", "--", "--config", "vitest.contract.config.ts"], { cwd: frontendDir }, "vitest", {
    groupIds: planned ? [groupId] : [],
    selectedTests: planned ? ["frontend/contracts/official-plugins.test.ts"] : [],
    checkId: "contract",
  });
  if (code !== 0) return code;

  code = await runRecordedCommand(nodeCommand, [path.join(officialPluginsRoot, "tools", "Test-FrontendPlugins.mjs"), "--host-root", "."], {
    groupId: planned ? groupId : "",
    selectedTest: "NexusPipeline-Plugins/tools/Test-FrontendPlugins.mjs",
    checkId: "contract",
  });
  if (code !== 0) return code;

  return runReported(nodeCommand, ["--test", "tests/tools/plugin-source-layout.test.mjs"], {}, "tap", {
    groupIds: planned ? [groupId] : [],
    selectedTests: planned ? ["tests/tools/plugin-source-layout.test.mjs"] : [],
    checkId: "contract",
  });
}

async function runDocs() {
  const files = [
    "tests\\documentation\\documentation-consistency.mjs",
    "tests\\documentation\\i18n-consistency.mjs",
    "tests\\documentation\\i18n-semantic-consistency.mjs",
    "tests\\documentation\\i18n-audit-consistency.mjs",
    "tests\\documentation\\backend-i18n-audit.mjs",
    "tests\\documentation\\native-scrollbar-audit.mjs",
    "tests\\documentation\\test-policy-consistency.mjs",
    "tests\\tools\\docs-index.test.mjs",
  ];
  const plan = readCiPlan();
  const domainGroup = ciPlannedGroup("domain:docs");
  if (domainGroup) {
    const code = await runReported(nodeCommand, ["--test", ...files.map(file => file.replaceAll("\\", "/"))], {}, "tap", {
      groupIds: ["domain:docs"],
      selectedTests: files.map(file => file.replaceAll("\\", "/")),
      checkId: "tests",
    });
    if (code !== 0) return code;
  }
  const governance = plan?.selectedGroups?.filter(group => group.physicalJobId === ciJobId() && group.kind === "governance") || [];
  if (governance.length > 0) {
    for (const group of governance) {
      if (["architecture-boundaries", "ci-tooling"].includes(group.key)) continue;
      const definition = GOVERNANCE_DOMAINS.find(item => item.key === group.key);
      const selectedFiles = definition ? matchingFiles(definition.testPaths) : [];
      if (selectedFiles.length === 0) {
        recordCiGroupFailure([group.groupId], { error: "没有可运行治理测试" });
        return 1;
      }
      const code = await runReported(nodeCommand, ["--test", ...selectedFiles.map(file => file.replaceAll("\\", "/"))], {}, "tap", {
        groupIds: [group.groupId],
        selectedTests: selectedFiles.map(file => path.relative(projectRoot, file).replaceAll("\\", "/")),
        checkId: "tests",
      });
      if (code !== 0) return code;
    }
    return 0;
  }
  if (domainGroup) return 0;
  return runReported(nodeCommand, ["--test", ...files.map(file => file.replaceAll("\\", "/"))], {}, "tap", {
    checkId: "tests",
  });
}

async function runTooling() {
  const files = [
    "tests\\tools\\ci-domains.test.mjs",
    "tests\\tools\\ci-summary.test.mjs",
    "tests\\tools\\ci-fingerprint.test.mjs",
    "tests\\tools\\test-results.test.mjs",
    "tests\\tools\\runner-manifest.test.mjs",
    "tests\\tools\\frontend-boundaries.test.mjs",
    "tests\\tools\\source-encoding.test.mjs",
    "tests\\tools\\update-policy-history.test.mjs",
  ];
  const plan = readCiPlan();
  const governance = plan?.selectedGroups?.filter(group => group.physicalJobId === ciJobId() && group.kind === "governance"
    && ["architecture-boundaries", "ci-tooling"].includes(group.key)) || [];
  if (governance.length > 0) {
    for (const group of governance) {
      const definition = GOVERNANCE_DOMAINS.find(item => item.key === group.key);
      const selectedFiles = definition ? matchingFiles(definition.testPaths) : [];
      if (selectedFiles.length === 0) {
        recordCiGroupFailure([group.groupId], { error: "没有可运行治理测试" });
        return 1;
      }
      const code = await runReported(nodeCommand, ["--test", ...selectedFiles.map(file => file.replaceAll("\\", "/"))], {}, "tap", {
        groupIds: [group.groupId],
        selectedTests: selectedFiles.map(file => path.relative(projectRoot, file).replaceAll("\\", "/")),
        checkId: "tests",
      });
      if (code !== 0) return code;
    }
  } else {
    const code = await runReported(nodeCommand, ["--test", ...files.map(file => file.replaceAll("\\", "/"))], {}, "tap", { checkId: "tests" });
    if (code !== 0) return code;
  }
  return runCheckedProcess("boundaries", nodeCommand, ["tools/frontend-boundaries.mjs", "--quiet"]);
}

async function runSyntax() {
  const files = syntaxTestFiles();
  if (files.length === 0) throw new Error("未找到 UI Smoke 语法检查文件");
  for (const file of files) {
    const code = await runProcess(nodeCommand, ["--check", file]);
    if (code !== 0) return code;
  }
  return 0;
}

async function runBuild() {
  if (!productionBuildPromise) {
    console.error("[Build] 本次 runner 首次请求生产构建，后续入口复用同一构建结果。");
    productionBuildPromise = runCmdFile(path.join(projectRoot, "build.cmd"));
  } else {
    console.error("[Build] 复用本次 runner 已完成的生产构建。");
  }
  const code = await productionBuildPromise;
  recordCiCheck("build", code === 0 ? "success" : "failure", code === 0 ? "" : `exit=${code}`);
  return code;
}

async function runDefault(mode, { permissionChecked = false } = {}) {
  if (mode === "admin" && !permissionChecked && !requireAdmin("管理员默认门禁", "admin default")) return 2;
  for (const step of [runUnit, runWeb, runContracts, runDocs, runTooling, runSyntax, runBuild]) {
    const code = await step();
    if (code !== 0) return code;
  }
  return 0;
}

async function buildTestHost() {
  if (testHostBuildPromise) {
    console.error("[Test Host] 复用本次 runner 已完成的 Test Host 构建。");
    return testHostBuildPromise;
  }
  testHostBuildPromise = buildTestHostCore();
  return testHostBuildPromise;
}

async function buildTestHostCore() {
  console.error(`[Test Host] 开始构建 Codex 本地反馈宿主：${testHostDir}`);
  fs.rmSync(testHostDir, { recursive: true, force: true, maxRetries: 120, retryDelay: 250 });
  fs.mkdirSync(testHostDir, { recursive: true });
  const code = await runProcess("dotnet", [
    "publish",
    "src\\NexusPipeline.csproj",
    "-c", "Release",
    "-r", "win-x64",
    "--self-contained", "false",
    "-p:PublishSingleFile=true",
    "-p:DebugType=none",
    "-p:DebugSymbols=false",
    "-p:NexusTestHost=true",
    "-o", testHostDir,
    "--nologo",
  ]);
  if (code !== 0) return code;
  fs.cpSync(path.join(projectRoot, "frontend", "dist"), path.join(testHostDir, "wwwroot"), { recursive: true });
  const pluginsDir = path.join(testHostDir, "plugins");
  fs.mkdirSync(pluginsDir, { recursive: true });
  console.error(`[Test Host] 构建完成：${path.join(testHostDir, "nexus-pipeline.exe")}`);
  return 0;
}

function cleanTestHost() {
  try {
    fs.rmSync(testHostDir, { recursive: true, force: true, maxRetries: 120, retryDelay: 250 });
    console.error(`[Test Host] 已清理临时产物：${testHostDir}`);
  } catch (error) {
    console.error(`[Test Host] 清理临时产物失败：${error.message}`);
  }
}

function cleanEmulatorFixturePlugin() {
  try {
    fs.rmSync(emulatorFixturePluginDir, { recursive: true, force: true, maxRetries: 120, retryDelay: 250 });
    console.error(`[System Smoke] 已清理模拟器 fixture 插件产物：${emulatorFixturePluginDir}`);
  } catch (error) {
    console.error(`[System Smoke] 清理模拟器 fixture 插件失败：${error.message}`);
  }
}

async function buildEmulatorFixturePlugin() {
  fs.rmSync(emulatorFixturePluginDir, { recursive: true, force: true, maxRetries: 120, retryDelay: 250 });
  fs.mkdirSync(emulatorFixturePluginDir, { recursive: true });
  console.error(`[System Smoke] 构建可控 managed emulator fixture 插件：${emulatorFixturePluginDir}`);
  return runProcess("dotnet", [
    "publish",
    "tests\\fixtures\\NexusPipeline.TestPlugin\\NexusPipeline.TestPlugin.csproj",
    "-c", "Release",
    "-o", emulatorFixturePluginDir,
    "--nologo",
    "-m:1",
    "-nr:false",
  ]);
}

async function runAll(mode, args) {
  if (mode === "admin" && !requireAdmin("管理员全部门禁", "admin all")) return 2;
  retainTestHostForRun = mode === "codex";
  try {
    let code = await runDefault(mode, { permissionChecked: mode === "admin" });
    if (code === 0) code = await runUi(mode, args);
    if (code === 0) code = await runSystem(mode, args);
    return code;
  } finally {
    if (retainTestHostForRun) {
      cleanTestHost();
      retainTestHostForRun = false;
    }
  }
}

async function runUi(mode, args) {
  if (mode === "admin" && !requireAdmin("管理员 UI Smoke", "admin ui")) return 2;
  const buildCode = await runBuild();
  if (buildCode !== 0) return buildCode;
  const testHostCode = mode === "codex" ? await buildTestHost() : 0;
  if (testHostCode !== 0) {
    if (mode === "codex") cleanTestHost();
    return testHostCode;
  }
  const env = modeEnvironment(mode, {
    exitFile: path.join(e2eDir, "runtime", ".nxp", "test-host.exit"),
  });
  if (!args.includes("--realtime")) env.NEXUS_TIME_SCALE = env.NEXUS_TIME_SCALE || "10";
  try {
    const playwrightArgs = [playwrightCli, "test"];
    const groupId = "domain:ui";
    const planned = ciPlannedGroup(groupId);
    return await runReported(nodeCommand, playwrightArgs, { cwd: e2eDir, env }, "playwright", {
      groupIds: planned ? [groupId] : [],
      selectedTests: planned ? syntaxTestFiles().map(file => path.relative(projectRoot, file).replaceAll("\\", "/")) : [],
      checkId: "tests",
    });
  } finally {
    if (mode === "codex" && !retainTestHostForRun) cleanTestHost();
  }
}

function parseNamedGroups(args, groups, label) {
  const selected = [];
  for (let index = 0; index < args.length; index++) {
    const arg = args[index];
    if (arg === "--group") {
      const value = args[index + 1]?.toLowerCase();
      if (!value || value.startsWith("--")) return { error: "--group 缺少分组名" };
      selected.push(value);
      index++;
      continue;
    }
    if (arg.startsWith("--group=")) {
      const value = arg.slice("--group=".length).toLowerCase();
      if (!value) return { error: "--group 缺少分组名" };
      selected.push(value);
      continue;
    }
    return { error: `${label} 不支持参数：${arg}` };
  }
  const unknown = selected.filter(group => !groups.includes(group));
  if (unknown.length > 0) {
    return {
      error: `未知 ${label} 分组：${unknown.join(", ")}\n可用分组：${groups.join(" | ")}`,
    };
  }
  return { groups: [...new Set(selected)] };
}

function matchingFiles(patterns) {
  return recursiveFiles(projectRoot)
    .filter(file => {
      const relative = path.relative(projectRoot, file).replaceAll("\\", "/");
      return patterns.some(pattern => globToRegExp(pattern).test(relative));
    })
    .sort();
}

function listTestPlan() {
  return {
    schemaVersion: 1,
    commands: {
      unit: "node tests/run.mjs unit [--group <area>]",
      frontend: "node tests/run.mjs frontend [--group <group>]",
      system: "node tests/run.mjs <codex|admin> system [--group <group>] [--dry]",
      combinations: ["codex default", "codex ui", "codex system", "codex all", "admin default", "admin ui", "admin system", "admin all"],
    },
    hostAreas: HOST_TEST_AREAS.map(area => ({
      key: area.key,
      paths: area.paths,
      tests: unitTestFiles([area.key]).map(file => path.relative(projectRoot, file).replaceAll("\\", "/")),
    })),
    frontendGroups: FRONTEND_TEST_GROUPS.map(group => ({
      key: group.key,
      paths: group.paths,
      tests: frontendTestFiles([group.key]).map(file => path.relative(projectRoot, file).replaceAll("\\", "/")),
    })),
    systemGroups: SYSTEM_TEST_GROUPS.map(group => ({
      key: group.key,
      suites: group.suitePaths,
      availableSuites: group.suitePaths.filter(file => fs.existsSync(path.join(projectRoot, file))),
    })),
    governanceDomains: GOVERNANCE_DOMAINS.map(domain => ({
      key: domain.key,
      paths: domain.paths,
      tests: matchingFiles(domain.testPaths).map(file => path.relative(projectRoot, file).replaceAll("\\", "/")),
    })),
  };
}

function printTestPlan(json) {
  const plan = listTestPlan();
  if (json) {
    console.log(JSON.stringify(plan, null, 2));
    return 0;
  }
  console.log(`宿主 Area：${plan.hostAreas.map(area => `${area.key}(${area.tests.length})`).join(" | ")}`);
  console.log(`前端分组：${plan.frontendGroups.map(group => `${group.key}(${group.tests.length})`).join(" | ")}`);
  console.log(`System 分组：${plan.systemGroups.map(group => `${group.key}(${group.availableSuites.length}/${group.suites.length})`).join(" | ")}`);
  return 0;
}

async function runUnitCommand(args) {
  if (args.includes("--affected")) args = affectedGroupArgs(args, "host");
  const parsed = parseNamedGroups(args, HOST_TEST_AREAS.map(area => area.key), "Unit");
  if (parsed.error) {
    console.error(parsed.error);
    return 2;
  }
  return runUnit(parsed.groups);
}

async function runFrontendCommand(args) {
  const affected = args.includes("--affected");
  if (affected) args = affectedGroupArgs(args, "frontend");
  const parsed = parseNamedGroups(args, FRONTEND_TEST_GROUPS.map(group => group.key), "Frontend");
  if (parsed.error) {
    console.error(parsed.error);
    return 2;
  }
  const code = await runFrontend(parsed.groups);
  if (code !== 0) return code;
  if (affected && !ciManifestEnabled()) return runCheckedProcess("build", npmCommand, ["run", "build"], { cwd: frontendDir });
  return 0;
}

function affectedGroupArgs(args, kind) {
  if (args.length !== 1) throw new Error("--affected 必须单独使用");
  const plan = JSON.parse(process.env.NEXUS_CI_EXECUTION_PLAN || "null");
  const validation = validateExecutionPlan(plan, { expectedHeadSha: process.env.GITHUB_SHA || "" });
  if (!validation.ok) throw new Error(validation.errors.join("; "));
  const groups = plan.testGroups?.[kind];
  if (!Array.isArray(groups) || groups.length === 0) throw new Error(`计划缺少 ${kind} 测试分组`);
  return groups.flatMap(group => ["--group", group]);
}

function parseSystemGroups(args) {
  const groups = [];
  for (let index = 0; index < args.length; index++) {
    const arg = args[index];
    if (arg === "--realtime" || arg === "--dry") continue;
    if (arg === "--group") {
      const value = args[index + 1]?.toLowerCase();
      if (!value || value.startsWith("--")) return { error: "--group 缺少分组名" };
      groups.push(value);
      index++;
      continue;
    }
    if (arg.startsWith("--group=")) {
      const value = arg.slice("--group=".length).toLowerCase();
      if (!value) return { error: "--group 缺少分组名" };
      groups.push(value);
      continue;
    }
    if (arg.startsWith("--")) return { error: `未知参数：${arg}` };
    groups.push(arg.toLowerCase());
  }

  const unknown = groups.filter(group => !SYSTEM_GROUP_NAMES.includes(group));
  if (unknown.length > 0) {
    const lines = [
      `未知 System Smoke 分组：${unknown.join(", ")}`,
      `可用分组：${SYSTEM_GROUP_NAMES.join(" | ")}`,
    ];
    for (const [group, suites] of SYSTEM_SUITE_GROUPS) {
      lines.push(`  ${group}：${suites.map(([runtimeName]) => runtimeName).join(", ")}`);
    }
    lines.push("用法：node tests\\run.mjs <codex|admin> system [分组...] [--realtime] [--dry]");
    return { error: lines.join("\n") };
  }
  return { groups };
}

function selectSystemSuites(groups) {
  const selectedGroups = groups.length > 0 ? new Set(groups) : null;
  const suites = [];
  for (const [group, groupSuites] of SYSTEM_SUITE_GROUPS) {
    if (selectedGroups && !selectedGroups.has(group)) continue;
    for (const [runtimeName, fileName] of groupSuites) {
      suites.push({ group, runtimeName, file: path.join(systemDir, fileName) });
    }
  }
  return suites;
}

async function runSystem(mode, args) {
  const parsed = parseSystemGroups(args);
  if (parsed.error) {
    console.error(parsed.error);
    return 2;
  }
  const suites = selectSystemSuites(parsed.groups);
  if (parsed.groups.length > 0) {
    const emptyGroups = parsed.groups.filter(group => {
      const definition = SYSTEM_SUITE_GROUPS.find(([name]) => name === group);
      return !definition || definition[1].length === 0;
    });
    if (emptyGroups.length > 0) {
      console.error(`[System Smoke] 选中的分组没有可运行 suite：${emptyGroups.join(", ")}`);
      return 1;
    }
  }
  if (args.includes("--dry")) {
    console.error(`[System Smoke] 干跑模式：共 ${suites.length} 个 suite，未启动构建与运行时。`);
    for (const suite of suites) {
      console.error(`[System Smoke] 影响域 ${suite.group} | ${suite.runtimeName} | ${suite.file}`);
    }
    return 0;
  }
  if (mode === "admin" && !requireAdmin("管理员 System Smoke", "admin system")) return 2;
  const buildCode = await runBuild();
  if (buildCode !== 0) return buildCode;
  const testHostCode = mode === "codex" ? await buildTestHost() : 0;
  if (testHostCode !== 0) {
    if (mode === "codex") cleanTestHost();
    return testHostCode;
  }
  if (suites.some(suite => suite.group === "emulator")) {
    const emulatorFixtureCode = await buildEmulatorFixturePlugin();
    if (emulatorFixtureCode !== 0) {
      cleanEmulatorFixturePlugin();
      if (mode === "codex") cleanTestHost();
      return emulatorFixtureCode;
    }
  }
  const env = {
    ...modeEnvironment(mode, { system: true }),
    // 系统测试使用独立 Web 端口，避免复用用户正在运行的 NexusPipeline 服务。
    NEXUS_SYSTEM_WEB_PORT: process.env.NEXUS_SYSTEM_WEB_PORT || "58831",
  };
  if (!args.includes("--realtime")) env.NEXUS_TIME_SCALE = env.NEXUS_TIME_SCALE || "10";
  const suiteTimeoutMs = 5 * 60 * 1000;
  try {
    for (const { group, runtimeName, file } of suites) {
      const suiteEnv = {
        ...env,
        NEXUS_SYSTEM_RUNTIME_NAME: runtimeName,
        ...(group === "emulator" ? { NEXUS_SYSTEM_EMULATOR_PLUGIN_DIR: emulatorFixturePluginDir } : {}),
        ...(mode === "codex"
          ? { NEXUS_TEST_HOST_EXIT_FILE: path.join(systemDir, runtimeName, ".nxp", "test-host.exit") }
          : {}),
      };
      const label = `${group}/${runtimeName} (${path.basename(file)})`;
      console.error(`[System Smoke] 开始 ${label}`);
      const startedAt = Date.now();
      const code = await runReported(
        nodeCommand,
        ["--test", "--test-concurrency=1", file],
        { env: suiteEnv, timeoutMs: suiteTimeoutMs },
        "tap",
        {
          groupIds: ciPlannedGroup(logicalGroupId("system", group)) ? [logicalGroupId("system", group)] : [],
          selectedTests: [path.relative(projectRoot, file).replaceAll("\\", "/")],
          checkId: "tests",
        },
      );
      console.error(`[System Smoke] 结束 ${label}：exit=${code}，耗时 ${Date.now() - startedAt}ms`);
      if (code !== 0) return code;
    }
    return 0;
  } finally {
    if (suites.some(suite => suite.group === "emulator")) cleanEmulatorFixturePlugin();
    if (mode === "codex" && !retainTestHostForRun) cleanTestHost();
  }
}

function printUsage() {
  console.error("用法：");
  console.error("  node tests\\run.mjs codex <default|ui|system|all> [--realtime]");
  console.error("  node tests\\run.mjs admin <default|ui|system|all> [--realtime]");
  console.error(`  node tests\\run.mjs <codex|admin> system [${SYSTEM_GROUP_NAMES.join("|")}] [--realtime] [--dry]`);
  console.error(`  node tests\\run.mjs unit [--group ${HOST_TEST_AREAS.map(area => area.key).join("|")}]`);
  console.error(`  node tests\\run.mjs frontend [--group ${FRONTEND_TEST_GROUPS.map(group => group.key).join("|")}]`);
  console.error("  node tests\\run.mjs list --json");
  console.error("  node tests\\run.mjs web|contract|docs|tooling|syntax|build");
  console.error("system 省略分组时运行全部 suite；指定分组时按影响域运行，可用 --group <分组> 重复指定。");
  console.error("system --dry 只列出将要执行的 suite，不构建也不启动运行时。");
  console.error("正式组合入口必须显式指定 codex 或 admin；default/ui/system/all 不能省略模式。");
}

async function runMode(mode, args) {
  const suite = args[0]?.toLowerCase();
  const suiteArgs = args.slice(1);
  if (!MODE_SUITES.has(suite)) {
    printUsage();
    return 2;
  }
  printModeBanner(mode, suite);
  switch (suite) {
    case "default":
      return runDefault(mode);
    case "ui":
      return runUi(mode, suiteArgs);
    case "system":
      return runSystem(mode, suiteArgs);
    case "all":
      return runAll(mode, suiteArgs);
    default:
      return 2;
  }
}

const [command = "", ...args] = process.argv.slice(2);
let exitCode;
try {
  switch (command.toLowerCase()) {
    case "unit":
      exitCode = await runUnitCommand(args);
      break;
    case "frontend":
      exitCode = await runFrontendCommand(args);
      break;
    case "contract":
      exitCode = args.length ? 2 : await runContracts();
      break;
    case "list":
      if (args.length > 1 || (args.length === 1 && args[0] !== "--json")) {
        printUsage();
        exitCode = 2;
      } else {
        exitCode = printTestPlan(args.includes("--json"));
      }
      break;
    case "web":
      exitCode = await runWeb();
      break;
    case "docs":
      exitCode = await runDocs();
      break;
    case "tooling":
      exitCode = await runTooling();
      break;
    case "syntax":
      exitCode = await runSyntax();
      break;
    case "build":
      exitCode = await runBuild();
      break;
    case "codex":
      exitCode = await runMode("codex", args);
      break;
    case "admin":
      exitCode = await runMode("admin", args);
      break;
    case "default":
    case "ui":
    case "system":
    case "all":
      printUsage();
      exitCode = 2;
      break;
    default:
      printUsage();
      exitCode = 2;
      break;
  }
} catch (error) {
  console.error(`[错误] ${error.stack || error.message}`);
  exitCode = 1;
}
process.exitCode = exitCode;

if (process.env.NEXUS_CI_MANIFEST && CI_MANIFEST_COMMANDS.has(command.toLowerCase()) && !args.includes("--dry")) {
  const manifestMode = process.env.NEXUS_CI_MODE
    || (command.toLowerCase() === "codex" ? "test-host" : command.toLowerCase() === "admin" ? "admin" : "ci");
  const physicalJobId = ciJobId();
  const sourceDigest = process.env.NEXUS_CI_SOURCE_DIGEST || "";
  const buildInputsDigest = process.env.NEXUS_CI_BUILD_INPUTS_DIGEST || "";
  const counterpartSha = process.env.NEXUS_CI_COUNTERPART_SHA || "";
  const buildFingerprint = process.env.NEXUS_CI_BUILD_FINGERPRINT || createBuildFingerprint({
    mode: manifestMode === "test-host" ? "test-host" : "production",
  });
  const manifest = {
    schemaVersion: 2,
    jobId: physicalJobId,
    domainId: ciExecution.domainId || command.toLowerCase() || "runner",
    headSha: process.env.NEXUS_CI_HEAD_SHA || process.env.GITHUB_SHA || "local",
    repositorySha: process.env.NEXUS_CI_REPOSITORY_SHA || process.env.GITHUB_SHA || "local",
    planDigest: process.env.NEXUS_CI_PLAN_DIGEST || "",
    buildFingerprint,
    sourceDigest,
    buildInputsDigest,
    counterpartRepository: process.env.NEXUS_CI_COUNTERPART_REPOSITORY || "",
    counterpartSha,
    artifactDigest: expectedArtifactDigest({
      buildFingerprint,
      sourceDigest,
      buildInputsDigest,
      counterpartSha,
      mode: manifestMode,
      physicalJobId,
    }),
    mode: manifestMode,
    integrityLevel: process.env.NEXUS_CI_INTEGRITY_LEVEL || getIntegrityLevel(),
    checks: [...ciExecution.checks],
    groups: [...ciExecution.groups],
    result: exitCode === 0 ? "success" : "failure",
    testCount: ciExecution.testCount,
    passed: ciExecution.passed,
    failed: ciExecution.failed,
    skipped: ciExecution.skipped,
    exitCode,
    manifestPresent: true,
  };
  try {
    const manifestPath = path.resolve(process.env.NEXUS_CI_MANIFEST);
    if (fs.existsSync(manifestPath)) {
      const previous = JSON.parse(fs.readFileSync(manifestPath, "utf8"));
      for (const key of [
        "jobId",
        "domainId",
        "headSha",
        "repositorySha",
        "planDigest",
        "mode",
        "sourceDigest",
        "buildInputsDigest",
        "counterpartRepository",
        "counterpartSha",
      ]) {
        if (previous[key] !== manifest[key]) throw new Error(`已有 manifest 的 ${key} 不匹配`);
      }
      for (const key of ["testCount", "passed", "failed", "skipped"]) {
        if (!Number.isInteger(previous[key]) || previous[key] < 0) throw new Error(`已有 manifest 的 ${key} 无效`);
        manifest[key] += previous[key];
      }
      if (previous.result !== "success") {
        manifest.result = "failure";
        manifest.exitCode = previous.exitCode || 1;
      }
      if (Array.isArray(previous.groups)) {
        for (const group of previous.groups) upsertCiGroup(group);
        manifest.groups = [...ciExecution.groups];
      }
      const previousChecks = Array.isArray(previous.checks)
        ? previous.checks
        : [];
      for (const check of previousChecks) {
        if (check?.checkId) recordCiCheck(check.checkId, check.status, check.detail || "");
      }
      manifest.checks = [...ciExecution.checks];
    }
    fs.mkdirSync(path.dirname(manifestPath), { recursive: true });
    fs.writeFileSync(manifestPath, `${JSON.stringify(manifest, null, 2)}\n`, "utf8");
  } catch (error) {
    console.error(`[CI manifest] 写入失败：${error.message}`);
    if (exitCode === 0) process.exitCode = 1;
  }
}
