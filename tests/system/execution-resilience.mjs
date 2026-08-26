import test, { after, before } from "node:test";
import assert from "node:assert/strict";
import fs from "node:fs";
import path from "node:path";
import {
  api,
  deleteScript,
  isElevated,
  makeFixture,
  prepareRuntime,
  runtimeDir,
  sleep,
  startRuntime,
  stopRuntime,
  waitFor,
  waitForService,
  waitNoRunning,
} from "./runtime-helper.mjs";
import {
  isProcessAlive,
  killProcessTree,
  readPidFile,
} from "../support/windows-process.mjs";

/*
 * Execution Resilience System Suite
 *
 * This is the small deterministic process/filesystem/async-state-machine gate
 * extracted from the former Judge/Chaos harness. Each case has one explicit
 * scenario and uses no random seed or retry-to-pass behavior.
 */

const enabled = process.env.NEXUS_SYSTEM_SMOKE === "1" && isElevated();
const skipReason = process.env.NEXUS_SYSTEM_SMOKE !== "1"
  ? "设置 NEXUS_SYSTEM_SMOKE=1 后运行"
  : "System Smoke 需要管理员终端";
const skip = enabled ? false : skipReason;
const pingExe = "C:\\Windows\\System32\\PING.EXE";
const timeScale = process.env.NEXUS_TIME_SCALE || "10";

before(async () => {
  if (!enabled) return;
  prepareRuntime();
  startRuntime([], { NEXUS_TIME_SCALE: timeScale });
  await waitForService();
});

after(async () => {
  if (enabled) await stopRuntime();
});

function writeBatchCode(fixture, lines, exitCode = 0) {
  fs.writeFileSync(
    fixture.exe,
    ["@echo off", "setlocal", ...lines, `exit /b ${exitCode}`, ""].join("\r\n"),
    "ascii",
  );
}

async function createScript(fixture, options = {}) {
  const response = await api("POST", "/api/scripts", {
    name: options.name || `Execution Resilience ${Date.now()}`,
    rootPath: fixture.dir,
    mainExe: fixture.exe,
    configPath: options.configPath ?? fixture.cfg,
    logPath: options.logPath ?? fixture.log,
    gameExe: options.gameExe ?? pingExe,
    gameArgs: options.gameArgs ?? "",
    forceCloseGame: options.forceCloseGame ?? false,
    maxAttempts: options.maxAttempts ?? 1,
    logStallTimeoutMinutes: options.logStallTimeoutMinutes ?? 5,
    totalTimeoutMinutes: options.totalTimeoutMinutes ?? 5,
    successKeywords: options.successKeywords ?? "",
    failureKeywords: options.failureKeywords ?? "",
    judgeScriptEnabled: options.judgeScriptEnabled ?? false,
    judgeScriptLanguage: options.judgeScriptLanguage ?? "javascript",
    judgeScript: options.judgeScript ?? "",
    autoUpdateConfig: false,
    ...options.extra,
  });
  if (!response.ok) {
    assert.fail(`创建脚本失败：HTTP ${response.status} ${await response.text()}`);
  }
  const script = await response.json();
  const users = options.users ?? ["ER-system-user"];
  for (const name of users) {
    const userResponse = await api("POST", `/api/scripts/${script.id}/users`, {
      name,
      enabled: true,
    });
    if (!userResponse.ok) {
      assert.fail(`添加用户失败：HTTP ${userResponse.status} ${await userResponse.text()}`);
    }
  }
  return script;
}

async function historyRecords(scriptId) {
  const response = await api("GET", "/api/history?days=7&offset=0&limit=200");
  if (!response.ok) return [];
  const payload = await response.json();
  const records = Array.isArray(payload) ? payload : payload.records;
  return (records || []).filter(item =>
    (item.scriptInstanceId || item.ScriptInstanceId) === scriptId);
}

async function runScript(scriptId, userName, timeoutMs = 60000) {
  const body = { scriptId, mode: "manual" };
  if (userName) body.userName = userName;
  const dispatch = await api("POST", "/api/dispatch/script", body);
  if (!dispatch.ok) {
    assert.fail(`启动脚本失败：HTTP ${dispatch.status} ${await dispatch.text()}`);
  }
  assert.equal(await waitNoRunning(timeoutMs), true, `脚本 ${scriptId} 未在 ${timeoutMs}ms 内结束`);
  let record = null;
  await waitFor(async () => {
    const records = await historyRecords(scriptId);
    record = records.at(-1) || null;
    return record !== null;
  }, 10000, 100);
  assert.ok(record, `脚本 ${scriptId} 未产生历史记录`);
  return record;
}

function recordStatus(record) {
  return record.finalStatus || record.FinalStatus || record.status || record.Status;
}

function recordAttempts(record) {
  return record.attempts ?? record.Attempts;
}

function recordDetails(record) {
  return record.attemptDetails || record.AttemptDetails || [];
}

function counterJudge(secondResult, { delayMs = 0 } = {}) {
  const delay = delayMs > 0
    ? `const deadline = Date.now() + ${delayMs}; while (Date.now() < deadline) {}`
    : "";
  return `
const input = JSON.parse(__NEXUS_INPUT__);
const counter = (input.files || []).find(f => f.Root === "script" && f.Path === "count");
const n = Number(counter ? (nexus.readFile(counter.Abs) || "0") : "0") + 1;
nexus.writeFile("count", String(n));
if (n === 1) {
  ${delay}
  console.log("pending");
} else {
  console.log(JSON.stringify({ status: "success", reason: ${JSON.stringify(secondResult)} + "-" + n }));
}`;
}

function pathJoin(...parts) {
  return parts.join("\\");
}

function managerLogText() {
  const now = new Date();
  const stamp = `${now.getFullYear()}-${String(now.getMonth() + 1).padStart(2, "0")}-${String(now.getDate()).padStart(2, "0")}`;
  const file = pathJoin(runtimeDir, "logs", `nexus-pipeline-${stamp}.log`);
  return fs.existsSync(file) ? fs.readFileSync(file, "utf8") : "";
}

test("ER01 Batch Judge → Final Judge：进程退出后的最终判定仍执行", { skip }, async () => {
  const fixture = makeFixture("er01-batch-final");
  writeBatchCode(fixture, [
    "cd /d \"%~dp0\"",
    `echo ER01-BATCH>>\"${fixture.log}\"`,
    "ping -n 5 127.0.0.1 >nul",
  ]);
  const script = await createScript(fixture, {
    name: "ER01 Batch Final",
    judgeScriptEnabled: true,
    judgeScript: counterJudge("ER01-final"),
  });
  try {
    const record = await runScript(script.id);
    assert.equal(recordAttempts(record), 1);
    assert.equal(recordStatus(record), "success");
    assert.match(recordDetails(record).map(item => item.reason || "").join(" | "), /ER01-final-2/);
  } finally {
    await deleteScript(script.id);
  }
});

test("ER02 Periodic Judge 与 exit 同轮：跳过重复 final", { skip }, async () => {
  const fixture = makeFixture("er02-periodic-exit");
  writeBatchCode(fixture, [
    "cd /d \"%~dp0\"",
    `echo ER02-BATCH>>\"${fixture.log}\"`,
    "ping -n 12 127.0.0.1 >nul",
  ]);
  const script = await createScript(fixture, {
    name: "ER02 Periodic Exit",
    judgeScriptEnabled: true,
    judgeScript: counterJudge("ER02-periodic"),
  });
  try {
    const record = await runScript(script.id, undefined, 60000);
    assert.equal(recordStatus(record), "success");
    assert.match(recordDetails(record).map(item => item.reason || "").join(" | "), /ER02-periodic-2/);
    assert.match(recordDetails(record).at(-1)?.reason || "", /ER02-periodic/);
  } finally {
    await deleteScript(script.id);
  }
});

test("ER03 stale Judge generation：慢结果不会覆盖终局结果", { skip }, async () => {
  const fixture = makeFixture("er03-stale-generation");
  writeBatchCode(fixture, [
    "cd /d \"%~dp0\"",
    `echo ER03-BATCH>>\"${fixture.log}\"`,
    "ping -n 8 127.0.0.1 >nul",
  ]);
  const script = await createScript(fixture, {
    name: "ER03 Stale Generation",
    judgeScriptEnabled: true,
    judgeScript: counterJudge("ER03-final", { delayMs: 1500 }),
  });
  try {
    const record = await runScript(script.id, undefined, 60000);
    assert.equal(recordStatus(record), "success");
    assert.match(recordDetails(record).map(item => item.reason || "").join(" | "), /ER03-final-2/);
    assert.match(recordDetails(record).at(-1)?.reason || "", /ER03-final/);
  } finally {
    await deleteScript(script.id);
  }
});

test("ER04 replaceConfigs retry：失败尝试完整退出后替换并重试", { skip }, async () => {
  const fixture = makeFixture("er04-replace-retry");
  fs.writeFileSync(pathJoin(fixture.cfg, "tasks.txt"), "FAIL\r\n", "ascii");
  writeBatchCode(fixture, [
    "cd /d \"%~dp0\"",
    `findstr /c:\"FAIL\" \"%~dp0cfg\\tasks.txt\" >nul && echo ER04-FAIL>>\"${fixture.log}\"`,
    `findstr /c:\"SUCCESS\" \"%~dp0cfg\\tasks.txt\" >nul && echo ER04-SUCCESS>>\"${fixture.log}\"`,
  ]);
  const judge = `
const input = JSON.parse(__NEXUS_INPUT__);
const config = (input.files || []).find(f => f.Root === "config" && f.Path === "tasks.txt");
const text = config ? (nexus.readFile(config.Abs) || "") : "";
if (text.includes("FAIL")) {
  nexus.writeFile("tasks.txt", "SUCCESS");
  console.log(JSON.stringify({ status: "failed", reason: "ER04-retry", replaceConfigs: ["tasks.txt"] }));
} else {
  console.log(JSON.stringify({ status: "success", reason: "ER04-success" }));
}`;
  const script = await createScript(fixture, {
    name: "ER04 Replace Retry",
    configPath: fixture.cfg,
    maxAttempts: 2,
    judgeScriptEnabled: true,
    judgeScript: judge,
  });
  try {
    const record = await runScript(script.id, undefined, 60000);
    assert.equal(recordAttempts(record), 2);
    assert.equal(recordStatus(record), "partial");
    assert.equal(fs.readFileSync(pathJoin(fixture.cfg, "tasks.txt"), "utf8").trim(), "FAIL");
    assert.match(recordDetails(record).map(item => item.reason || "").join(" | "), /ER04-retry/);
  } finally {
    await deleteScript(script.id);
  }
});

test("ER05 Marker grace：成功标记后继续输出不会重新判定", { skip }, async () => {
  const fixture = makeFixture("er05-marker-grace");
  writeBatchCode(fixture, [
    "cd /d \"%~dp0\"",
    `echo ER05-MARKER>>\"${fixture.log}\"`,
    "ping -n 12 127.0.0.1 >nul",
    `echo ER05-AFTER>>\"${fixture.log}\"`,
  ]);
  const script = await createScript(fixture, {
    name: "ER05 Marker Grace",
    successKeywords: "ER05-MARKER",
    gameExe: pingExe,
  });
  try {
    const record = await runScript(script.id, undefined, 60000);
    assert.equal(recordAttempts(record), 1);
    assert.equal(recordStatus(record), "success");
    assert.equal(await waitNoRunning(5000), true);
  } finally {
    await deleteScript(script.id);
  }
});

test("ER06 Log rotation isolation：新 attempt 不读取旧日志", { skip }, async () => {
  const fixture = makeFixture("er06-log-rotation");
  fs.writeFileSync(pathJoin(fixture.cfg, "tasks.txt"), "FAIL\r\n", "ascii");
  writeBatchCode(fixture, [
    "cd /d \"%~dp0\"",
    `findstr /c:\"FAIL\" \"%~dp0cfg\\tasks.txt\" >nul && echo ER06-FAIL>>\"${fixture.log}\"`,
    `findstr /c:\"SUCCESS\" \"%~dp0cfg\\tasks.txt\" >nul && (move /y \"${fixture.log}\" \"${fixture.dir}\\logs\\run-old.log\" >nul & echo ER06-SUCCESS>>\"${fixture.log}\")`,
  ]);
  const judge = `
const input = JSON.parse(__NEXUS_INPUT__);
const log = input.log || "";
if (log.includes("ER06-FAIL")) {
  nexus.writeFile("tasks.txt", "SUCCESS");
  console.log(JSON.stringify({ status: "failed", reason: "ER06-rotation-retry", replaceConfigs: ["tasks.txt"] }));
} else if (log.includes("ER06-SUCCESS")) {
  console.log(JSON.stringify({ status: "success", reason: "ER06-rotation-success" }));
} else {
  console.log("pending");
}`;
  const script = await createScript(fixture, {
    name: "ER06 Log Rotation",
    configPath: fixture.cfg,
    maxAttempts: 2,
    judgeScriptEnabled: true,
    judgeScript: judge,
  });
  try {
    const record = await runScript(script.id, undefined, 60000);
    assert.equal(recordAttempts(record), 2);
    assert.equal(recordStatus(record), "partial");
    assert.equal(fs.readFileSync(pathJoin(fixture.cfg, "tasks.txt"), "utf8").trim(), "FAIL");
    assert.equal(fs.existsSync(pathJoin(fixture.dir, "logs", "run-old.log")), true);
    assert.match(recordDetails(record).map(item => item.reason || "").join(" | "), /ER06-rotation-success/);
  } finally {
    await deleteScript(script.id);
  }
});

test("ER07 Script crash + cleanup：异常退出后清理子进程并保留正确历史", { skip }, async () => {
  const fixture = makeFixture("er07-script-crash");
  const childPidPath = path.join(fixture.dir, "long-lived-child.pid");
  const childFixture = path.join(projectRoot, "tests", "system", "fixtures", "long-lived-child.mjs");
  writeBatchCode(fixture, [
    "cd /d \"%~dp0\"",
    `start \"\" /b \"${process.execPath}\" \"${childFixture}\" \"${childPidPath}\"`,
    `echo ER07-START>>\"${fixture.log}\"`,
  ], 1);
  const script = await createScript(fixture, {
    name: "ER07 Script Crash",
    judgeScriptEnabled: true,
    judgeScript: `
const input = JSON.parse(__NEXUS_INPUT__);
if ((input.log || "").includes("ER07-START")) {
  console.log(JSON.stringify({ status: "failed", reason: "ER07-script-crash" }));
} else {
  console.log("pending");
}`,
  });
  try {
    const record = await runScript(script.id, undefined, 60000);
    assert.equal(recordStatus(record), "failed");
    assert.match(recordDetails(record).map(item => item.reason || "").join(" | "), /ER07/);
    let childPid = null;
    await waitFor(() => {
      childPid = readPidFile(childPidPath);
      return childPid !== null;
    }, 10000, 200);
    assert.ok(childPid, "ER07 fixture 未写入 child PID");
    assert.equal(await waitFor(() => !isProcessAlive(childPid), 10000, 200), true);
  } finally {
    const childPid = readPidFile(childPidPath);
    if (childPid) killProcessTree(childPid);
    await deleteScript(script.id);
  }
});

test("ER08 Game crash：游戏子进程清理与重试边界保持正确", { skip }, async () => {
  const fixture = makeFixture("er08-game-crash");
  writeBatchCode(fixture, [
    "cd /d \"%~dp0\"",
    `start \"\" /b \"${pingExe}\" -n 60 127.0.0.1 >nul`,
    `echo ER08-GAME-CRASH>>\"${fixture.log}\"`,
  ], 1);
  const script = await createScript(fixture, {
    name: "ER08 Game Crash",
    gameExe: pingExe,
    forceCloseGame: true,
    judgeScriptEnabled: true,
    judgeScript: `
const input = JSON.parse(__NEXUS_INPUT__);
if ((input.log || "").includes("ER08-GAME-CRASH")) {
  console.log(JSON.stringify({ status: "failed", reason: "ER08-game-crash" }));
} else {
  console.log("pending");
}`,
  });
  try {
    const record = await runScript(script.id, undefined, 60000);
    assert.equal(recordStatus(record), "failed");
    assert.match(recordDetails(record).map(item => item.reason || "").join(" | "), /ER08/);
    assert.match(managerLogText(), /强制结束游戏/);
  } finally {
    await deleteScript(script.id);
  }
});

test("ER09 Multi-user Config Isolation：两个用户的 store 与运行输入不串", { skip }, async () => {
  const fixture = makeFixture("er09-multi-user");
  const value = pathJoin(fixture.cfg, "value.txt");
  fs.writeFileSync(value, "BASE", "ascii");
  writeBatchCode(fixture, [
    "cd /d \"%~dp0\"",
    `type \"%~dp0cfg\\value.txt\">>\"${fixture.log}\"`,
  ]);
  const script = await createScript(fixture, {
    name: "ER09 Multi User",
    configPath: fixture.cfg,
    users: ["ER09-A", "ER09-B"],
  });
  async function editUser(user, text) {
    const start = await api("POST", `/api/scripts/${script.id}/users/${encodeURIComponent(user)}/edit-config`, { action: "start" });
    if (!start.ok) {
      assert.fail(`用户配置编辑启动失败：HTTP ${start.status} ${await start.text()}`);
    }
    fs.writeFileSync(value, text, "ascii");
    const done = await api("POST", `/api/scripts/${script.id}/users/${encodeURIComponent(user)}/edit-config`, { action: "done" });
    if (!done.ok) {
      assert.fail(`用户配置编辑提交失败：HTTP ${done.status} ${await done.text()}`);
    }
    assert.equal(fs.readFileSync(value, "utf8"), "BASE");
  }
  try {
    await editUser("ER09-A", "USER-A");
    await editUser("ER09-B", "USER-B");
    await runScript(script.id, "ER09-A");
    await runScript(script.id, "ER09-B");
    const log = fs.readFileSync(fixture.log, "utf8");
    assert.match(log, /USER-A/);
    assert.match(log, /USER-B/);
    assert.equal(fs.readFileSync(value, "utf8"), "BASE");
  } finally {
    await deleteScript(script.id);
  }
});

test("ER10 stall/final Judge：日志停滞后终局判断仍能完成", { skip }, async () => {
  const fixture = makeFixture("er10-stall-final");
  writeBatchCode(fixture, [
    "cd /d \"%~dp0\"",
    `echo ER10-START>>\"${fixture.log}\"`,
    "ping -n 30 127.0.0.1 >nul",
  ]);
  const script = await createScript(fixture, {
    name: "ER10 Stall Final",
    logStallTimeoutMinutes: 1,
    judgeScriptEnabled: true,
    judgeScript: counterJudge("ER10-stall-final"),
  });
  try {
    const record = await runScript(script.id, undefined, 60000);
    assert.equal(recordAttempts(record), 1);
    assert.equal(recordStatus(record), "success");
    assert.match(recordDetails(record).map(item => item.reason || "").join(" | "), /ER10-stall-final-2/);
  } finally {
    await deleteScript(script.id);
  }
});
