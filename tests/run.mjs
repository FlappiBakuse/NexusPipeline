import fs from "node:fs";
import path from "node:path";
import { execFileSync } from "node:child_process";
import { fileURLToPath } from "node:url";
import { runProcess, getProcessRunnerState, resetProcessRunnerState } from "./support/process-runner.mjs";
import { findAvailablePort } from "./support/test-runtime.mjs";

/**
 * 统一测试入口。三个命令分别对应本地开发真正需要的三种验证：
 *   smoke       语法、核心 xUnit、前端类型与核心 Vitest、文档内链
 *   integration 单次发布 asInvoker Test Host，跑 UI Smoke 与 System Smoke
 *   release     生产构建（requireAdministrator）与内嵌清单校验
 */

const projectRoot = path.resolve(path.dirname(fileURLToPath(import.meta.url)), "..");
const frontendDir = path.join(projectRoot, "frontend");
const e2eDir = path.join(projectRoot, "tests", "e2e");
const runId = process.env.NEXUS_TEST_RUN_ID?.trim() || `run-${Date.now()}-${process.pid}`;
const runRoot = path.join(projectRoot, "tests", ".artifacts", "runs", runId);
const testHostDir = path.join(runRoot, "test-host");
const npmCommand = process.platform === "win32" ? "npm.cmd" : "npm";
const pythonCommand = process.platform === "win32" ? "python" : "python3";
const playwrightCli = path.join(e2eDir, "node_modules", "playwright", "cli.js");

function step(label) {
  console.error(`\n== ${label} ==`);
}

async function run(command, args, options = {}) {
  return runProcess(command, args, { defaultCwd: projectRoot, ...options });
}

/** 逐个文件做语法检查；收集失败后一次性报告，避免中途退出丢证据。 */
async function runSyntaxCheck() {
  step("语法检查");
  const roots = [path.join(projectRoot, "tests"), path.join(projectRoot, "tools")];
  const failures = [];
  const walk = directory => fs.readdirSync(directory, { withFileTypes: true }).flatMap(entry => {
    const full = path.join(directory, entry.name);
    if (entry.isDirectory()) return entry.name === "node_modules" ? [] : walk(full);
    return entry.isFile() && entry.name.endsWith(".mjs") ? [full] : [];
  });
  const files = roots.flatMap(walk);
  for (const file of files) {
    const code = await run(process.execPath, ["--check", file]);
    if (code !== 0) failures.push(path.relative(projectRoot, file));
  }
  if (files.length === 0) {
    console.error("未找到可检查的 .mjs 文件");
    return 1;
  }
  if (failures.length > 0) {
    console.error(`语法检查失败：${failures.join(", ")}`);
    return 1;
  }
  console.error(`语法检查通过：${files.length} 个文件`);
  return 0;
}

async function runUnit() {
  step("核心 xUnit");
  return run("dotnet", [
    "test", "tests\\NexusPipeline.Tests\\NexusPipeline.Tests.csproj",
    "-p:NexusTestHost=true", "--nologo",
  ], { timeoutMs: 15 * 60 * 1000 });
}

async function ensureNpm(directory) {
  if (fs.existsSync(path.join(directory, "node_modules", ".package-lock.json"))) return 0;
  return run(npmCommand, ["ci", "--no-audit", "--no-fund"], { cwd: directory, timeoutMs: 15 * 60 * 1000 });
}

async function runFrontend() {
  step("前端类型检查与核心 Vitest");
  let code = await ensureNpm(frontendDir);
  if (code !== 0) return code;
  code = await run(npmCommand, ["run", "typecheck"], { cwd: frontendDir });
  if (code !== 0) return code;
  return run(npmCommand, ["run", "test"], { cwd: frontendDir });
}

async function runDocLinks() {
  step("文档内链检查");
  return run(process.execPath, ["tools\\check-doc-links.mjs"]);
}

async function runSmoke() {
  for (const runStep of [runSyntaxCheck, runUnit, runFrontend, runDocLinks]) {
    const code = await runStep();
    if (code !== 0) return code;
  }
  return 0;
}

async function buildFrontendBundle() {
  step("前端生产构建");
  let code = await ensureNpm(frontendDir);
  if (code !== 0) return code;
  code = await run(npmCommand, ["run", "typecheck"], { cwd: frontendDir });
  if (code !== 0) return code;
  code = await run(npmCommand, ["run", "build"], { cwd: frontendDir });
  if (code !== 0) return code;
  // 发行候选校验 --frontend-ready 时按该指纹确认 dist 与源码一致。
  const stamp = execFileSync(process.execPath, [path.join(projectRoot, "tools", "source-hash.mjs"), "--frontend"], {
    cwd: projectRoot,
    encoding: "utf8",
  }).trim();
  const stampPath = path.join(projectRoot, ".generated", "frontend-build.hash");
  fs.mkdirSync(path.dirname(stampPath), { recursive: true });
  fs.writeFileSync(stampPath, `${stamp}\n`, "utf8");
  return 0;
}

/** 发布 asInvoker Test Host：nexus-pipeline.exe 与 wwwroot 放在同一次运行目录内。 */
async function publishTestHost() {
  step("发布 asInvoker Test Host");
  fs.rmSync(testHostDir, { recursive: true, force: true, maxRetries: 120, retryDelay: 250 });
  fs.mkdirSync(testHostDir, { recursive: true });
  const code = await run("dotnet", [
    "publish", "src\\NexusPipeline.csproj", "-c", "Release", "-r", "win-x64", "--self-contained", "false",
    "-p:PublishSingleFile=true", "-p:DebugType=none", "-p:DebugSymbols=false", "-p:NexusTestHost=true",
    "-o", testHostDir, "--nologo",
  ], { timeoutMs: 20 * 60 * 1000 });
  if (code !== 0) return code;
  const manifestCode = await verifyManifest(path.join(testHostDir, "nexus-pipeline.exe"), "asInvoker");
  if (manifestCode !== 0) return code;
  fs.cpSync(path.join(frontendDir, "dist"), path.join(testHostDir, "wwwroot"), { recursive: true });
  fs.mkdirSync(path.join(testHostDir, "plugins"), { recursive: true });
  return 0;
}

function verifyManifest(executable, expectedLevel) {
  return run(pythonCommand, ["tools\\pe_manifest.py", "--exe", executable, "--expected-level", expectedLevel]);
}

function testHostEnvironment({ runtimeName, webPort, exitFile }) {
  const env = { ...process.env };
  Object.assign(env, {
    NEXUS_TEST_MODE: "test-host",
    NEXUS_TEST_HOST: "1",
    NEXUS_TEST_HOST_DIR: testHostDir,
    NEXUS_TEST_HOST_EXIT_FILE: exitFile,
    NEXUS_SYSTEM_SMOKE: "1",
    NEXUS_SYSTEM_ACTION_DRYRUN: "1",
    NEXUS_TIME_SCALE: "10",
    NEXUS_TEST_RUN_ID: runId,
    NEXUS_SYSTEM_RUNTIME_NAME: runtimeName,
    NEXUS_SYSTEM_WEB_PORT: String(webPort),
  });
  return env;
}

async function runUiSmoke() {
  step("UI Smoke（Edge，单浏览器会话）");
  const code = await ensureNpm(e2eDir);
  if (code !== 0) return code;
  const webPort = await findAvailablePort();
  const env = testHostEnvironment({
    runtimeName: "ui",
    webPort,
    exitFile: path.join(runRoot, "ui", ".nxp", "test-host.exit"),
  });
  env.NEXUS_E2E_BASE_URL = `http://127.0.0.1:${webPort}/`;
  return run(process.execPath, [playwrightCli, "test"], { cwd: e2eDir, env, timeoutMs: 15 * 60 * 1000 });
}

async function runSystemSuite(suite) {
  const webPort = await findAvailablePort();
  const env = testHostEnvironment({
    runtimeName: suite.runtimeName,
    webPort,
    exitFile: path.join(runRoot, suite.runtimeName, ".nxp", "test-host.exit"),
  });
  console.error(`\n-- System Smoke ${suite.file} (port=${webPort}) --`);
  return run(process.execPath, ["--test", "--test-concurrency=1", suite.file], { env, timeoutMs: 15 * 60 * 1000 });
}

async function runSystemSmoke() {
  step("System Smoke");
  for (const suite of [
    { file: "tests/system/runtime-smoke.mjs", runtimeName: "runtime" },
    { file: "tests/system/config-smoke.mjs", runtimeName: "config-runtime" },
    { file: "tests/system/judge-smoke.mjs", runtimeName: "judge-runtime" },
    { file: "tests/system/mcp-smoke.mjs", runtimeName: "mcp-runtime" },
  ]) {
    const code = await runSystemSuite(suite);
    if (code !== 0) return code;
  }
  return 0;
}

async function runIntegration() {
  let code = await buildFrontendBundle();
  if (code !== 0) return code;
  code = await publishTestHost();
  if (code !== 0) return code;
  code = await runUiSmoke();
  if (code !== 0) return code;
  return runSystemSmoke();
}

/** 生产构建只发布程序自有文件，release/ 下的用户运行数据保持原样。 */
async function runRelease() {
  step("生产构建");
  let code = await buildFrontendBundle();
  if (code !== 0) return code;
  const releaseDir = path.join(projectRoot, "release");
  fs.mkdirSync(releaseDir, { recursive: true });
  fs.rmSync(path.join(releaseDir, "wwwroot"), { recursive: true, force: true, maxRetries: 120, retryDelay: 250 });
  code = await run("dotnet", [
    "publish", "src\\NexusPipeline.csproj", "-c", "Release", "-r", "win-x64", "--self-contained", "false",
    "-p:PublishSingleFile=true", "-p:DebugType=none", "-p:DebugSymbols=false", "-o", releaseDir, "--nologo",
  ], { timeoutMs: 20 * 60 * 1000 });
  if (code !== 0) return code;
  fs.cpSync(path.join(frontendDir, "dist"), path.join(releaseDir, "wwwroot"), { recursive: true });
  fs.mkdirSync(path.join(releaseDir, "plugins"), { recursive: true });
  return verifyManifest(path.join(releaseDir, "nexus-pipeline.exe"), "requireAdministrator");
}

function printUsage() {
  console.error("用法：node tests\\run.mjs smoke|integration|release");
  console.error("  smoke       语法 + 核心 xUnit + 前端类型/核心 Vitest + 文档内链");
  console.error("  integration 发布 asInvoker Test Host，运行 UI Smoke 与 System Smoke");
  console.error("  release     生产 requireAdministrator 构建与内嵌清单校验");
}

const [command = ""] = process.argv.slice(2);
const commands = { smoke: runSmoke, integration: runIntegration, release: runRelease };
let exitCode = 2;
resetProcessRunnerState();
try {
  const selected = commands[command.toLowerCase()];
  if (!selected) {
    printUsage();
  } else if (process.argv.length !== 3) {
    printUsage();
  } else {
    exitCode = await selected();
  }
} catch (error) {
  console.error(`[错误] ${error.stack || error.message}`);
  exitCode = 1;
} finally {
  const runnerState = getProcessRunnerState();
  if (!runnerState.cleanupComplete) {
    console.error(`[清理] 进程树清理未确认完成：${runnerState.cleanupFailures.join("；")}`);
    exitCode = exitCode === 0 ? 1 : exitCode;
  }
}
process.exitCode = exitCode;
