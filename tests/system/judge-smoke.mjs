import test, { after, before } from "node:test";
import assert from "node:assert/strict";
import { spawnSync } from "node:child_process";
import {
  api,
  deleteScript,
  isAdminMode,
  isAdministrator,
  makeFixture,
  prepareRuntime,
  startRuntime,
  stopRuntime,
  waitForHistory,
  waitForService,
  waitNoRunning,
  writeBatch,
} from "./runtime-helper.mjs";

const enabled = process.env.NEXUS_SYSTEM_SMOKE === "1";
const skipReason = process.env.NEXUS_SYSTEM_SMOKE !== "1"
  ? "设置 NEXUS_SYSTEM_SMOKE=1 后运行"
  : "";
const skip = enabled ? false : skipReason;

before(async () => {
  if (!enabled) return;
  if (isAdminMode) {
    assert.ok(isAdministrator(), "管理员 System Smoke 必须在 Administrator / High Integrity 终端运行");
  }
  prepareRuntime();
  startRuntime();
  await waitForService();
});

after(async () => {
  if (enabled) await stopRuntime();
});

async function runJudge(language, code, label) {
  const fixture = makeFixture(label);
  writeBatch(fixture, [`echo ${label}-ok>>"${fixture.log}"`]);
  const response = await api("POST", "/api/scripts", {
    name: `System Smoke ${label}-${Date.now()}`,
    rootPath: fixture.dir,
    mainExe: fixture.exe,
    configPath: fixture.cfg,
    logPath: fixture.log,
    gameExe: "C:\\Windows\\System32\\PING.EXE",
    maxAttempts: 1,
    logStallTimeoutMinutes: 5,
    totalTimeoutMinutes: 120,
    judgeScriptEnabled: true,
    judgeScriptLanguage: language,
    judgeScript: code,
  });
  assert.equal(response.status, 200);
  const script = await response.json();
  try {
    const userResponse = await api("POST", `/api/scripts/${script.id}/users`, { name: "系统用户", enabled: true });
    assert.equal(userResponse.status, 200);
    const runResponse = await api("POST", "/api/dispatch/script", { scriptId: script.id });
    assert.equal(runResponse.status, 200);
    assert.equal(await waitNoRunning(), true);
    const record = await waitForHistory(script.id);
    assert.ok(record);
    assert.equal((record.finalStatus || record.FinalStatus), "success");
  } finally {
    await deleteScript(script.id);
  }
}

test("真实 JavaScript Judge 解释器边界", { skip }, async () => {
  await runJudge(
    "javascript",
    "console.log(JSON.stringify({status:'success',reason:'js-system-smoke'}));",
    "js-judge",
  );
});

const pythonAvailable = spawnSync("python", ["--version"], { stdio: "ignore", windowsHide: true }).status === 0;
test("真实 Python Judge 解释器边界", { skip: enabled && pythonAvailable ? false : (!enabled ? skipReason : "未找到 python") }, async () => {
  await runJudge(
    "python",
    "import json\nprint(json.dumps({'status':'success','reason':'python-system-smoke'}))",
    "python-judge",
  );
});
