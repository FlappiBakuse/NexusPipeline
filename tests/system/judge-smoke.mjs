import test, { after, before } from "node:test";
import assert from "node:assert/strict";
import { spawnSync } from "node:child_process";
import fs from "node:fs";
import path from "node:path";
import { installTaskProtocolFixture, installTaskProtocolAdmissionFixture } from "./task-protocol-fixture.mjs";
import {
  api,
  createUserBinding,
  deleteScript,
  makeFixture,
  prepareRuntime,
  runtimeDir,
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
  await prepareRuntime();
  installTaskProtocolFixture(runtimeDir);
  installTaskProtocolAdmissionFixture(runtimeDir);
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
    await createUserBinding(script.id, "系统用户");
    const runResponse = await api("POST", "/api/dispatch/script", { scriptId: script.id });
    assert.equal(runResponse.status, 200);
    assert.equal(await waitNoRunning(), true);
    const record = await waitForHistory(script.id);
    assert.ok(record);
    assert.equal(record.status, "success");
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
test("专项协议真实进程：选择重试、原选择恢复、最终计数、只读预览与稳定历史", { skip }, async () => {
  const fixture = makeFixture("task-protocol");
  fixture.exe = path.join(fixture.dir, "task-run.bat");
  const configPath = path.join(fixture.cfg, "config.json");
  fs.writeFileSync(configPath, JSON.stringify({ tasks: [{ id: "a", enabled: true }, { id: "b", enabled: true }], counter: 0 }));
  const worker = path.join(fixture.dir, "task-worker.mjs");
  fs.writeFileSync(worker, `import fs from 'node:fs';
    const file=${JSON.stringify(configPath)};const config=JSON.parse(fs.readFileSync(file,'utf8'));config.counter++;
    for(const task of config.tasks.filter(t=>t.enabled)) console.log('TASK '+task.id+' '+(task.id==='b'&&config.counter===1?'FAIL':'OK'));
    fs.writeFileSync(file,JSON.stringify(config));`);
  writeBatch(fixture, [`"${process.execPath}" "${worker}"`]);
  const response = await api("POST", "/api/scripts", { name: `Task protocol ${Date.now()}`, pluginType: "task-protocol-fixture",
    rootPath: fixture.dir, gameExe: "C:\\Windows\\System32\\PING.EXE", maxAttempts: 2, totalTimeoutMinutes: 10,
    logStallTimeoutMinutes: 5, autoUpdateConfig: true });
  assert.equal(response.status, 200, await response.clone().text());
  const script = await response.json();
  try {
    const user = await createUserBinding(script.id, "Task fixture user");
    const started = await api("POST", "/api/dispatch/script", { scriptId: script.id });
    assert.equal(started.status, 200, await started.clone().text());
    assert.equal(await waitNoRunning(60000), true);
    const record = await waitForHistory(script.id);
    assert.ok(record, "Task run persisted");
    assert.equal(record.status, "success", JSON.stringify(record));
    assert.equal(record.taskReport.summary.recovered, true);
    assert.equal(record.taskReport.attemptReports.length, 2);
    assert.deepEqual(record.taskReport.attemptReports[1].selectedTaskIds, ["b"]);
    assert.ok(record.taskReport.finalTaskResults.every(t => t.status === "succeeded"));
    const store = path.join(runtimeDir, "data", script.id, user.id, "store", "config.json");
    const before = fs.readFileSync(store);
    const finalConfig = JSON.parse(before);
    assert.equal(finalConfig.counter, 2);
    assert.ok(finalConfig.tasks.every(t => t.enabled));
    const previewResponse = await api("GET", `/api/users/${user.id}/bindings/${script.id}/task-plan`);
    assert.equal(previewResponse.status, 200);
    const preview = await previewResponse.json();
    assert.equal(preview.readOnly, true, JSON.stringify(preview));
    assert.equal(preview.stale, false);
    assert.deepEqual(fs.readFileSync(store), before);
    const summary = await (await api("GET", "/api/users/task-summaries")).json();
    assert.equal(summary.find(u => u.userId === user.id).recordId, record.id);
    const detail = await (await api("GET", `/api/history/detail?id=${record.id}&metadata=true`)).json();
    assert.equal(detail.record.id, record.id);
  } finally { await deleteScript(script.id); }
});
test("1.2 重试准入阻断保留真实运行和前置钩子", { skip }, async () => {
  const fixture = makeFixture("retry-admission");
  fixture.exe = path.join(fixture.dir, "task-run.bat");
  const configPath = path.join(fixture.cfg, "config.json");
  fs.writeFileSync(configPath, JSON.stringify({ tasks: [{ id: "a", enabled: true }, { id: "b", enabled: true }],
    counter: 0, armBlock: false, blocked: false, hookCount: 0 }));
  const worker = path.join(fixture.dir, "task-worker.mjs");
  fs.writeFileSync(worker, "import fs from 'node:fs';const file=" + JSON.stringify(configPath)
    + ";const c=JSON.parse(fs.readFileSync(file,'utf8'));c.counter++;for(const t of c.tasks.filter(x=>x.enabled))"
    + "console.log('TASK '+t.id+' '+(t.id==='b'&&c.counter>=2?'FAIL':'OK'));fs.writeFileSync(file,JSON.stringify(c));");
  writeBatch(fixture, ['"' + process.execPath + '" "' + worker + '"']);
  const hookWorker = path.join(fixture.dir, "pre-hook.mjs");
  fs.writeFileSync(hookWorker, "import fs from 'node:fs';const file=" + JSON.stringify(configPath)
    + ";const c=JSON.parse(fs.readFileSync(file,'utf8'));if(c.armBlock){c.hookCount++;if(c.hookCount>=2)c.blocked=true;"
    + "fs.writeFileSync(file,JSON.stringify(c));}console.log('HOOK '+c.hookCount);");
  const hook = path.join(fixture.dir, "pre-hook.bat");
  fs.writeFileSync(hook, '@echo off\r\n"' + process.execPath + '" "' + hookWorker + '"\r\n', "utf8");
  const response = await api("POST", "/api/scripts", { name: "Retry admission " + Date.now(),
    pluginType: "task-protocol-admission-fixture", rootPath: fixture.dir,
    gameExe: "C:\\\\Windows\\\\System32\\\\PING.EXE", maxAttempts: 2,
    totalTimeoutMinutes: 10, logStallTimeoutMinutes: 5, autoUpdateConfig: true });
  assert.equal(response.status, 200, await response.clone().text());
  const script = await response.json();
  try {
    const user = await createUserBinding(script.id, "Retry admission fixture", { preRunScript: hook });
    const dispatch = async () => {
      const started = await api("POST", "/api/dispatch/script", { scriptId: script.id });
      assert.equal(started.status, 200, await started.clone().text());
      assert.equal(await waitNoRunning(60000), true);
    };
    await dispatch();
    const old = await waitForHistory(script.id);
    assert.equal(old.status, "success", JSON.stringify(old));
    const store = path.join(runtimeDir, "data", script.id, user.id, "store", "config.json");
    const armed = JSON.parse(fs.readFileSync(store, "utf8"));
    armed.armBlock = true; armed.blocked = false; armed.hookCount = 0;
    fs.writeFileSync(store, JSON.stringify(armed));
    await dispatch();
    const responseHistory = await (await api("GET", "/api/history?days=7&offset=0&limit=100")).json();
    const records = Array.isArray(responseHistory) ? responseHistory : responseHistory.records;
    const current = records.find(item => item.scriptInstanceId === script.id && item.id !== old.id);
    assert.ok(current, "new run persisted");
    assert.equal(current.status, "partial", JSON.stringify(current));
    assert.equal(current.attempts, 1);
    assert.equal(current.attemptDetails.length, 2);
    assert.equal(current.attemptDetails[1].status, "blocked");
    assert.equal(current.taskReport.attemptReports.length, 1);
    assert.equal(current.taskReport.finalTaskResults.find(item => item.taskId === "b").status, "failed");
    assert.equal(current.taskReport.summary.tone, "warn");
    assert.ok(current.taskReport.admissionBlocked);
    assert.equal(JSON.parse(fs.readFileSync(store, "utf8")).counter, 2);
    const summary = await (await api("GET", "/api/users/task-summaries")).json();
    const binding = summary.find(item => item.userId === user.id).bindings.find(item => item.scriptInstanceId === script.id);
    assert.equal(binding.recordId, current.id);
    assert.equal(binding.admissionRecordId, current.id);
    const day = fs.readdirSync(path.join(runtimeDir, "history")).find(name => /^\d{4}-\d{2}-\d{2}$/.test(name));
    const hookLog = path.join(runtimeDir, "history", day, current.historyDirectory, current.attemptDetails[1].logFile);
    assert.match(fs.readFileSync(hookLog, "utf8"), /HOOK 2/);
  } finally { await deleteScript(script.id); }
});

test("真实 Python Judge 解释器边界", { skip: enabled && pythonAvailable ? false : (!enabled ? skipReason : "未找到 python") }, async () => {
  await runJudge(
    "python",
    "import json\nprint(json.dumps({'status':'success','reason':'python-system-smoke'}))",
    "python-judge",
  );
});

test("专项协议运行边界：脚本保持打开时完成监控，未确认任务保持未知", { skip }, async () => {
  const fixture = makeFixture("task-boundary");
  fixture.exe = path.join(fixture.dir, "task-run.bat");
  fs.writeFileSync(path.join(fixture.cfg, "config.json"), JSON.stringify({tasks:[{id:"a",enabled:true},{id:"b",enabled:true}]}));
  const worker = path.join(fixture.dir, "task-worker.mjs");
  fs.writeFileSync(worker, "console.log('TASK a OK');console.log('RUN END');setInterval(()=>{},1000);");
  writeBatch(fixture, [`"${process.execPath}" "${worker}"`]);
  const response = await api("POST", "/api/scripts", {name:`Task boundary ${Date.now()}`,pluginType:"task-protocol-fixture",
    rootPath:fixture.dir,gameExe:"C:\\Windows\\System32\\PING.EXE",maxAttempts:1,totalTimeoutMinutes:10,logStallTimeoutMinutes:5});
  assert.equal(response.status,200,await response.clone().text());
  const script=await response.json();
  try {
    await createUserBinding(script.id,"Boundary fixture");
    const started=await api("POST","/api/dispatch/script",{scriptId:script.id});
    assert.equal(started.status,200,await started.clone().text());
    assert.equal(await waitNoRunning(60000),true,"Explicit boundary completes a live process without waiting for stall");
    const record=await waitForHistory(script.id);
    assert.equal(record.status,"partial",JSON.stringify(record));
    const results=record.taskReport.finalTaskResults;
    assert.equal(results.find(t=>t.taskId==='a').status,'succeeded');
    assert.equal(results.find(t=>t.taskId==='b').status,'unknown');
    assert.equal(record.taskReport.attemptReports[0].lifecycleOutcome,'completed');
  } finally { await deleteScript(script.id); }
});
