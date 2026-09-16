import test, { after, before } from "node:test";
import assert from "node:assert/strict";
import fs from "node:fs";
import path from "node:path";
import {
  api,
  adbStub,
  createUserBinding,
  deleteScript,
  isAdminMode,
  isAdministrator,
  blueStacksConfig,
  blueStacksStub,
  ldStub,
  makeFixture,
  mumuStub,
  noxStub,
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

before(async () => {
  if (!enabled) return;
  if (isAdminMode) {
    assert.ok(isAdministrator(), "管理员 System Smoke 必须在 Administrator / High Integrity 终端运行");
  }
  prepareRuntime();
  startRuntime([], {
    NEXUS_ADB_EXE: adbStub,
    NEXUS_MUMU_MANAGER_EXE: mumuStub,
    NEXUS_LD_CONSOLE_EXE: ldStub,
    NEXUS_NOX_CONSOLE_EXE: noxStub,
    NEXUS_BLUESTACKS_PLAYER_EXE: blueStacksStub,
    NEXUS_BLUESTACKS_CONF: blueStacksConfig,
  });
  await waitForService();
});

after(async () => {
  if (enabled) await stopRuntime();
});

async function runEmulator(endpoint, label) {
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
    assert.equal(record.status, "success");
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

test("LDPlayer driver 使用 index 命令和候选端点映射", { skip }, async () => {
  fs.rmSync(path.join(runtimeDir, "ld-stub", "ld-calls.log"), { force: true });
  await runEmulator("127.0.0.1:5554", "ldplayer-emu");
  const calls = fs.readFileSync(path.join(runtimeDir, "ld-stub", "ld-calls.log"), "utf8");
  assert.match(calls, /list2/);
  assert.match(calls, /launch.*--index.*0/);
  assert.match(calls, /quit.*--index.*0/);
  assert.doesNotMatch(calls, /runapp/);
});

test("Nox driver 使用实例索引和 vbox ADB 端口映射", { skip }, async () => {
  fs.rmSync(path.join(runtimeDir, "nox-stub", "nox-calls.log"), { force: true });
  await runEmulator("127.0.0.1:62023", "nox-emu");
  const calls = fs.readFileSync(path.join(runtimeDir, "nox-stub", "nox-calls.log"), "utf8");
  assert.match(calls, /list/);
  assert.match(calls, /launch.*-index:0/);
  assert.match(calls, /quit.*-index:0/);
});

test("BlueStacks driver 使用实例身份和 bundled ADB", { skip }, async () => {
  fs.rmSync(path.join(runtimeDir, "bluestacks-stub", "bluestacks-calls.log"), { force: true });
  await runEmulator("127.0.0.1:5557", "bluestacks-emu");
  const calls = fs.readFileSync(path.join(runtimeDir, "bluestacks-stub", "bluestacks-calls.log"), "utf8");
  assert.match(calls, /--instance.*Pie64/);
});
