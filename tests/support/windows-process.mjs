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

/** High and System are the administrator boundaries required by production. */
export function isAdministrator() {
  const level = getIntegrityLevel();
  return level === "High" || level === "System";
}

export function requireAdministrator(label = "该测试入口", command = "default") {
  if (isAdministrator()) return true;
  console.error(`[错误] ${label}需要 Administrator / High Integrity。当前终端权限不足，正式门禁未执行。请在管理员终端执行：node tests\\run.mjs ${command}`);
  return false;
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
