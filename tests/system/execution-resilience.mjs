import test, { after, before } from "node:test";
import assert from "node:assert/strict";
import fs from "node:fs";
import path from "node:path";
import diagnostics from "node:diagnostics_channel";
import {
  api,
  createUserBinding,
  deleteScript,
  makeFixture,
  projectRoot,
  prepareRuntime,
  runtimeDir,
  serviceUrl,
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

const enabled = process.env.NEXUS_SYSTEM_SMOKE === "1";
const skipReason = process.env.NEXUS_SYSTEM_SMOKE !== "1"
  ? "设置 NEXUS_SYSTEM_SMOKE=1 后运行"
  : "";
const skip = enabled ? false : skipReason;
const pingExe = "C:\\Windows\\System32\\PING.EXE";
const timeScale = process.env.NEXUS_TIME_SCALE || "10";

before(async () => {
  if (!enabled) return;
  await prepareRuntime();
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
  script.testUserIds = {};
  for (const name of users) {
    const user = await createUserBinding(script.id, name, options.binding || {});
    script.testUserIds[name] = user.id;
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

async function runScript(scriptId, userName, timeoutMs = 60000, onStarted) {
  const body = { scriptId, mode: "manual" };
  if (userName) body.userName = userName;
  const dispatch = await api("POST", "/api/dispatch/script", body);
  if (!dispatch.ok) {
    assert.fail(`启动脚本失败：HTTP ${dispatch.status} ${await dispatch.text()}`);
  }
  const { runId } = await dispatch.json();
  if (onStarted) onStarted(runId);
  const finished = await waitNoRunning(timeoutMs);
  if (!finished) {
    await api("POST", "/api/cancel", { runId });
    assert.equal(await waitNoRunning(40000), true, `本用例 ${runId} 取消后仍未停止`);
  }
  assert.equal(finished, true, `脚本 ${scriptId} 未在 ${timeoutMs}ms 内结束`);
  let record = null;
  await waitFor(async () => {
    const records = await historyRecords(scriptId);
    const timestamp = item => Date.parse(item.startTime || item.StartTime || "") || 0;
    record = records.slice().sort((left, right) => timestamp(left) - timestamp(right)).at(-1) || null;
    return record !== null;
  }, 10000, 100);
  assert.ok(record, `脚本 ${scriptId} 未产生历史记录`);
  return record;
}

function recordStatus(record) {
  return record.status || record.Status;
}

function recordAttempts(record) {
  return record.attempts ?? record.Attempts;
}

function recordDetails(record) {
  return record.attemptDetails || record.AttemptDetails || [];
}

function counterJudge(secondResult, { delayMs = 0, successAt = 2, secondDelayMs = 0 } = {}) {
  const delay = delayMs > 0
    ? `const deadline = Date.now() + ${delayMs}; while (Date.now() < deadline) {}`
    : "";
  return `
const input = JSON.parse(__NEXUS_INPUT__);
const counter = (input.files || []).find(f => f.Root === "script" && f.Path === "count");
const n = Number(counter ? (nexus.readFile(counter.Abs) || "0") : "0") + 1;
nexus.writeFile("count", String(n));
if (n < ${successAt}) {
  ${delay}
  if (n === 2) { const until = Date.now() + ${secondDelayMs}; while (Date.now() < until) {} }
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

test("ER00 每日成功运行上限：达到上限后写入 skipped 且不再次启动脚本", { skip }, async () => {
  const fixture = makeFixture("er00-daily-cap");
  writeBatchCode(fixture, [`echo ER00-RUN>>"${fixture.log}"`]);
  const script = await createScript(fixture, {
    name: "ER00 Daily Cap",
    binding: { maxSuccessfulRunsPerDay: 1 },
  });
  try {
    const first = await runScript(script.id);
    assert.equal(recordStatus(first), "success", JSON.stringify({
      resultCode: first.resultCode || first.ResultCode,
      resultDetail: first.resultDetail || first.ResultDetail,
      attempts: recordDetails(first),
    }));
    const second = await runScript(script.id);
    assert.equal(recordStatus(second), "skipped");
    assert.equal(recordAttempts(second), 0);
    assert.match(second.resultDetail || second.ResultDetail || "", /最多成功运行次数/);
    const log = fs.existsSync(fixture.log) ? fs.readFileSync(fixture.log, "utf8") : "";
    assert.equal((log.match(/ER00-RUN/g) || []).length, 1, "达到上限后脚本进程未再次启动");
  } finally {
    await deleteScript(script.id);
  }
});

test("ER01 Batch Judge → Final Judge：进程退出后的最终判定仍执行", { skip }, async () => {
  const fixture = makeFixture("er01-batch-final");
  // Keep the script alive until the first Judge writes its observable counter;
  // a fixed delay can end before the batch Judge runs on a loaded CI runner.
  writeBatchCode(fixture, [
    "cd /d \"%~dp0\"",
    `echo ER01-BATCH>>\"${fixture.log}\"`,
    "set /a judgeWait=0",
    ":wait-first-judge",
    "if exist \"%~dp0count\" goto :judge-observed",
    "set /a judgeWait+=1",
    "if %judgeWait% GEQ 20 exit /b 42",
    "ping -n 2 127.0.0.1 >nul",
    "goto :wait-first-judge",
    ":judge-observed",
  ]);
  const script = await createScript(fixture, {
    name: "ER01 Batch Final",
    judgeScriptEnabled: true,
    judgeScript: counterJudge("ER01-final"),
  });
  try {
    const record = await runScript(script.id);
    assert.equal(recordAttempts(record), 1);
    assert.equal(recordStatus(record), "success", JSON.stringify({
      resultCode: record.resultCode || record.ResultCode,
      resultDetail: record.resultDetail || record.ResultDetail,
      attempts: recordDetails(record),
    }));
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
    let runId;
    const record = await runScript(script.id, undefined, 60000, id => { runId = id; });
    assert.equal(recordAttempts(record), 2);
    assert.equal(recordStatus(record), "success");
    const detail = await api("GET", `/api/dispatch/${runId}`);
    assert.equal(detail.status, 200);
    const current = await detail.json();
    assert.match(current.logSegmentId, /:attempt:2$/);
    assert.equal(current.logSegment?.id, current.logSegmentId);
    assert.equal(current.logSegment?.attemptNumber, 2);
    assert.equal(current.logSegment?.generation, current.logSegmentSequence);
    assert.equal(current.logSegment?.runRecordId, record.id || record.Id);
    assert.ok(current.logSegmentSequence >= 3, "准备段和两次 attempt 应各有独立代际");
    assert.doesNotMatch((current.logTail || []).join("\n"), /ER04-FAIL/);
    assert.match((current.logTail || []).join("\n"), /ER04-SUCCESS/);
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
    assert.equal(recordStatus(record), "success");
    assert.equal(fs.readFileSync(pathJoin(fixture.cfg, "tasks.txt"), "utf8").trim(), "FAIL");
    assert.equal(fs.existsSync(pathJoin(fixture.dir, "logs", "run-old.log")), true);
    assert.match(recordDetails(record).map(item => item.reason || "").join(" | "), /ER06-rotation-success/);
  } finally {
    await deleteScript(script.id);
  }
});

test("ER11 Judge partial：明确部分完成时终止且不重试", { skip }, async () => {
  const fixture = makeFixture("er11-judge-partial");
  writeBatchCode(fixture, [
    "cd /d \"%~dp0\"",
    `echo ER11-PARTIAL>>\"${fixture.log}\"`,
    "ping -n 5 127.0.0.1 >nul",
  ]);
  const script = await createScript(fixture, {
    name: "ER11 Judge Partial",
    maxAttempts: 2,
    judgeScriptEnabled: true,
    judgeScript: `
const input = JSON.parse(__NEXUS_INPUT__);
if ((input.log || "").includes("ER11-PARTIAL")) {
  console.log(JSON.stringify({ status: "partial", reason: "ER11-partial" }));
}`,
  });
  try {
    const record = await runScript(script.id, undefined, 60000);
    assert.equal(recordAttempts(record), 1);
    assert.equal(recordStatus(record), "partial");
    assert.equal(record.status || record.Status, "partial");
    assert.match(recordDetails(record).map(item => item.reason || item.Reason || "").join(" | "), /ER11-partial/);
  } finally {
    await deleteScript(script.id);
  }
});

test("ER12 Judge success：日志中的 ERROR 不改变最终状态", { skip }, async () => {
  const fixture = makeFixture("er12-judge-success-error-log");
  writeBatchCode(fixture, [
    "cd /d \"%~dp0\"",
    `echo ERROR ER12-SYNTHETIC>>\"${fixture.log}\"`,
    "ping -n 5 127.0.0.1 >nul",
  ]);
  const script = await createScript(fixture, {
    name: "ER12 Judge Success Error Log",
    judgeScriptEnabled: true,
    judgeScript: `
const input = JSON.parse(__NEXUS_INPUT__);
if ((input.log || "").includes("ER12-SYNTHETIC")) {
  console.log(JSON.stringify({ status: "success", reason: "ER12-success" }));
}`,
  });
  try {
    const record = await runScript(script.id, undefined, 60000);
    assert.equal(recordAttempts(record), 1);
    assert.equal(recordStatus(record), "success");
    assert.equal(record.status || record.Status, "success");
    assert.equal(record.status || record.Status, "success");
  } finally {
    await deleteScript(script.id);
  }
});

test("ER13 Partial 不消耗每日成功额度", { skip }, async () => {
  const fixture = makeFixture("er13-partial-daily-cap");
  const statePath = pathJoin(fixture.dir, "state.txt");
  fs.writeFileSync(statePath, "0\r\n", "ascii");
  writeBatchCode(fixture, [
    "cd /d \"%~dp0\"",
    `set /p RUN=<\"${statePath}\"`,
    `if \"%RUN%\"==\"0\" (>\"${statePath}\" echo 1 & echo ER13-PARTIAL>>\"${fixture.log}\") else (echo ER13-SUCCESS>>\"${fixture.log}\")`,
  ]);
  const script = await createScript(fixture, {
    name: "ER13 Partial Daily Cap",
    maxAttempts: 1,
    binding: { maxSuccessfulRunsPerDay: 1 },
    judgeScriptEnabled: true,
    judgeScript: `
const input = JSON.parse(__NEXUS_INPUT__);
const log = input.log || "";
if (log.includes("ER13-PARTIAL")) {
  console.log(JSON.stringify({ status: "partial", reason: "ER13-partial" }));
} else if (log.includes("ER13-SUCCESS")) {
  console.log(JSON.stringify({ status: "success", reason: "ER13-success" }));
}`,
  });
  try {
    const partial = await runScript(script.id, undefined, 60000);
    assert.equal(recordStatus(partial), "partial");
    const success = await runScript(script.id, undefined, 60000);
    assert.equal(recordStatus(success), "success");
    const skipped = await runScript(script.id, undefined, 60000);
    assert.equal(recordStatus(skipped), "skipped");
    assert.equal(recordAttempts(skipped), 0);
    assert.equal(fs.readFileSync(statePath, "utf8").trim(), "1");
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
    const userId = script.testUserIds[user];
    const editPath = `/api/users/${encodeURIComponent(userId)}/bindings/${encodeURIComponent(script.id)}/edit-config`;
    // v0.12.8：无快照的首次编辑用 fresh（原目录整体入缓存区，done 后还原为编辑前内容）；有快照走既有交换
    const status = await (await api("GET", editPath)).json();
    const start = await api("POST", editPath, { action: "start", ...(status && status.hasSnapshot ? {} : { mode: "fresh" }) });
    if (!start.ok) {
      assert.fail(`用户配置编辑启动失败：HTTP ${start.status} ${await start.text()}`);
    }
    if (!status.hasSnapshot) {
      // fresh 已把原配置目录移入缓存区：模拟脚本启动时重建默认配置目录，再写入编辑后的值
      fs.mkdirSync(fixture.cfg, { recursive: true });
    }
    fs.writeFileSync(value, text, "ascii");
    const done = await api("POST", editPath, { action: "done" });
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
    "ping -n 120 127.0.0.1 >nul",
  ]);
  const script = await createScript(fixture, {
    name: "ER10 Stall Final",
    logStallTimeoutMinutes: 1,
    judgeScriptEnabled: true,
    // Batch call is pending; the 30-second periodic call is also pending.
    // Its brief delay separates the next periodic deadline from the 60-second
    // input deadline, so the third call is the final stall decision.
    judgeScript: counterJudge("ER10-stall-final", { successAt: 3, secondDelayMs: 2000 / Number(timeScale) }),
  });
  try {
    const startedAt = performance.now();
    const record = await runScript(script.id, undefined, 90000);
    const elapsedMs = performance.now() - startedAt;
    const finalReason = recordDetails(record).map(item => item.reason || "").join(" | ");
    fs.writeFileSync(path.join(runtimeDir, "stall-final-evidence.json"), JSON.stringify({
      caseId: "ER10", timeScale: Number(timeScale), elapsedMs, record,
      judgeCalls: Number(finalReason.match(/ER10-stall-final-(\d+)/)?.[1] ?? 0),
    }, null, 2));
    assert.equal(recordAttempts(record), 1);
    assert.equal(recordStatus(record), "success");
    assert.match(finalReason, /ER10-stall-final-[34]/);
    assert.ok(elapsedMs >= 60000 / Number(timeScale) && elapsedMs < 90000,
      `ER10 did not terminate at the input stall boundary: ${elapsedMs}ms`);
  } finally {
    await deleteScript(script.id);
  }
});

test("EX01 人工取消：30 次独立运行均停止当前脚本且不派发第二个用户", { skip }, async () => {
  const fixture = makeFixture("ex01-cancel-users");
  const marker = path.join(fixture.dir, "started.txt");
  const after = path.join(fixture.dir, "after.txt");
  writeBatchCode(fixture, [
    `echo started>>"${marker}"`,
    "ping -n 30 127.0.0.1 >nul",
    `echo completed>>"${after}"`,
  ]);
  const script = await createScript(fixture, {
    name: "EX01 Cancel Users",
    users: ["EX01-A", "EX01-B"],
    maxAttempts: 2,
  });
  let runId;
  const samples = [];
  let passed = false;
  try {
    for (let sample = 0; sample < 33; sample++) {
      const measured = { sample, warmup: sample < 3, completed: false };
      samples.push(measured);
      fs.rmSync(marker, { force: true });
      fs.rmSync(after, { force: true });
      const started = await api("POST", "/api/dispatch/script", { scriptId: script.id, mode: "manual" });
      assert.equal(started.status, 200);
      ({ runId } = await started.json());
      assert.equal(await waitFor(() => fs.existsSync(marker), 15000, 25), true, `第 ${sample + 1} 次首项未开始`);
      // Resolve the fixture's port file outside the HTTP response interval,
      // just as the application already knows the service URL. Retain the
      // lookup cost separately rather than charging test filesystem I/O to HTTP.
      const lookupAt = performance.now();
      const cancelUrl = serviceUrl();
      measured.fixtureUrlLookupMs = performance.now() - lookupAt;
      const acceptedAt = performance.now();
      measured.requestedAtUtc = new Date().toISOString();
      const observers = ["create", "bodySent", "headers", "trailers"].map(stage => {
        const channel = diagnostics.channel("undici:request:" + stage);
        const observe = ({ request }) => {
          if (request?.path === "/api/cancel") measured["transport" + stage + "Ms"] = performance.now() - acceptedAt;
        };
        channel.subscribe(observe); return { channel, observe };
      });
      let cancel;
      try {
        cancel = await api("POST", "/api/cancel", { runId }, cancelUrl);
        measured.responseHeadersMs = performance.now() - acceptedAt;
        measured.serverTiming = cancel.headers.get("server-timing");
        assert.equal(cancel.status, 200);
        assert.equal((await cancel.json()).cancellation, "accepted");
      } finally {
        observers.forEach(({ channel, observe }) => channel.unsubscribe(observe));
      }
      const responseMs = performance.now() - acceptedAt;
      const repeated = await api("POST", "/api/cancel", { runId });
      assert.equal(repeated.status, 200);
      assert.match((await repeated.json()).cancellation, /already_requested|already_finished/);
      assert.equal(await waitNoRunning(15000), true, `第 ${sample + 1} 次取消后未结束`);
      const terminalMs = performance.now() - acceptedAt;
      const detail = await api("GET", `/api/dispatch/${runId}`);
      assert.equal(detail.status, 200);
      const snapshot = await detail.json();
      assert.equal(snapshot.status || snapshot.Status, "cancelled");
      assert.equal(snapshot.cancelRequested, true);
      const timing = snapshot.cancellationTimingMs;
      assert.ok(timing, "缺少单调时钟取消阶段计时");
      const stopIssuedMs = timing.stopIssued ?? timing.StopIssued;
      const ownedExitedMs = timing.ownedProcessesExited ?? timing.OwnedProcessesExited;
      const workersQuiescedMs = timing.workersQuiesced ?? timing.WorkersQuiesced;
      const resultCommittedMs = timing.resultCommitted ?? timing.ResultCommitted;
      assert.ok(Number.isFinite(stopIssuedMs), "缺少停止请求时刻");
      assert.ok(Number.isFinite(ownedExitedMs), "缺少已确权退出时刻");
      assert.ok(Number.isFinite(workersQuiescedMs), "缺少 worker 收拢时刻");
      assert.ok(Number.isFinite(resultCommittedMs), "缺少结果提交时刻");
      assert.ok(stopIssuedMs <= ownedExitedMs && ownedExitedMs <= workersQuiescedMs && workersQuiescedMs <= resultCommittedMs);
      assert.equal((snapshot.records || []).length, 1, "取消后仍派发了下一用户");
      assert.equal(fs.readFileSync(marker, "utf8").trim(), "started");
      assert.equal(fs.existsSync(after), false, "当前脚本取消后仍运行至末尾");
      Object.assign(measured, { completed: true, responseMs: Math.round(responseMs * 10) / 10,
        terminalMs: Math.round(terminalMs * 10) / 10,
        stopIssuedMs, ownedExitedMs, workersQuiescedMs, resultCommittedMs });
    }
    const metric = key => {
      const ordered = samples.filter(item => !item.warmup).map(item => item[key]).sort((a, b) => a - b);
      return { p50: ordered[14], p95: ordered[28], max: ordered[29] };
    };
    console.log(`EX01 cancellation samples=${samples.length} raw=${path.join(runtimeDir, "cancellation-performance.json")} response=${JSON.stringify(metric("responseMs"))} terminal=${JSON.stringify(metric("terminalMs"))} stopIssued=${JSON.stringify(metric("stopIssuedMs"))} ownedExited=${JSON.stringify(metric("ownedExitedMs"))} workersQuiesced=${JSON.stringify(metric("workersQuiescedMs"))}`);
    assert.ok(metric("responseMs").p95 <= 500, "EX01 取消 HTTP 响应 P95 超过 500ms");
    assert.ok(metric("stopIssuedMs").p95 <= 500, "EX01 停止请求 P95 超过 500ms");
    assert.ok(metric("ownedExitedMs").p95 <= 2000, "EX01 已确权退出 P95 超过 2s");
    passed = true;
  } finally {
    fs.writeFileSync(path.join(runtimeDir, "cancellation-performance.json"), JSON.stringify({
      caseId: "EX01", passed, timeScale: Number(timeScale), warmupCount: 3, sampleCount: 30,
      samples, failures: passed ? 0 : 1,
    }, null, 2));
    if (runId) {
      await api("POST", "/api/cancel", { runId });
      await waitNoRunning(40000);
    }
    await deleteScript(script.id);
  }
});

for (const channel of ["stdout", "stderr"]) {
  test(`ER14 ${channel} only：首次输出后的沉默仍触发无日志超时`, { skip }, async () => {
    const fixture = makeFixture(`er14-${channel}-stall`);
    writeBatchCode(fixture, [
      channel === "stderr" ? "echo ER14-START 1>&2" : "echo ER14-START",
      "ping -n 120 127.0.0.1 >nul",
    ]);
    const script = await createScript(fixture, {
      name: `ER14 ${channel} stall`,
      // The script contract requires a plausible log path and game executable.
      // Leave the log file absent so stdout/stderr is the sole input source.
      logPath: fixture.log,
      gameExe: pingExe,
      logStallTimeoutMinutes: 1,
      totalTimeoutMinutes: 5,
    });
    try {
      const record = await runScript(script.id, undefined, 90000);
      assert.equal(recordStatus(record), "failed");
      assert.match(recordDetails(record).map(item => item.reason || "").join(" | "), /日志超过 .*无新增输入/);
    } finally {
      await deleteScript(script.id);
    }
  });
}
