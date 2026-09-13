import { spawn } from "node:child_process";
import fs from "node:fs";
import os from "node:os";
import path from "node:path";
import { fileURLToPath } from "node:url";

const projectRoot = path.resolve(path.dirname(fileURLToPath(import.meta.url)), "..", "..");
const diagnosticProject = path.join(projectRoot, "tests", "stress", "RuntimeEfficiencyDiagnostic", "RuntimeEfficiencyDiagnostic.csproj");

function parseArgs(argv) {
  const options = { output: null, ticks: "600", appendBytes: "4096", keepRuntime: false };
  for (let index = 0; index < argv.length; index++) {
    const arg = argv[index];
    const value = () => {
      if (!argv[index + 1] || argv[index + 1].startsWith("--")) throw new Error(`${arg} 缺少值`);
      return argv[++index];
    };
    if (arg === "--output") options.output = path.resolve(value());
    else if (arg === "--ticks") options.ticks = value();
    else if (arg === "--append-bytes") options.appendBytes = value();
    else if (arg === "--keep-runtime") options.keepRuntime = true;
    else if (arg === "--help") {
      console.log("用法：node tests/stress/runtime-efficiency.mjs [--output FILE] [--ticks 600] [--append-bytes 4096] [--keep-runtime]");
      process.exit(0);
    } else throw new Error(`未知参数：${arg}`);
  }
  return options;
}

function run(command, args, cwd) {
  return new Promise((resolve, reject) => {
    console.log(`[runtime-efficiency] 执行：${command} ${args.join(" ")}`);
    const child = spawn(command, args, { cwd, stdio: "inherit", windowsHide: false });
    child.once("error", reject);
    child.once("exit", code => code === 0 ? resolve() : reject(new Error(`诊断进程退出码 ${code ?? 1}`)));
  });
}

const options = parseArgs(process.argv.slice(2));
const runtimeDirectory = fs.mkdtempSync(path.join(os.tmpdir(), "nxp-runtime-efficiency-"));
const outputPath = options.output || path.join(runtimeDirectory, "runtime-efficiency.json");
try {
  console.log(`[runtime-efficiency] 使用隔离 runtime：${runtimeDirectory}`);
  console.log(`[runtime-efficiency] 参数：${options.ticks} 个 idle tick、100 MiB 日志、追加 ${options.appendBytes} bytes`);
  await run("dotnet", [
    "run",
    "--project", diagnosticProject,
    "--no-build",
    "--no-restore",
    "-p:NuGetAudit=false",
    "--",
    "--runtime", runtimeDirectory,
    "--output", outputPath,
    "--ticks", options.ticks,
    "--append-bytes", options.appendBytes,
  ], projectRoot);
  const result = JSON.parse(fs.readFileSync(outputPath, "utf8"));
  console.log(`[runtime-efficiency] scheduler state writes=${result.schedulerIdle.StateSaveCount} bytes=${result.schedulerIdle.StateBytes}`);
  console.log(`[runtime-efficiency] checkpoint open=${result.logMonitor100MiB.CheckpointBytesAtOpen} bytes after append=${result.logMonitor100MiB.CheckpointBytesAfterAppend}`);
  console.log(`[runtime-efficiency] screenshot gate schedules no-judge=${result.screenshotCadence.NoJudgeScheduleCount} judge=${result.screenshotCadence.JudgeScheduleCount}`);
  console.log(`[runtime-efficiency] ReadNew allocation=${result.logMonitor100MiB.ReadAllocatedBytes} bytes, read=${result.logMonitor100MiB.ReadCharacters} chars`);
  console.log(`[runtime-efficiency] 机器可读结果：${outputPath}`);
} finally {
  if (!options.keepRuntime) {
    fs.rmSync(runtimeDirectory, { recursive: true, force: true, maxRetries: 10, retryDelay: 100 });
    if (!options.output) {
      try { fs.rmSync(outputPath, { force: true }); } catch { /* best effort */ }
    }
    console.log(`[runtime-efficiency] 已清理隔离 runtime：${runtimeDirectory}`);
  } else {
    console.log(`[runtime-efficiency] 已保留隔离 runtime：${runtimeDirectory}`);
  }
}
