import { spawn } from "node:child_process";
import fs from "node:fs";
import path from "node:path";
import { fileURLToPath } from "node:url";
import { getIntegrityLevel, isAdministrator, killProcessTree } from "./support/windows-process.mjs";

const projectRoot = path.resolve(path.dirname(fileURLToPath(import.meta.url)), "..");
const e2eDir = path.join(projectRoot, "tests", "e2e");
const systemDir = path.join(projectRoot, "tests", "system");
const testHostDir = path.join(projectRoot, "tests", ".artifacts", "test-host");
const nodeCommand = process.execPath;
const playwrightCli = path.join(e2eDir, "node_modules", "playwright", "cli.js");
const TEST_HOST_ENV_KEYS = ["NEXUS_TEST_HOST", "NEXUS_TEST_HOST_DIR", "NEXUS_TEST_HOST_EXIT_FILE"];
const MODE_SUITES = new Set(["default", "ui", "system", "all"]);
// System Smoke 影响域分组：CI 按改动范围选择分组，每个分组复用同一份 suite 定义。
const SYSTEM_SUITE_GROUPS = [
  ["runtime", [
    ["runtime-mcp", "mcp-smoke.mjs"],
    ["runtime-runtime", "runtime-smoke.mjs"],
  ]],
  ["execution", [
    ["runtime-judge", "judge-smoke.mjs"],
    ["runtime-execution-resilience", "execution-resilience.mjs"],
  ]],
  ["emulator", [
    ["runtime-emulator", "emulator-smoke.mjs"],
  ]],
  ["update", [
    ["runtime-startup-update", "startup-update-smoke.mjs"],
    ["runtime-update", "update-smoke.mjs"],
  ]],
];
const SYSTEM_GROUP_NAMES = SYSTEM_SUITE_GROUPS.map(([group]) => group);

function runProcess(command, args, options = {}) {
  return new Promise(resolve => {
    const label = [command, ...args].join(" ");
    const timeoutMs = Number.isFinite(options.timeoutMs) && options.timeoutMs > 0
      ? options.timeoutMs
      : null;
    const timeoutCode = options.timeoutCode ?? 124;
    let timeoutHandle = null;
    let settled = false;
    let timedOut = false;
    const finish = code => {
      if (settled) return;
      settled = true;
      if (timeoutHandle) clearTimeout(timeoutHandle);
      resolve(code);
    };
    const child = spawn(command, args, {
      cwd: options.cwd || projectRoot,
      env: options.env || process.env,
      stdio: options.stdio || "inherit",
      windowsHide: false,
    });
    child.once("error", error => {
      console.error(`[错误] 启动 ${command} 失败：${error.message}`);
      finish(timedOut ? timeoutCode : 1);
    });
    child.once("exit", (code, signal) => {
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
        finish(timeoutCode);
      }, timeoutMs);
    }
  });
}

function runCmdFile(filePath, args = [], options = {}) {
  const cwd = options.cwd || projectRoot;
  const relativePath = path.relative(cwd, filePath) || path.basename(filePath);
  const commandLine = ["call", relativePath, ...args].join(" ");
  return runProcess(process.env.ComSpec || "cmd.exe", ["/d", "/s", "/c", commandLine], { ...options, cwd });
}

function webTestFiles() {
  const directory = path.join(projectRoot, "tests", "web");
  if (!fs.existsSync(directory)) return [];
  return fs.readdirSync(directory)
    .filter(name => name.endsWith(".test.mjs"))
    .sort()
    .map(name => path.join(directory, name));
}

function syntaxTestFiles() {
  const directory = path.join(projectRoot, "tests", "e2e", "tests");
  return fs.readdirSync(directory)
    .filter(name => name.endsWith(".smoke.spec.mjs"))
    .sort()
    .map(name => path.join(directory, name));
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

async function runUnit() {
  return runProcess("dotnet", ["test", "tests\\NexusPipeline.Tests\\NexusPipeline.Tests.csproj", "--nologo", "-m:1"]);
}

async function runWeb() {
  const files = webTestFiles();
  if (files.length === 0) {
    console.error("[Web Logic] 当前 Web Logic 用例由 frontend Vitest 承载，本步骤无独立 Node 用例。");
    return 0;
  }
  return runProcess(nodeCommand, ["--test", ...files]);
}

async function runDocs() {
  return runProcess(nodeCommand, ["--test", "tests\\documentation\\documentation-consistency.mjs", "tests\\documentation\\i18n-consistency.mjs", "tests\\documentation\\i18n-semantic-consistency.mjs", "tests\\documentation\\i18n-audit-consistency.mjs", "tests\\documentation\\backend-i18n-audit.mjs", "tests\\documentation\\native-scrollbar-audit.mjs", "tests\\documentation\\test-policy-consistency.mjs"]);
}

async function runTooling() {
  return runProcess(nodeCommand, ["--test", "tests\\tools\\ci-domains.test.mjs"]);
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
  return runCmdFile(path.join(projectRoot, "build.cmd"));
}

async function runDefault(mode, { permissionChecked = false } = {}) {
  if (mode === "admin" && !permissionChecked && !requireAdmin("管理员默认门禁", "admin default")) return 2;
  for (const step of [runUnit, runWeb, runDocs, runTooling, runSyntax, runBuild]) {
    const code = await step();
    if (code !== 0) return code;
  }
  return 0;
}

async function buildTestHost() {
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

async function runAll(mode, args) {
  if (mode === "admin" && !requireAdmin("管理员全部门禁", "admin all")) return 2;
  let code = await runDefault(mode, { permissionChecked: mode === "admin" });
  if (code === 0) code = await runUi(mode, args);
  if (code === 0) code = await runSystem(mode, args);
  return code;
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
    return await runProcess(nodeCommand, playwrightArgs, { cwd: e2eDir, env });
  } finally {
    if (mode === "codex") cleanTestHost();
  }
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
        ...(mode === "codex"
          ? { NEXUS_TEST_HOST_EXIT_FILE: path.join(systemDir, runtimeName, ".nxp", "test-host.exit") }
          : {}),
      };
      const label = `${group}/${runtimeName} (${path.basename(file)})`;
      console.error(`[System Smoke] 开始 ${label}`);
      const startedAt = Date.now();
      const code = await runProcess(
        nodeCommand,
        ["--test", "--test-concurrency=1", file],
        { env: suiteEnv, timeoutMs: suiteTimeoutMs },
      );
      console.error(`[System Smoke] 结束 ${label}：exit=${code}，耗时 ${Date.now() - startedAt}ms`);
      if (code !== 0) return code;
    }
    return 0;
  } finally {
    if (mode === "codex") cleanTestHost();
  }
}

function printUsage() {
  console.error("用法：");
  console.error("  node tests\\run.mjs codex <default|ui|system|all> [--realtime]");
  console.error("  node tests\\run.mjs admin <default|ui|system|all> [--realtime]");
  console.error(`  node tests\\run.mjs <codex|admin> system [${SYSTEM_GROUP_NAMES.join("|")}] [--realtime] [--dry]`);
  console.error("  node tests\\run.mjs unit|web|docs|tooling|syntax|build");
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
      exitCode = await runUnit();
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
