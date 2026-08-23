import fs from "node:fs";
import path from "node:path";
import { test, expect } from "@playwright/test";
import { baseUrl, runtimeDir, api, waitFor, waitNoRunning, ensureService } from "./helpers.mjs";

await ensureService();

// v0.7.0+ 模拟器适配 e2e：stub adb（NEXUS_ADB_EXE 注入）模拟 MuMu 命令，calls.log 记录调用序列供断言。
const STUB_DIR = path.join(runtimeDir, "adb-stub");
const callsLog = path.join(STUB_DIR, "calls.log");
const foregroundFile = path.join(STUB_DIR, "foreground.txt");
const ADB = "127.0.0.1:16384";
const PKG = "com.example.game";
const START_ARGS = `-n ${PKG}/.MainActivity`;

function resetStub(foregroundPkg = "app.lawnchair") {
  fs.rmSync(callsLog, { force: true });
  fs.writeFileSync(foregroundFile, `  mCurrentFocus=Window{test u0 ${foregroundPkg}/app.lawnchair.LawnchairLauncher}`, "utf8");
}

function readCalls() {
  return fs.existsSync(callsLog) ? fs.readFileSync(callsLog, "utf8").split(/\r?\n/).filter(Boolean) : [];
}

function callsContain(prefix) {
  return readCalls().filter(c => c.startsWith(prefix)).length;
}

function makeEmuDir(label) {
  const dir = path.join(runtimeDir, "emu-" + label);
  fs.rmSync(dir, { recursive: true, force: true });
  fs.mkdirSync(path.join(dir, "logs"), { recursive: true });
  fs.mkdirSync(path.join(dir, "cfg"), { recursive: true });
  return dir;
}

function writeScriptBat(dir, label, logFile, lines) {
  const bat = path.join(dir, `nexusemu-${label}.bat`);
  fs.writeFileSync(bat, ["@echo off", ...lines, "exit /b 0"].join("\r\n"), "ascii");
  return bat;
}

function findHistoryRecord(scriptId) {
  const historyRoot = path.join(runtimeDir, "history");
  if (!fs.existsSync(historyRoot)) return null;
  const dirs = fs.readdirSync(historyRoot).filter(d => /^\d{4}-\d{2}-\d{2}$/.test(d)).sort().reverse();
  for (const dir of dirs) {
    const files = fs.readdirSync(path.join(historyRoot, dir)).filter(f => f.endsWith(".json")).sort().reverse();
    for (const f of files) {
      const rec = JSON.parse(fs.readFileSync(path.join(historyRoot, dir, f), "utf8").replace(/^\uFEFF/, ""));
      if (rec.ScriptInstanceId === scriptId) return rec;
    }
  }
  return null;
}

async function waitHistory(scriptId) {
  await waitFor(() => findHistoryRecord(scriptId) !== null, 10000);
  return findHistoryRecord(scriptId);
}

async function runScript(scriptId) {
  const res = await api("POST", "/api/dispatch/script", { scriptId });
  expect(res.ok, "调度中心提交运行").toBeTruthy();
  await waitNoRunning(60000);
}

async function deleteScript(scriptId) {
  try {
    await api("DELETE", "/api/scripts/" + scriptId);
  } catch { /* 清理失败不阻塞 */ }
}

test("前端：通用脚本弹窗显示「启动方式」选择器，切模拟器后字段联动并保存成功", async ({ page }) => {
  const dir = makeEmuDir("front");
  const logFile = path.join(dir, "logs", "run.log");
  const bat = writeScriptBat(dir, "front", logFile, [`echo emu-ok-line >> "${logFile}"`]);
  await page.goto(baseUrl + "#/scripts");
  await page.click('[data-testid="new-script"]');
  const chooser = await page.$(".new-script-chooser");
  if (chooser) await page.click('[data-action="open-script-type"][data-plugin=""]');
  await expect(page.locator("#sm-mode")).toBeVisible();
  await expect(page.locator("#sm-mode option")).toHaveCount(2);
  await expect(page.locator("#sm-mode")).toHaveValue("pc");
  await expect(page.locator('label[for="sm-game-exe"]')).toContainText("游戏路径");
  await page.selectOption("#sm-mode", "emulator");
  await expect(page.locator('label[for="sm-game-exe"]')).toContainText("模拟器ADB地址");
  await page.fill("#sm-name", "模拟器前端用例");
  await page.fill("#sm-root", dir);
  await page.fill("#sm-exe", bat);
  await page.fill("#sm-config", path.join(dir, "cfg"));
  await page.fill("#sm-log", logFile);
  await page.fill("#sm-game-exe", ADB);
  await page.fill("#sm-game-args", START_ARGS);
  await page.click('[data-action="save-script"]');
  await expect(page.locator('.script-card', { hasText: "模拟器前端用例" })).toBeVisible();
  const scripts = await (await api("GET", "/api/scripts")).json();
  const created = scripts.find(s => s.name === "模拟器前端用例");
  expect(created, "脚本已创建").toBeTruthy();
  expect(created.gameMode).toBe("emulator");
  expect(created.gameExe).toBe(ADB);
  await deleteScript(created.id);
});

test("前端：MaaEnd 专项弹窗有选择器（supportsEmulator=true），BetterGI 专项无选择器", async ({ page }) => {
  await page.goto(baseUrl + "#/scripts");
  await page.click('[data-testid="new-script"]');
  await page.click('.new-script-chooser [data-action="open-script-type"][data-plugin="maaend"]');
  await expect(page.locator("#sm-mode")).toBeVisible();
  await page.click('[data-action="close-modal"]');
  await page.click('[data-testid="new-script"]');
  await page.click('.new-script-chooser [data-action="open-script-type"][data-plugin="bettergi"]');
  await expect(page.locator("#sm-mode")).toHaveCount(0);
  await page.click('[data-action="close-modal"]');
});

test("保存校验：非法 ADB 地址被前端拦截（无端口）", async ({ page }) => {
  const dir = makeEmuDir("badaddr");
  const logFile = path.join(dir, "logs", "run.log");
  const bat = writeScriptBat(dir, "badaddr", logFile, [`echo emu-ok-line >> "${logFile}"`]);
  await page.goto(baseUrl + "#/scripts");
  await page.click('[data-testid="new-script"]');
  const chooser = await page.$(".new-script-chooser");
  if (chooser) await page.click('[data-action="open-script-type"][data-plugin=""]');
  await page.selectOption("#sm-mode", "emulator");
  await page.fill("#sm-name", "非法地址用例");
  await page.fill("#sm-root", dir);
  await page.fill("#sm-exe", bat);
  await page.fill("#sm-config", path.join(dir, "cfg"));
  await page.fill("#sm-log", logFile);
  await page.fill("#sm-game-exe", "127.0.0.1");
  await page.click('[data-action="save-script"]');
  await expect(page.locator(".toast")).toContainText("ADB地址格式不正确", { timeout: 3000 });
  await page.click('[data-action="close-modal"]');
});

test("后端拒绝：BetterGI 专项 + 模拟器启动方式 → 400；MaaEnd 专项 + 模拟器 → 允许", async () => {
  const bgiRoot = path.join(runtimeDir, "emu-bgi");
  fs.rmSync(bgiRoot, { recursive: true, force: true });
  fs.mkdirSync(bgiRoot, { recursive: true });
  fs.copyFileSync("C:\\Windows\\System32\\cmd.exe", path.join(bgiRoot, "BetterGI.exe"));
  const denied = await api("POST", "/api/scripts", {
    name: "bgi-emu", pluginType: "bettergi", rootPath: bgiRoot,
    gameMode: "emulator", gameExe: ADB, gameArgs: START_ARGS,
    maxAttempts: 1, logStallTimeoutMinutes: 5, totalTimeoutMinutes: 120,
  });
  expect(denied.status).toBe(400);
  const deniedBody = await denied.json();
  expect(deniedBody.error).toContain("不支持安卓模拟器");

  const maaendRoot = path.join(runtimeDir, "emu-maaend");
  fs.rmSync(maaendRoot, { recursive: true, force: true });
  fs.mkdirSync(maaendRoot, { recursive: true });
  fs.copyFileSync("C:\\Windows\\System32\\cmd.exe", path.join(maaendRoot, "MaaEnd.exe"));
  const allowed = await api("POST", "/api/scripts", {
    name: "maaend-emu", pluginType: "maaend", rootPath: maaendRoot,
    gameMode: "emulator", gameExe: ADB, gameArgs: START_ARGS,
    maxAttempts: 1, logStallTimeoutMinutes: 5, totalTimeoutMinutes: 120,
  });
  expect(allowed.ok, "MaaEnd 专项允许模拟器启动方式").toBeTruthy();
  const created = await allowed.json();
  expect(created.gameMode).toBe("emulator");
  await deleteScript(created.id);
});

test("运行链路：connect → am start → 前台确认 → 成功收尾关闭模拟器（reboot）", async () => {
  const dir = makeEmuDir("succ");
  const logFile = path.join(dir, "logs", "run.log");
  const bat = writeScriptBat(dir, "succ", logFile, [`echo emu-ok-line >> "${logFile}"`]);
  resetStub(PKG);
  const created = await api("POST", "/api/scripts", {
    name: "emu-succ", rootPath: dir, mainExe: bat, configPath: path.join(dir, "cfg"), logPath: logFile,
    successKeywords: "emu-ok-line", launchGame: true, gameMode: "emulator", gameExe: ADB, gameArgs: START_ARGS,
    gameWaitSeconds: 10, forceCloseGame: true, maxAttempts: 1, logStallTimeoutMinutes: 5, totalTimeoutMinutes: 120,
  });
  const script = await created.json();
  await api("POST", `/api/scripts/${script.id}/users`, { name: "默认", enabled: true });
  try {
    await runScript(script.id);
    const rec = await waitHistory(script.id);
    expect(rec.FinalStatus).toBe("success");
    await waitFor(() => readCalls().length >= 3, 8000);
    expect(callsContain("connect " + ADB)).toBe(1);
    expect(callsContain("start -n " + PKG)).toBe(1);
    expect(callsContain("reboot")).toBe(1);
  } finally {
    await deleteScript(script.id);
  }
});

test("失败重试：尝试收尾关闭前台应用（force-stop），重试轮重新 am start", async () => {
  const dir = makeEmuDir("retry");
  const logFile = path.join(dir, "logs", "run.log");
  const bat = writeScriptBat(dir, "retry", logFile, [`echo emu-fail-line >> "${logFile}"`]);
  resetStub(PKG);
  const created = await api("POST", "/api/scripts", {
    name: "emu-retry", rootPath: dir, mainExe: bat, configPath: path.join(dir, "cfg"), logPath: logFile,
    failureKeywords: "emu-fail-line", launchGame: true, gameMode: "emulator", gameExe: ADB, gameArgs: START_ARGS,
    gameWaitSeconds: 10, forceCloseGame: true, maxAttempts: 2, logStallTimeoutMinutes: 5, totalTimeoutMinutes: 120,
  });
  const script = await created.json();
  await api("POST", `/api/scripts/${script.id}/users`, { name: "默认", enabled: true });
  try {
    await runScript(script.id);
    const rec = await waitHistory(script.id);
    expect(rec.FinalStatus).toBe("failed");
    expect(rec.Attempts).toBe(2);
    await waitFor(() => readCalls().length >= 4, 8000);
    expect(callsContain("start -n " + PKG)).toBe(2);
    expect(callsContain("force-stop " + PKG)).toBeGreaterThanOrEqual(1);
    expect(callsContain("reboot")).toBe(1);
  } finally {
    await deleteScript(script.id);
  }
});

test("强制关闭开关：ForceCloseGame=false 时运行结束不关闭模拟器", async () => {
  const dir = makeEmuDir("noclose");
  const logFile = path.join(dir, "logs", "run.log");
  const bat = writeScriptBat(dir, "noclose", logFile, [`echo emu-ok-line >> "${logFile}"`]);
  resetStub(PKG);
  const created = await api("POST", "/api/scripts", {
    name: "emu-noclose", rootPath: dir, mainExe: bat, configPath: path.join(dir, "cfg"), logPath: logFile,
    successKeywords: "emu-ok-line", launchGame: true, gameMode: "emulator", gameExe: ADB, gameArgs: START_ARGS,
    gameWaitSeconds: 10, forceCloseGame: false, maxAttempts: 1, logStallTimeoutMinutes: 5, totalTimeoutMinutes: 120,
  });
  const script = await created.json();
  await api("POST", `/api/scripts/${script.id}/users`, { name: "默认", enabled: true });
  try {
    await runScript(script.id);
    const rec = await waitHistory(script.id);
    expect(rec.FinalStatus).toBe("success");
    await waitFor(() => readCalls().length >= 2, 8000);
    expect(callsContain("reboot")).toBe(0);
  } finally {
    await deleteScript(script.id);
  }
});

test("连接失败：adb connect 输出失败标记时立即判失败，不进入 am start", async () => {
  const dir = makeEmuDir("connfail");
  const logFile = path.join(dir, "logs", "run.log");
  const bat = writeScriptBat(dir, "connfail", logFile, [`echo emu-ok-line >> "${logFile}"`]);
  resetStub(PKG);
  const created = await api("POST", "/api/scripts", {
    name: "emu-connfail", rootPath: dir, mainExe: bat, configPath: path.join(dir, "cfg"), logPath: logFile,
    successKeywords: "emu-ok-line", launchGame: true, gameMode: "emulator", gameExe: "127.0.0.1:16385", gameArgs: START_ARGS,
    gameWaitSeconds: 10, forceCloseGame: true, maxAttempts: 1, logStallTimeoutMinutes: 5, totalTimeoutMinutes: 120,
  });
  const script = await created.json();
  await api("POST", `/api/scripts/${script.id}/users`, { name: "默认", enabled: true });
  try {
    await runScript(script.id);
    const rec = await waitHistory(script.id);
    expect(rec.FinalStatus).toBe("failed");
    expect(rec.ResultDetail).toContain("模拟器连接失败");
    await waitFor(() => readCalls().length >= 1, 8000);
    expect(callsContain("connect 127.0.0.1:16385")).toBe(1);
    expect(callsContain("start -n " + PKG)).toBe(0);
  } finally {
    await deleteScript(script.id);
  }
});

test("am start 失败：输出 Error 标记时立即失败，不做前台确认轮询", async () => {
  const dir = makeEmuDir("startfail");
  const logFile = path.join(dir, "logs", "run.log");
  const bat = writeScriptBat(dir, "startfail", logFile, [`echo emu-ok-line >> "${logFile}"`]);
  resetStub(PKG);
  const created = await api("POST", "/api/scripts", {
    name: "emu-startfail", rootPath: dir, mainExe: bat, configPath: path.join(dir, "cfg"), logPath: logFile,
    successKeywords: "emu-ok-line", launchGame: true, gameMode: "emulator", gameExe: ADB, gameArgs: "-n com.bad.game/.MainActivity",
    gameWaitSeconds: 10, forceCloseGame: true, maxAttempts: 1, logStallTimeoutMinutes: 5, totalTimeoutMinutes: 120,
  });
  const script = await created.json();
  await api("POST", `/api/scripts/${script.id}/users`, { name: "默认", enabled: true });
  try {
    await runScript(script.id);
    const rec = await waitHistory(script.id);
    expect(rec.FinalStatus).toBe("failed");
    expect(rec.ResultDetail).toContain("does not exist");
    await waitFor(() => readCalls().length >= 2, 8000);
    expect(callsContain("start -n com.bad.game")).toBe(1);
    expect(callsContain("dumpsys")).toBe(0);
  } finally {
    await deleteScript(script.id);
  }
});

test("插件开关：配置状态与运行态分离，前端按配置隐藏选择器", async ({ page }) => {
  resetStub();
  const disabled = await api("POST", "/api/plugins/emulator-adapter/disable");
  expect(disabled.ok).toBeTruthy();
  const disabledBody = await disabled.json();
  expect(disabledBody.configuredEnabled).toBe(false);
  expect(disabledBody.runtimeEnabled).toBe(true);
  expect(disabledBody.state).toBe("Active");
  try {
    await page.goto(baseUrl + "#/scripts");
    await page.click('[data-testid="new-script"]');
    const chooser = await page.$(".new-script-chooser");
    if (chooser) await page.click('[data-action="open-script-type"][data-plugin=""]');
    await expect(page.locator("#sm-mode")).toHaveCount(0);
    await page.click('[data-action="close-modal"]');

    const dir = makeEmuDir("disabled");
    const logFile = path.join(dir, "logs", "run.log");
    const bat = writeScriptBat(dir, "disabled", logFile, [`echo emu-ok-line >> "${logFile}"`]);
    const created = await api("POST", "/api/scripts", {
      name: "emu-disabled", rootPath: dir, mainExe: bat, configPath: path.join(dir, "cfg"), logPath: logFile,
      successKeywords: "emu-ok-line", launchGame: true, gameMode: "emulator", gameExe: ADB, gameArgs: START_ARGS,
      gameWaitSeconds: 10, forceCloseGame: true, maxAttempts: 1, logStallTimeoutMinutes: 5, totalTimeoutMinutes: 120,
    });
    const script = await created.json();
    await api("POST", `/api/scripts/${script.id}/users`, { name: "默认", enabled: true });
    try {
      // 配置开关重启生效；当前进程的模拟器 capability 仍保持 Active。
      resetStub(PKG);
      await runScript(script.id);
      const rec = await waitHistory(script.id);
      expect(rec.FinalStatus).toBe("success");
      expect(callsContain("start -n " + PKG)).toBe(1);
    } finally {
      await deleteScript(script.id);
    }
  } finally {
    await api("POST", "/api/plugins/emulator-adapter/enable");
  }
});
