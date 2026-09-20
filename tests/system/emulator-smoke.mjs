import test, { after, before } from "node:test";
import assert from "node:assert/strict";
import fs from "node:fs";
import path from "node:path";
import {
  api,
  adbStub,
  createUserBinding,
  deleteScript,
  makeFixture,
  mumuStub,
  prepareRuntime,
  startRuntime,
  stopRuntime,
  waitForHistory,
  waitForService,
  waitNoRunning,
  writeBatch,
  runtimeDir,
} from "./runtime-helper.mjs";

const enabled = process.env.NEXUS_SYSTEM_SMOKE === "1";
const skipReason = process.env.NEXUS_SYSTEM_SMOKE !== "1"
  ? "设置 NEXUS_SYSTEM_SMOKE=1 后运行"
  : "";
const skip = enabled ? false : skipReason;

const defaultEmulatorEnv = {
  NEXUS_ADB_EXE: adbStub,
  NEXUS_MUMU_MANAGER_EXE: mumuStub,
  NEXUS_TEST_EMULATOR_PLUGIN: "",
};

const fixturePluginName = "fixture-emulator";
const fixturePluginArtifact = "FixtureEmulator";
const fixturePluginEndpoint = "127.0.0.1:16555";
const fixturePluginEvents = path.join(runtimeDir, "emulator-plugin-events.log");

function installEmulatorPluginFixture() {
  const sourceDirectory = process.env.NEXUS_SYSTEM_EMULATOR_PLUGIN_DIR;
  assert.ok(sourceDirectory && fs.existsSync(sourceDirectory), "managed emulator fixture plugin must be built by tests/run.mjs");
  const pluginDirectory = path.join(runtimeDir, "plugins", fixturePluginArtifact);
  fs.mkdirSync(pluginDirectory, { recursive: true });
  fs.cpSync(sourceDirectory, pluginDirectory, { recursive: true });
  fs.writeFileSync(path.join(pluginDirectory, "plugin.json"), JSON.stringify({
    schemaVersion: 2,
    name: fixturePluginName,
    artifactName: fixturePluginArtifact,
    displayName: "Fixture Emulator",
    description: "System Smoke emulator provider",
    version: "0.1.0",
    kind: "managed-code",
    minHostVersion: "0.16.5",
    apiVersion: "1.7",
    entryAssembly: "NexusPipeline.TestPlugin.dll",
    entryType: "NexusPipeline.TestPlugin.TestPlugin",
    capabilities: ["background-jobs"],
  }, null, 2), "utf8");
}

function enableEmulatorPluginFixture() {
  const settingsPath = path.join(runtimeDir, "config", "settings.json");
  fs.mkdirSync(path.dirname(settingsPath), { recursive: true });
  const settings = fs.existsSync(settingsPath)
    ? JSON.parse(fs.readFileSync(settingsPath, "utf8"))
    : {};
  settings.PluginPreferences ??= {};
  settings.PluginPreferences[fixturePluginName] = { Enabled: true };
  fs.writeFileSync(settingsPath, JSON.stringify(settings, null, 2), "utf8");
}

before(async () => {
  if (!enabled) return;
  await prepareRuntime();
  installEmulatorPluginFixture();
  startRuntime([], defaultEmulatorEnv);
  await waitForService();
});

after(async () => {
  if (enabled) await stopRuntime();
});

async function restartRuntime(extraEnv = {}) {
  await stopRuntime();
  startRuntime([], { ...defaultEmulatorEnv, ...extraEnv });
  await waitForService();
}

async function runEmulator(endpoint, label, expectedStatus = "success") {
  const fixture = makeFixture(label);
  writeBatch(fixture, [`echo ${label}-ok>>"${fixture.log}"`]);
  fs.rmSync(path.join(runtimeDir, "adb-stub", "rebooted.flag"), { force: true });
  const stubDir = endpoint.endsWith(":16416") ? path.join(runtimeDir, "mumu-stub") : path.join(runtimeDir, "adb-stub");
  fs.writeFileSync(path.join(stubDir, "foreground.txt"), "  mCurrentFocus=Window{test u0 com.example.game/.MainActivity}", "utf8");
  const response = await api("POST", "/api/scripts", {
    name: `System Smoke ${label}-${Date.now()}`,
    rootPath: fixture.dir,
    mainExe: fixture.exe,
    configPath: fixture.cfg,
    logPath: fixture.log,
    gameMode: "emulator",
    gameExe: endpoint,
    gameArgs: "-n com.example.game/.MainActivity",
    launchGame: true,
    forceCloseGame: true,
    maxAttempts: 1,
    logStallTimeoutMinutes: 5,
    totalTimeoutMinutes: 120,
    successKeywords: `${label}-ok`,
  });
  assert.equal(response.status, 200);
  const script = await response.json();
  try {
    await createUserBinding(script.id, "系统用户");
    assert.equal((await api("POST", "/api/dispatch/script", { scriptId: script.id })).status, 200);
    assert.equal(await waitNoRunning(), true);
    const record = await waitForHistory(script.id);
    assert.equal(record.status, expectedStatus);
  } finally {
    await deleteScript(script.id);
  }
}

test("Generic ADB driver 使用 stub command sequence", { skip }, async () => {
  fs.rmSync(path.join(runtimeDir, "adb-stub", "calls.log"), { force: true });
  await runEmulator("127.0.0.1:16384", "generic-emu");
  const calls = fs.readFileSync(path.join(runtimeDir, "adb-stub", "calls.log"), "utf8");
  assert.match(calls, /start/);
});

test("MuMu driver 使用 manager stub command sequence", { skip }, async () => {
  fs.rmSync(path.join(runtimeDir, "mumu-stub", "mumu-calls.log"), { force: true });
  await runEmulator("127.0.0.1:16416", "mumu-emu");
  const calls = fs.readFileSync(path.join(runtimeDir, "mumu-stub", "mumu-calls.log"), "utf8");
  assert.match(calls, /launch/);
  assert.match(calls, /"start"/);
  assert.match(calls, /"dumpsys"/);
});

test("未安装模拟器支持扩展时 Generic ADB 仍可运行", { skip }, async () => {
  fs.rmSync(path.join(runtimeDir, "adb-stub", "calls.log"), { force: true });
  await restartRuntime();
  await runEmulator("127.0.0.1:5554", "generic-without-emulator-extension");
  const calls = fs.readFileSync(path.join(runtimeDir, "adb-stub", "calls.log"), "utf8");
  assert.match(calls, /connect 127\.0\.0\.1:5554/);
  assert.match(calls, /start/);
});

test("MuMuManager info 失败返回检测错误，成功但端点不匹配时继续 Generic ADB", { skip }, async () => {
  const onlyMumu = {
    NEXUS_MUMU_MANAGER_EXE: mumuStub,
  };
  const failureFlag = path.join(runtimeDir, "mumu-stub", "info-fail.flag");
  try {
    await restartRuntime(onlyMumu);
    fs.writeFileSync(failureFlag, "fail\n", "utf8");
    await runEmulator("127.0.0.1:16416", "mumu-info-failure", "failed");
    fs.rmSync(failureFlag, { force: true });

    await restartRuntime({
      NEXUS_MUMU_MANAGER_EXE: mumuStub,
    });
    await runEmulator("127.0.0.1:16384", "mumu-nonmatch-generic");
  } finally {
    fs.rmSync(failureFlag, { force: true });
    await restartRuntime();
  }
});

test("managed emulator provider 经插件 API 参与执行、截图与实例清理", { skip }, async () => {
  enableEmulatorPluginFixture();
  fs.rmSync(fixturePluginEvents, { force: true });
  await restartRuntime({
    NEXUS_TEST_EMULATOR_PLUGIN: "1",
    NEXUS_TEST_EMULATOR_ENDPOINT: fixturePluginEndpoint,
    NEXUS_TEST_EMULATOR_EVENTS: fixturePluginEvents,
  });

  await runEmulator(fixturePluginEndpoint, "managed-emulator-extension");

  const events = fs.readFileSync(fixturePluginEvents, "utf8");
  assert.match(events, /ready/);
  assert.match(events, /start -n com\.example\.game\/\.MainActivity/);
  assert.match(events, /foreground/);
  assert.match(events, /capture/);
  assert.match(events, /shutdown/);
});
