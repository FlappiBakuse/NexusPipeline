import { spawn } from "node:child_process";
import fs from "node:fs";
import path from "node:path";
import { fileURLToPath } from "node:url";

const __dirname = path.dirname(fileURLToPath(import.meta.url));
const runtimeDir = path.join(__dirname, "runtime");
// 独立目录存放（playwright 每轮会清空 test-results/，放那里会被删）
const resultsDir = path.join(__dirname, "flake-monitor-logs");
const logFile = path.join(resultsDir, "flake-monitor.log");
const stopFile = path.join(resultsDir, "flake-monitor.stop");
const PORT = 58731;

/** 服务日志尾部（死亡事件时抓现场）：logs/nexus-pipeline-YYYY-MM-DD.log 最后 N 行。 */
function tailServiceLog(lines = 30) {
  const logsDir = path.join(runtimeDir, "logs");
  try {
    const files = fs.readdirSync(logsDir).filter(f => /^nexus-pipeline-\d{4}-\d{2}-\d{2}\.log$/.test(f)).sort();
    if (!files.length) return "（无服务日志文件）";
    const text = fs.readFileSync(path.join(logsDir, files[files.length - 1]), "utf8").replace(/^\uFEFF/, "");
    const rows = text.split(/\r?\n/).filter(Boolean);
    return rows.slice(-lines).join("\n");
  } catch (e) {
    return "（读取服务日志失败：" + e.message + "）";
  }
}

function stamp() {
  const d = new Date();
  const p = n => String(n).padStart(2, "0");
  return `${p(d.getHours())}:${p(d.getMinutes())}:${p(d.getSeconds())}.${String(d.getMilliseconds()).padStart(3, "0")}`;
}

/** 常驻 PowerShell 采样循环：每 500ms 输出一行 `pid1,pid2|True|PID`（nexus-pipeline 进程 PID 列表 | 58731 监听中 | 监听进程 PID）。
 *  按进程名 + 端口判定（测试监控进程可能无管理员权限，查不到服务进程的 ExecutablePath）。 */
const ps = spawn("powershell", ["-NoProfile", "-NonInteractive", "-Command", `
  $ErrorActionPreference = 'SilentlyContinue';
  while ($true) {
    $p = Get-Process nexus-pipeline -ErrorAction SilentlyContinue;
    $ids = ($p | ForEach-Object { $_.Id }) -join ',';
    $lst = $false;
    $owner = '';
    $c = Get-NetTCPConnection -LocalPort ${PORT} -State Listen -ErrorAction SilentlyContinue;
    if ($c) { $lst = $true; $owner = ($c | Select-Object -First 1).OwningProcess; }
    Write-Output ("{0}|{1}|{2}" -f $ids, $lst, $owner);
    Start-Sleep -Milliseconds 500;
  }
`], { stdio: ["ignore", "pipe", "inherit"] });

let buf = "";
let prevAlive = false;
let prevListen = false;
let lastPids = "";
let lastWrite = 0;

function emit(prefix, text) {
  fs.appendFileSync(logFile, `[${stamp()}] ${prefix} ${text}\n`, "utf8");
  console.log(`[flake-monitor ${stamp()}] ${prefix} ${text}`);
}

function onLine(line) {
  const parts = line.trim().split("|");
  const pids = parts[0] || "";
  const listening = parts[1] === "True";
  const alive = pids.length > 0;

  if (prevAlive && !alive) {
    emit("DEATH", `runtime nexus-pipeline 进程全部消失（此前 PID：${lastPids}）`);
    emit("DEATH-LOG", "服务日志尾部：\n" + tailServiceLog(30));
  } else if (!prevAlive && alive) {
    emit("START", `runtime nexus-pipeline 进程出现（PID：${pids}）`);
  }
  if (prevListen && !listening) {
    emit("LISTEN-LOST", `端口 ${PORT} 停止监听`);
  } else if (!prevListen && listening) {
    emit("LISTEN-UP", `端口 ${PORT} 开始监听`);
  }
  prevAlive = alive;
  prevListen = listening;
  if (alive) lastPids = pids;

  const now = Date.now();
  if (now - lastWrite >= 5000) {
    emit("TICK", `alive=${alive} pids=[${pids}] listen58731=${listening}`);
    lastWrite = now;
  }
}

ps.stdout.setEncoding("utf8");
ps.stdout.on("data", chunk => {
  buf += chunk;
  let idx;
  while ((idx = buf.indexOf("\n")) >= 0) {
    const line = buf.slice(0, idx);
    buf = buf.slice(idx + 1);
    if (line.trim()) onLine(line);
  }
});
ps.on("exit", code => {
  emit("EXIT", `采样 PowerShell 进程退出（code=${code}）`);
  process.exit(0);
});

fs.mkdirSync(resultsDir, { recursive: true });
fs.writeFileSync(logFile, "", "utf8");
try { fs.rmSync(stopFile, { force: true }); } catch { /* 忽略 */ }
emit("BOOT", `flake 监控采样器启动（runtime=${runtimeDir}，端口 ${PORT}，500ms 采样）`);
emit("BOOT", "停止方式：touch uitest/flake-monitor-logs/flake-monitor.stop 或 Ctrl+C");

const stopTimer = setInterval(() => {
  if (fs.existsSync(stopFile)) {
    emit("STOP", "检测到 stop 信号文件，采样器退出");
    try { fs.rmSync(stopFile, { force: true }); } catch { /* 忽略 */ }
    ps.kill();
  }
}, 1000);
stopTimer.unref();

process.on("SIGINT", () => ps.kill());
process.on("SIGTERM", () => ps.kill());
