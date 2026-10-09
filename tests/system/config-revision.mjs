import net from "node:net";
import { randomUUID } from "node:crypto";
import assert from "node:assert/strict";
import fs from "node:fs";
import path from "node:path";

const port = net.createServer();
await new Promise(resolve => port.listen(0, "127.0.0.1", resolve));
process.env.NEXUS_SYSTEM_WEB_PORT = String(port.address().port);
await new Promise(resolve => port.close(resolve));
const runtime = await import("./runtime-helper.mjs");
const { json, report } = await import("./finite-common.mjs");
const scriptId = randomUUID().replaceAll("-", ""), userId = randomUUID().replaceAll("-", "");
const revision = "settings-v2";
try {
  await runtime.prepareRuntime();
  const plugin = path.join(runtime.runtimeDir, "plugins", "RevisionFixture");
  const site = path.join(runtime.runtimeDir, "fixtures", "native");
  fs.mkdirSync(path.join(plugin, "data"), { recursive: true });
  fs.mkdirSync(path.join(site, "config", "instances"), { recursive: true });
  fs.writeFileSync(path.join(site, "fixture.exe"), "This file must never be executed.");
  const original = '{"selection":"native"}\r\n';
  fs.writeFileSync(path.join(site, "config", "instances", "default.json"), original);
  fs.writeFileSync(path.join(plugin, "plugin.json"), JSON.stringify({ schemaVersion: 2,
    name: "revision-fixture", artifactName: "RevisionFixture", displayName: "Revision fixture",
    version: "0.1.0", minHostVersion: "0.16.15", kind: "data-specialized", configurationRevision: revision,
    capabilities: ["no-fresh-config"], resolve: "data/resolve.json", judgeScript: "data/judge.js" }));
  fs.writeFileSync(path.join(plugin, "data", "judge.js"), "console.log(JSON.stringify({status:'running'}));");
  fs.writeFileSync(path.join(plugin, "data", "resolve.json"), JSON.stringify({
    configSelectionPath: "config/instances/{input:instance}.json",
    inputs: [{ name: "instance", required: false, pattern: "^[A-Za-z0-9_-]+$" }],
    require: [{ var: "main", file: "fixture.exe" }],
    paths: { mainExe: "{main}", args: "--instance {input:instance}", configPath: "config", logPath: "" },
  }));
  const settingsPath = path.join(runtime.runtimeDir, "config", "settings.json");
  const settings = JSON.parse(fs.readFileSync(settingsPath));
  Object.assign(settings, { UpdateCheckEnabled: false, PluginAutoUpdateEnabled: false,
    PluginPreferences: { "revision-fixture": { Enabled: true } } });
  fs.writeFileSync(settingsPath, JSON.stringify(settings));
  fs.writeFileSync(path.join(runtime.runtimeDir, "config", "scripts.json"), JSON.stringify([
    { Id: scriptId, Name: "Revision fixture", PluginType: "revision-fixture", RootPath: site,
      LaunchGame: false, GameExe: process.execPath, MaxAttempts: 1, PluginInputs: {}, ConfigurationRevision: "settings-v1" },
  ]));
  fs.writeFileSync(path.join(runtime.runtimeDir, "config", "users.json"), JSON.stringify([
    // 未选择实例才能验证候选响应，并在占位 EXE 的启动检查前终止编辑。
    { Id: userId, Name: "Synthetic account", Bindings: [{ ScriptInstanceId: scriptId, ConfigInputs: {} }] },
  ]));
  const store = path.join(runtime.runtimeDir, "data", scriptId, userId, "store");
  fs.mkdirSync(store, { recursive: true });
  const oldBytes = '{"old":"preserve exactly"}\r\n';
  fs.writeFileSync(path.join(store, "legacy.json"), oldBytes);
  runtime.startRuntime(["service"]);
  await runtime.waitForService(null, 5000);
  let scripts = await json("GET", "api/scripts");
  assert.equal(scripts.find(s => s.id === scriptId).requiresReconfiguration, true);
  assert.equal(fs.readFileSync(path.join(store, "legacy.json"), "utf8"), oldBytes);
  const journalPath = path.join(runtime.runtimeDir, ".nxp", "config-resets", scriptId, userId + ".json");
  assert.equal(fs.existsSync(journalPath), false);
  const status = await json("GET", `api/users/${userId}/bindings/${scriptId}/edit-config`);
  assert.equal(status.hasSnapshot, true);
  const mismatch = await json("POST", `api/users/${userId}/bindings/${scriptId}/edit-config`, { action: "start", mode: "reuse" }, 400);
  assert.equal(mismatch.code, "config_input_mismatch");
  assert.equal(mismatch.args.inputName, "instance");
  assert.deepEqual(mismatch.args.candidates, ["default"]);
  await json("PUT", `api/scripts/${scriptId}`, { ...scripts.find(s => s.id === scriptId), configurationRevision: "forged" });
  scripts = await json("GET", "api/scripts");
  assert.equal(scripts.find(s => s.id === scriptId).requiresReconfiguration, false);
  assert.equal(scripts.find(s => s.id === scriptId).configurationRevision, revision);
  assert.equal(fs.readFileSync(path.join(site, "config", "instances", "default.json"), "utf8"), original);
  await runtime.stopRuntime();
  runtime.startRuntime(["service"]);
  await runtime.waitForService(null, 5000);
  assert.equal(fs.readFileSync(path.join(store, "legacy.json"), "utf8"), oldBytes);
  assert.equal(fs.existsSync(journalPath), false);
  await runtime.stopRuntime();
  report("configuration-revision", { oldSnapshotPreserved: true, candidateEnvelope: "args", serverAcknowledgement: true });
} finally {
  await runtime.stopRuntime();
}
