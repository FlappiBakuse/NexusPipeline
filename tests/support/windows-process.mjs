import { spawnSync } from "node:child_process";
import fs from "node:fs";

function run(command, args) {
  try {
    return spawnSync(command, args, {
      encoding: "utf8",
      windowsHide: true,
    });
  } catch {
    return { status: null, stdout: "", stderr: "" };
  }
}

/** Windows integrity levels are represented by locale-independent SIDs. */
export function getIntegrityLevel() {
  if (process.platform !== "win32") return "Unknown";
  const result = run("whoami", ["/groups"]);
  const output = `${result.stdout || ""}\n${result.stderr || ""}`;
  if (result.status !== 0) return "Unknown";
  if (/S-1-16-16384\b/i.test(output)) return "System";
  if (/S-1-16-12288\b/i.test(output)) return "High";
  if (/S-1-16-8192\b/i.test(output)) return "Medium";
  if (/S-1-16-4096\b/i.test(output)) return "Low";
  if (/S-1-16-0\b/i.test(output)) return "Untrusted";
  return "Unknown";
}

/** Permission is diagnostic only; the runner never uses it to select or skip tests. */
export function isAdministrator() {
  const level = getIntegrityLevel();
  return level === "High" || level === "System";
}

/** Return OS-observed process identity for marker-protected cleanup. */
export function readProcessIdentity(pid) {
  const numericPid = Number(pid);
  if (!Number.isInteger(numericPid) || numericPid <= 0) return null;
  if (process.platform !== "win32") {
    try {
      return {
        pid: numericPid,
        executablePath: fs.realpathSync(`/proc/${numericPid}/exe`),
        parentPid: null,
      };
    } catch {
      return null;
    }
  }
  const script = `$p=Get-Process -Id ${numericPid} -ErrorAction SilentlyContinue; $w=Get-CimInstance Win32_Process -Filter 'ProcessId = ${numericPid}' -ErrorAction SilentlyContinue; if ($null -ne $p) { [pscustomobject]@{ pid=$p.Id; executablePath=$p.Path; startTime=$p.StartTime.ToUniversalTime().ToString('o'); parentPid=if ($null -ne $w) { [int]$w.ParentProcessId } else { 0 } } | ConvertTo-Json -Compress }`;
  const result = run("powershell.exe", ["-NoProfile", "-NonInteractive", "-Command", script]);
  if (result.status !== 0 || !String(result.stdout || "").trim()) return null;
  try {
    const identity = JSON.parse(String(result.stdout).trim());
    return Number(identity?.pid) === numericPid && typeof identity?.executablePath === "string"
      ? {
          pid: numericPid,
          executablePath: identity.executablePath,
          startTime: identity.startTime || "",
          parentPid: Number.isInteger(Number(identity.parentPid)) ? Number(identity.parentPid) : null,
        }
      : null;
  } catch {
    return null;
  }
}

export function readPidFile(filePath) {
  try {
    const pid = Number.parseInt(String(fs.readFileSync(filePath, "utf8")).trim(), 10);
    return Number.isInteger(pid) && pid > 0 ? pid : null;
  } catch {
    return null;
  }
}

export function isProcessAlive(pid) {
  if (!Number.isInteger(Number(pid)) || Number(pid) <= 0) return false;
  const numericPid = Number(pid);
  if (process.platform === "win32") {
    const result = run("tasklist", ["/FI", `PID eq ${numericPid}`, "/FO", "CSV", "/NH"]);
    const output = `${result.stdout || ""}\n${result.stderr || ""}`;
    if (result.status === 0 && new RegExp(`"${numericPid}"`).test(output)) return true;
    if (result.status === 0 && /INFO:|没有运行的任务|no tasks/i.test(output)) return false;
    const powershell = run("powershell.exe", [
      "-NoProfile",
      "-NonInteractive",
      "-Command",
      `$p=Get-Process -Id ${numericPid} -ErrorAction SilentlyContinue; if ($null -eq $p) { 'absent' } else { 'present' }`,
    ]);
    const state = String(powershell.stdout || "").trim().toLowerCase();
    if (powershell.status === 0 && state === "present") return true;
    if (powershell.status === 0 && state === "absent") return false;
  }
  try {
    process.kill(numericPid, 0);
    return true;
  } catch (error) {
    return error?.code === "EPERM";
  }
}

export function killProcessTree(pid) {
  if (!Number.isInteger(Number(pid)) || Number(pid) <= 0) return false;
  const numericPid = Number(pid);
  if (process.platform !== "win32") {
    try {
      process.kill(numericPid, "SIGKILL");
    } catch {
      // 已退出视为清理完成。
    }
    return !isProcessAlive(numericPid);
  }
  const result = run("taskkill", ["/PID", String(numericPid), "/T", "/F"]);
  return result.status === 0 || !isProcessAlive(numericPid);
}

export async function waitForExit(pid, timeoutMs = 10000, intervalMs = 100) {
  const deadline = Date.now() + timeoutMs;
  while (Date.now() < deadline) {
    if (!isProcessAlive(pid)) return true;
    await new Promise(resolve => setTimeout(resolve, intervalMs));
  }
  return !isProcessAlive(pid);
}

export function readListeningPids(port) {
  if (process.platform !== "win32") return [];
  const result = run("netstat", ["-ano", "-p", "tcp"]);
  const pids = new Set();
  for (const line of String(result.stdout || "").split(/\r?\n/)) {
    if (!line.includes(`:${port}`) || !/\bLISTENING\b|监听/i.test(line)) continue;
    const fields = line.trim().split(/\s+/);
    const pid = Number.parseInt(fields.at(-1), 10);
    if (Number.isInteger(pid) && pid > 0) pids.add(pid);
  }
  return [...pids];
}

export function readListeningPid(port) {
  return readListeningPids(port)[0] ?? null;
}
