import assert from "node:assert/strict";
import fs from "node:fs";
import path from "node:path";
import * as runtime from "./runtime-helper.mjs";
export { runtime, assert, fs, path };

export async function json(method, route, body, status = 200) {
  const response = await runtime.api(method, route, body);
  const text = await response.text();
  assert.equal(response.status, status, `${method} ${route}: ${text}`);
  return JSON.parse(text);
}

export async function boot() {
  await runtime.prepareRuntime();
  const settings = path.join(runtime.runtimeDir, "config/settings.json");
  fs.writeFileSync(settings, JSON.stringify({ ...JSON.parse(fs.readFileSync(settings)),
    UpdateCheckEnabled: false }));
  const child = runtime.startRuntime(["service"]);
  await runtime.waitForService(null, 5000);
  return child;
}

export async function target(label, { hold = false, failure = false, configPath } = {}) {
  const directory = path.join(runtime.runtimeDir, "fixtures", label);
  fs.mkdirSync(directory, { recursive: true });
  const effect = path.join(directory, "effect.json");
  const worker = path.join(directory, "worker.mjs");
  const executable = path.join(directory, "owned.cmd");
  const configuration = configPath ?? path.join(directory, "cfg");
  if (!configPath) fs.mkdirSync(configuration);
  fs.writeFileSync(executable, `@echo off\r\n"${process.execPath}" "${worker}"\r\nexit /b %errorlevel%\r\n`);
  fs.writeFileSync(worker, `import fs from 'node:fs';
const config=${JSON.stringify(configPath ?? null)};
fs.writeFileSync(${JSON.stringify(effect)},JSON.stringify({pid:process.pid,label:${JSON.stringify(label)},config:config?fs.readFileSync(config,'utf8'):null}));
${hold ? "setInterval(()=>{},1000);" : `process.exitCode=${failure ? 1 : 0};`}`);
  const script = await json("POST", "api/scripts", { name: label, rootPath: directory,
    mainExe: executable, configPath: configuration, logPath: path.join(directory, "run.log"),
    launchGame: false, gameExe: "C:\\Windows\\System32\\PING.EXE", maxAttempts: 1,
    totalTimeoutMinutes: 10, logStallTimeoutMinutes: 5, autoUpdateConfig: false,
    judgeScriptEnabled: true, judgeScriptLanguage: "javascript",
    judgeScript: `console.log(JSON.stringify({status:'${failure ? "failed" : "success"}',reason:'owned-target'}));` });
  return { script, directory, effect };
}

export async function settled(runId, timeoutMs = 8000) {
  let result;
  assert.equal(await runtime.waitFor(async () => {
    result = await json("GET", `api/dispatch/${runId}`);
    return result.finishedAt != null;
  }, timeoutMs, 50), true, JSON.stringify(result));
  return result;
}

export function report(scenarioId, evidence) {
  fs.writeFileSync(process.env.NEXUS_FINITE_RESULT, JSON.stringify({ evidenceType: "actual", scenarioId,
    status: "PASS", cleanup: "complete", runId: runtime.runId, ...evidence }, null, 2), { flag: "wx" });
}
