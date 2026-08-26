import fs from "node:fs";
import path from "node:path";
import { fileURLToPath } from "node:url";
import {
  isProcessAlive,
  readListeningPid,
  readPidFile,
} from "../../support/windows-process.mjs";

const __dirname = path.dirname(fileURLToPath(import.meta.url));
const runtimeDir = path.resolve(__dirname, "..", "runtime");
const resultsDir = path.join(__dirname, "flake-monitor-logs");
const logFile = path.join(resultsDir, "flake-monitor.log");
const stopFile = path.join(resultsDir, "flake-monitor.stop");
const servicePidPath = path.join(runtimeDir, ".nxp", "runtime", "service.pid");
const PORT = 58731;

function tailServiceLog(lines = 30) {
  const logsDir = path.join(runtimeDir, "logs");
  try {
    const files = fs.readdirSync(logsDir).filter(f => /^nexus-pipeline-\d{4}-\d{2}-\d{2}\.log$/.test(f)).sort();
    if (!files.length) return "（无服务日志文件）";
    const text = fs.readFileSync(path.join(logsDir, files[files.length - 1]), "utf8").replace(/^\uFEFF/, "");
    return text.split(/\r?\n/).filter(Boolean).slice(-lines).join("\n");
  } catch (error) {
    return `（读取服务日志失败：${error.message}）`;
  }
}

function stamp() {
  const d = new Date();
  const p = n => String(n).padStart(2, "0");
  return `${p(d.getHours())}:${p(d.getMinutes())}:${p(d.getSeconds())}.${String(d.getMilliseconds()).padStart(3, "0")}`;
}

function emit(prefix, text) {
  fs.appendFileSync(logFile, `[${stamp()}] ${prefix} ${text}\n`, "utf8");
  console.log(`[flake-monitor ${stamp()}] ${prefix} ${text}`);
}

function sample() {
  const pid = readPidFile(servicePidPath);
  const alive = pid !== null && isProcessAlive(pid);
  const owner = readListeningPid(PORT);
  const listening = owner !== null;
  return `${alive ? pid : ""}|${listening}|${owner || ""}`;
}

let previousAlive = false;
let previousListening = false;
let lastPids = "";
let lastWrite = 0;

function onLine(line) {
  const parts = line.trim().split("|");
  const pids = parts[0] || "";
  const listening = parts[1] === "True";
  const alive = pids.length > 0;

  if (previousAlive && !alive) {
    emit("DEATH", `runtime nexus-pipeline 进程退出（此前 PID：${lastPids}）`);
    emit("DEATH-LOG", "服务日志尾部：\n" + tailServiceLog(30));
  } else if (!previousAlive && alive) {
    emit("START", `runtime nexus-pipeline 进程出现（PID：${pids}）`);
  }
  if (previousListening && !listening) {
    emit("LISTEN-LOST", `端口 ${PORT} 停止监听`);
  } else if (!previousListening && listening) {
    emit("LISTEN-UP", `端口 ${PORT} 开始监听`);
  }
  previousAlive = alive;
  previousListening = listening;
  if (alive) lastPids = pids;

  const now = Date.now();
  if (now - lastWrite >= 5000) {
    emit("TICK", `alive=${alive} pids=[${pids}] listen${PORT}=${listening}`);
    lastWrite = now;
  }
}

fs.mkdirSync(resultsDir, { recursive: true });
fs.writeFileSync(logFile, "", "utf8");
try { fs.rmSync(stopFile, { force: true }); } catch { /* 忽略 */ }
emit("BOOT", `flake 监控采样器启动（runtime=${runtimeDir}，端口 ${PORT}，500ms 采样）`);
emit("BOOT", "停止方式：touch tests/stress/diagnostics/flake-monitor-logs/flake-monitor.stop 或 Ctrl+C");

const sampleTimer = setInterval(() => {
  if (fs.existsSync(stopFile)) {
    emit("STOP", "检测到 stop 信号文件，采样器退出");
    try { fs.rmSync(stopFile, { force: true }); } catch { /* 忽略 */ }
    clearInterval(sampleTimer);
    process.exit(0);
  }
  onLine(sample());
}, 500);

function stop() {
  clearInterval(sampleTimer);
  process.exit(0);
}

process.on("SIGINT", stop);
process.on("SIGTERM", stop);
