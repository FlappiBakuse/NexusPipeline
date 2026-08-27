import { spawn } from "node:child_process";
import fs from "node:fs";
import path from "node:path";
import { fileURLToPath } from "node:url";
import { isAdministrator, killProcessTree } from "./support/windows-process.mjs";

const projectRoot = path.resolve(path.dirname(fileURLToPath(import.meta.url)), "..");
const e2eDir = path.join(projectRoot, "tests", "e2e");
const systemDir = path.join(projectRoot, "tests", "system");
const nodeCommand = process.execPath;
const playwrightCli = path.join(e2eDir, "node_modules", "playwright", "cli.js");

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
  const files = fs.readdirSync(directory)
    .filter(name => name.endsWith(".test.mjs"))
    .sort()
    .map(name => path.join(directory, name));
  if (files.length === 0) throw new Error("未找到 Web Logic 测试文件");
  return files;
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
  console.error(`[错误] ${label}需要管理员终端。请在管理员终端执行：node tests/run.mjs ${command}`);
  return false;
}

async function runUnit() {
  return runProcess("dotnet", ["test", "tests\\NexusPipeline.Tests\\NexusPipeline.Tests.csproj", "--nologo"]);
}

async function runWeb() {
  return runProcess(nodeCommand, ["--test", ...webTestFiles()]);
}

async function runDocs() {
  return runProcess(nodeCommand, ["--test", "tests\\documentation\\documentation-consistency.mjs"]);
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

async function runDefault() {
  for (const step of [runUnit, runWeb, runDocs, runSyntax, runBuild]) {
    const code = await step();
    if (code !== 0) return code;
  }
  return 0;
}

async function runUi(args) {
  if (!requireAdmin("UI Smoke", "ui")) return 2;
  const env = { ...process.env };
  if (!args.includes("--realtime")) env.NEXUS_TIME_SCALE = env.NEXUS_TIME_SCALE || "10";
  return runProcess(nodeCommand, [playwrightCli, "test"], { cwd: e2eDir, env });
}

async function runSystem(args) {
  if (!requireAdmin("System Smoke", "system")) return 2;
  const env = {
    ...process.env,
    NEXUS_SYSTEM_SMOKE: "1",
  };
  if (!args.includes("--realtime")) env.NEXUS_TIME_SCALE = env.NEXUS_TIME_SCALE || "10";
  const suites = [
    ["runtime-mcp", "mcp-smoke.mjs"],
    ["runtime-runtime", "runtime-smoke.mjs"],
    ["runtime-judge", "judge-smoke.mjs"],
    ["runtime-execution-resilience", "execution-resilience.mjs"],
    ["runtime-emulator", "emulator-smoke.mjs"],
    ["runtime-update", "update-smoke.mjs"],
  ];
  const suiteTimeoutMs = 5 * 60 * 1000;
  for (const [runtimeName, file] of suites) {
    const suiteEnv = { ...env, NEXUS_SYSTEM_RUNTIME_NAME: runtimeName };
    const label = `${runtimeName} (${file})`;
    console.error(`[System Smoke] 开始 ${label}`);
    const startedAt = Date.now();
    const code = await runProcess(
      nodeCommand,
      ["--test", "--test-concurrency=1", path.join(systemDir, file)],
      { env: suiteEnv, timeoutMs: suiteTimeoutMs },
    );
    console.error(`[System Smoke] 结束 ${label}：exit=${code}，耗时 ${Date.now() - startedAt}ms`);
    if (code !== 0) return code;
  }
  return 0;
}

function printUsage() {
  console.error("用法：node tests/run.mjs default|unit|web|docs|syntax|build|ui|system|all");
}

const [command = "default", ...args] = process.argv.slice(2);
let exitCode;
try {
  switch (command.toLowerCase()) {
    case "default":
      exitCode = await runDefault();
      break;
    case "unit":
      exitCode = await runUnit();
      break;
    case "web":
      exitCode = await runWeb();
      break;
    case "docs":
      exitCode = await runDocs();
      break;
    case "syntax":
      exitCode = await runSyntax();
      break;
    case "build":
      exitCode = await runBuild();
      break;
    case "ui":
      exitCode = await runUi(args);
      break;
    case "system":
      exitCode = await runSystem(args);
      break;
    case "all":
      exitCode = await runDefault();
      if (exitCode === 0) exitCode = await runUi(args);
      if (exitCode === 0) exitCode = await runSystem(args);
      break;
    default:
      printUsage();
      exitCode = 1;
      break;
  }
} catch (error) {
  console.error(`[错误] ${error.stack || error.message}`);
  exitCode = 1;
}
process.exitCode = exitCode;
