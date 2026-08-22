import { spawn } from "node:child_process";
import fs from "node:fs";
import path from "node:path";
import { test, expect } from "@playwright/test";
import { baseUrl, PING_GAME, runtimeDir, makeScriptDir, createScript, api, waitFor, waitNoRunning, localDate, ensureService } from "./helpers.mjs";

await ensureService();

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

test("自启动参数显式路径：运行时启动目标（管理端/执行端分离）/ 普通参数不受影响 / 解析失败回退 / 编辑配置门禁", async () => {
  const ltDir = path.join(runtimeDir, "lt-scripts");
  const ltFlags = path.join(runtimeDir, "lt-flags");
  fs.rmSync(ltDir, { recursive: true, force: true });
  fs.rmSync(ltFlags, { recursive: true, force: true });
  fs.mkdirSync(ltDir, { recursive: true });
  fs.mkdirSync(ltFlags, { recursive: true });
  const managerFlag = path.join(ltFlags, "launcher-ran.flag");
  const execFlag = path.join(ltFlags, "exec-ran.flag");
  const execArgsFlag = path.join(ltFlags, "exec-args.flag");
  const launcherBat = path.join(ltDir, "launcher.bat");
  const execBat = path.join(ltDir, "exec target.bat");
  const runLog = path.join(ltDir, "lt-run-" + localDate() + ".log");
  fs.writeFileSync(launcherBat, [
    "@echo off",
    "echo LAUNCHER-RAN %1 >> \"" + managerFlag + "\"",
  ].join("\r\n"), "ascii");
  fs.writeFileSync(execBat, [
    "@echo off",
    "echo EXEC-RAN >> \"" + execFlag + "\"",
    "echo ARGS %~1 %~2 >> \"" + execArgsFlag + "\"",
    "echo done >> \"" + runLog + "\"",
    "ping -n 3 127.0.0.1 >nul",
  ].join("\r\n"), "ascii");
  const logPattern = path.join(ltDir, "lt-run-{YYYY-MM-DD}.log").replace(/\\/g, "\\\\");

  const s1 = await createScript({
    name: "启动目标相对", rootPath: ltDir.replace(/\\/g, "\\\\"),
    mainExe: launcherBat.replace(/\\/g, "\\\\"), args: ".\\exec target.bat ?-x marker",
    configPath: ltDir.replace(/\\/g, "\\\\"), logPath: logPattern, successKeywords: "done",
  });
  expect(s1.ok, "创建启动目标脚本（Args 显式相对路径，含空格无引号 + ? 前带空格）").toBeTruthy();
  await api("POST", "/api/dispatch/script", { scriptId: s1.id, mode: "manual" });
  expect(await waitNoRunning(30000), "相对路径场景运行结束").toBeTruthy();
  expect(fs.existsSync(execFlag), "运行时启动目标为 Args 路径（exec target.bat 执行端被启动，含空格无需引号）").toBeTruthy();
  expect(!fs.existsSync(managerFlag), "主程序（launcher.bat 管理端）未被启动").toBeTruthy();
  expect(fs.existsSync(execArgsFlag) && fs.readFileSync(execArgsFlag, "utf8").includes("-x marker"), "? 之后的启动目标参数原样传入（-x marker）").toBeTruthy();
  const rec1 = findHistoryRecord(s1.id);
  expect(rec1 && rec1.Status === "success", "相对路径场景历史记录成功（日志完成标志判定）").toBeTruthy();
  await api("DELETE", "/api/scripts/" + s1.id);

  fs.rmSync(execFlag, { force: true });
  const s2 = await createScript({
    name: "启动目标绝对", rootPath: ltDir.replace(/\\/g, "\\\\"),
    mainExe: launcherBat.replace(/\\/g, "\\\\"), args: execBat.replace(/\\/g, "\\\\"),
    configPath: ltDir.replace(/\\/g, "\\\\"), logPath: logPattern,
  });
  await api("POST", "/api/dispatch/script", { scriptId: s2.id, mode: "manual" });
  expect(await waitNoRunning(30000), "绝对路径场景运行结束").toBeTruthy();
  expect(fs.existsSync(execFlag), "绝对路径（含空格无引号）同样作为运行时启动目标").toBeTruthy();
  await api("DELETE", "/api/scripts/" + s2.id);

  const s3 = await createScript({
    name: "普通参数脚本", rootPath: ltDir.replace(/\\/g, "\\\\"),
    mainExe: launcherBat.replace(/\\/g, "\\\\"), args: "-x marker",
    configPath: ltDir.replace(/\\/g, "\\\\"), logPath: logPattern,
  });
  await api("POST", "/api/dispatch/script", { scriptId: s3.id, mode: "manual" });
  expect(await waitNoRunning(30000), "普通参数场景运行结束").toBeTruthy();
  const managerOut = fs.existsSync(managerFlag) ? fs.readFileSync(managerFlag, "utf8") : "";
  expect(managerOut.includes("LAUNCHER-RAN") && managerOut.includes("-x"), "普通参数首项不视为路径，主程序被启动且参数原样传入").toBeTruthy();
  await api("DELETE", "/api/scripts/" + s3.id);

  fs.rmSync(execFlag, { force: true });
  const sQ = await createScript({
    name: "引号参数脚本", rootPath: ltDir.replace(/\\/g, "\\\\"),
    mainExe: launcherBat.replace(/\\/g, "\\\\"), args: '".\\exec target.bat"',
    configPath: ltDir.replace(/\\/g, "\\\\"), logPath: logPattern,
  });
  expect(sQ.ok, "创建引号包裹路径脚本").toBeTruthy();
  await api("POST", "/api/dispatch/script", { scriptId: sQ.id, mode: "manual" });
  expect(await waitNoRunning(30000), "引号场景运行结束").toBeTruthy();
  expect(!fs.existsSync(execFlag), "Args 禁止引号：引号包裹路径不视为启动目标（exec 未被启动）").toBeTruthy();
  const managerOut5 = fs.existsSync(managerFlag) ? fs.readFileSync(managerFlag, "utf8") : "";
  expect(managerOut5.includes("exec target.bat"), "引号内容按普通参数原样传给主程序").toBeTruthy();
  await api("DELETE", "/api/scripts/" + sQ.id);

  const logFile = path.join(runtimeDir, "logs", "nexus-pipeline-" + localDate() + ".log");
  const readLog = () => fs.existsSync(logFile) ? fs.readFileSync(logFile, "utf8").replace(/^\uFEFF/, "") : "";
  const logBefore = readLog();
  const s4 = await createScript({
    name: "启动目标缺失", rootPath: ltDir.replace(/\\/g, "\\\\"),
    mainExe: launcherBat.replace(/\\/g, "\\\\"), args: "..\\no-such-exec.bat",
    configPath: ltDir.replace(/\\/g, "\\\\"), logPath: logPattern,
  });
  await api("POST", "/api/dispatch/script", { scriptId: s4.id, mode: "manual" });
  expect(await waitNoRunning(30000), "解析失败场景运行结束").toBeTruthy();
  expect(fs.existsSync(managerFlag) && fs.readFileSync(managerFlag, "utf8").split("LAUNCHER-RAN").length - 1 >= 2, "显式路径无法解析为可执行文件时回退主程序（launcher.bat 被启动）").toBeTruthy();
  expect(await waitFor(() => readLog().includes("[警告] 脚本自启动参数含显式路径"), 5000), "回退时输出警告日志").toBeTruthy();
  await api("DELETE", "/api/scripts/" + s4.id);

  const cmdCopy = path.join(ltDir, "lt-exec.exe");
  fs.copyFileSync("C:\\Windows\\System32\\cmd.exe", cmdCopy);
  const s5 = await api("POST", "/api/scripts", {
    name: "启动目标门禁", rootPath: ltDir.replace(/\\/g, "\\\\"),
    mainExe: launcherBat.replace(/\\/g, "\\\\"), args: ".\\lt-exec.exe",
    configPath: ltDir.replace(/\\/g, "\\\\"), logPath: logPattern, gameExe: PING_GAME,
    maxAttempts: 1, logStallTimeoutMinutes: 5, totalTimeoutMinutes: 120,
  });
  const sid5 = (await s5.json()).id;
  await api("POST", "/api/scripts/" + sid5 + "/users", { name: "甲", enabled: true });
  const execProc = spawn(cmdCopy, ["/c", "ping -n 60 127.0.0.1"], { stdio: "ignore" });
  await waitFor(async () => {
    try { const p = await fetch(baseUrl + "api/status"); await p.json(); } catch { return false; }
    return true;
  }, 3000);
  await new Promise(r => setTimeout(r, 800));
  const during = await api("POST", `api/scripts/${sid5}/users/甲/edit-config`, { action: "start" });
  expect(during.status === 409, "运行时启动目标在运行 → 编辑配置被拒绝（409）").toBeTruthy();
  spawn("taskkill.exe", ["/PID", String(execProc.pid), "/T", "/F"], { stdio: "ignore" });
  await new Promise(r => setTimeout(r, 800));
  const after = await api("POST", `api/scripts/${sid5}/users/甲/edit-config`, { action: "start" });
  expect(after.ok, "启动目标退出后可正常开始编辑配置").toBeTruthy();
  const cancel = await api("POST", `api/scripts/${sid5}/users/甲/edit-config`, { action: "cancel" });
  expect(cancel.ok, "取消编辑配置正常（会话关闭）").toBeTruthy();
  await api("DELETE", "/api/scripts/" + sid5);
});

test("专用插件：March7thAssistant 适配 / probe / 启动目标推导 / 上级目录执行端", async ({ page }) => {
  const mRoot = path.join(runtimeDir, "sim-march7th");
  fs.rmSync(mRoot, { recursive: true, force: true });
  fs.mkdirSync(mRoot, { recursive: true });
  fs.writeFileSync(path.join(mRoot, "March7th Launcher.exe"), "");
  fs.writeFileSync(path.join(mRoot, "March7th Assistant.exe"), "");

  const st = await (await fetch(baseUrl + "api/status")).json();
  const m7 = (st.plugins || []).find(p => p.name === "march7th");
  expect(m7 && m7.kind === "specialized" && m7.enabled, "March7thAssistant 专用插件已加载且启用（kind=specialized）").toBeTruthy();
  expect(m7 && m7.gameName === "崩坏：星穹铁道", "March7thAssistant 插件提供游戏名（gameName=崩坏：星穹铁道）").toBeTruthy();

  const probeOk = await api("POST", "/api/scripts/probe", { rootPath: mRoot.replace(/\\/g, "\\\\"), pluginType: "march7th" });
  expect(probeOk.ok, "march7th probe 成功").toBeTruthy();
  const profile = (await probeOk.json()).profile;
  expect(profile.mainExe.endsWith("March7th Launcher.exe"), "probe 主程序为 Launcher（编辑配置用）").toBeTruthy();
  expect(profile.args === ".\\March7th Assistant.exe", "probe 启动目标为显式相对路径（.\\ 前缀，无引号）").toBeTruthy();
  expect(profile.configPath.endsWith("config.yaml"), "probe 推导配置文件 config.yaml").toBeTruthy();
  expect(profile.logPath.includes("{YYYY-MM-DD}.log"), "probe 推导日志路径 logs/{YYYY-MM-DD}.log").toBeTruthy();
  expect(profile.successMarkers === undefined, "probe 不再返回完成标志字段（已废弃）").toBeTruthy();
  expect(profile.judgeScript && profile.judgeScript.includes("游戏终止：StarRail"), "probe 提供判断脚本（含运行结束关键字）").toBeTruthy();

  const probeBad = await api("POST", "/api/scripts/probe", { rootPath: path.join(runtimeDir, "no-m7"), pluginType: "march7th" });
  expect(probeBad.status === 400, "march7th probe 对无法推导的根目录返回 400").toBeTruthy();

  const mUp = path.join(runtimeDir, "sim-m7-up");
  fs.rmSync(mUp, { recursive: true, force: true });
  fs.mkdirSync(mUp, { recursive: true });
  fs.writeFileSync(path.join(mUp, "March7th Launcher.exe"), "");
  fs.writeFileSync(path.join(runtimeDir, "March7th Assistant.exe"), "");
  const probeUp = await api("POST", "/api/scripts/probe", { rootPath: mUp.replace(/\\/g, "\\\\"), pluginType: "march7th" });
  expect(probeUp.ok, "执行端在上级目录时 probe 成功").toBeTruthy();
  const upProfile = (await probeUp.json()).profile;
  expect(upProfile.mainExe.endsWith("March7th Launcher.exe") && upProfile.args.startsWith("..\\"), "上级目录场景：主程序 Launcher + Args 为 ..\\ 显式相对路径").toBeTruthy();
  fs.rmSync(path.join(runtimeDir, "March7th Assistant.exe"), { force: true });

  const created = await api("POST", "/api/scripts", {
    name: "专项M7脚本", rootPath: mRoot.replace(/\\/g, "\\\\"), pluginType: "march7th", gameExe: PING_GAME,
    maxAttempts: 1, logStallTimeoutMinutes: 5, totalTimeoutMinutes: 120,
  });
  expect(created.ok, "API 创建 march7th 专项脚本实例成功").toBeTruthy();
  const sid = (await created.json()).id;
  const list = await (await fetch(baseUrl + "api/scripts")).json();
  const got = list.find(s => s.id === sid);
  expect(got && got.pluginType === "march7th", "专项实例保存 pluginType=march7th").toBeTruthy();
  expect(got.mainExe.endsWith("March7th Launcher.exe"), "主程序由插件固化（Launcher）").toBeTruthy();
  expect(got.args === ".\\March7th Assistant.exe", "Args 启动目标由插件固化（.\\ 显式相对路径，无引号）").toBeTruthy();
  expect(got.configPath.endsWith("config.yaml") && got.logPath.includes("{YYYY-MM-DD}.log"), "配置/日志路径由插件固化").toBeTruthy();
  expect(got.successMarkers === undefined, "专项实例不再返回完成标志字段（已废弃）").toBeTruthy();
  await api("DELETE", "/api/scripts/" + sid);

  await page.goto(baseUrl + "#/scripts", { waitUntil: "domcontentloaded" });
  await page.waitForSelector('[data-testid="new-script"]', { timeout: 5000 });
  await page.click('[data-testid="new-script"]');
  await page.waitForSelector(".new-script-chooser", { timeout: 5000 });
  const chooserText = await page.textContent(".new-script-chooser");
  expect(chooserText.includes("新建March7thAssistant专项脚本实例"), "选择卡片层含「新建March7thAssistant专项脚本实例」卡片").toBeTruthy();
  await page.click(".modal button:has-text('取消')");
});

test("专用插件：ZenlessZoneZeroOneDragon 适配 / probe / 固化 / 新建卡片", async ({ page }) => {
  const zRoot = path.join(runtimeDir, "sim-zenless");
  fs.rmSync(zRoot, { recursive: true, force: true });
  fs.mkdirSync(path.join(zRoot, ".log"), { recursive: true });
  fs.mkdirSync(path.join(zRoot, "config"), { recursive: true });
  fs.writeFileSync(path.join(zRoot, "OneDragon-Launcher.exe"), "");

  const st = await (await fetch(baseUrl + "api/status")).json();
  const z = (st.plugins || []).find(p => p.name === "zzzonedragon");
  expect(z && z.kind === "specialized" && z.enabled, "ZenlessZoneZeroOneDragon 专用插件已加载且启用（kind=specialized）").toBeTruthy();
  expect(z && z.gameName === "绝区零", "ZenlessZoneZeroOneDragon 插件提供游戏名（gameName=绝区零）").toBeTruthy();

  const probeOk = await api("POST", "/api/scripts/probe", { rootPath: zRoot.replace(/\\/g, "\\\\"), pluginType: "zzzonedragon" });
  expect(probeOk.ok, "zzzonedragon probe 成功").toBeTruthy();
  const profile = (await probeOk.json()).profile;
  expect(profile.mainExe.endsWith("OneDragon-Launcher.exe"), "probe 主程序为 OneDragon-Launcher.exe").toBeTruthy();
  expect(profile.args === "-o -c", "probe 推导启动参数 -o -c").toBeTruthy();
  expect(profile.configPath.endsWith("config"), "probe 推导配置文件目录 config").toBeTruthy();
  expect(profile.logPath.includes(".log") && profile.logPath.endsWith("log.txt"), "probe 推导日志路径 .log/log.txt（固定文件）").toBeTruthy();
  expect(profile.successMarkers === undefined, "probe 不再返回完成标志字段（已废弃）").toBeTruthy();
  expect(profile.judgeScript && profile.judgeScript.includes("关闭游戏成功"), "probe 提供判断脚本（含运行结束关键字）").toBeTruthy();

  const probeBad = await api("POST", "/api/scripts/probe", { rootPath: path.join(runtimeDir, "no-zenless"), pluginType: "zzzonedragon" });
  expect(probeBad.status === 400, "zzzonedragon probe 对无法推导的根目录返回 400").toBeTruthy();

  const created = await api("POST", "/api/scripts", {
    name: "专项ZEN脚本", rootPath: zRoot.replace(/\\/g, "\\\\"), pluginType: "zzzonedragon", gameExe: PING_GAME,
    maxAttempts: 1, logStallTimeoutMinutes: 5, totalTimeoutMinutes: 120,
  });
  expect(created.ok, "API 创建 zzzonedragon 专项脚本实例成功").toBeTruthy();
  const sid = (await created.json()).id;
  const list = await (await fetch(baseUrl + "api/scripts")).json();
  const got = list.find(s => s.id === sid);
  expect(got && got.pluginType === "zzzonedragon", "专项实例保存 pluginType=zzzonedragon").toBeTruthy();
  expect(got.mainExe.endsWith("OneDragon-Launcher.exe") && got.args === "-o -c", "主程序/启动参数由插件固化").toBeTruthy();
  expect(got.configPath.endsWith("config") && got.logPath.endsWith("log.txt"), "配置/日志路径由插件固化").toBeTruthy();
  expect(got.successMarkers === undefined, "专项实例不再返回完成标志字段（已废弃）").toBeTruthy();
  await api("DELETE", "/api/scripts/" + sid);

  await page.goto(baseUrl + "#/scripts", { waitUntil: "domcontentloaded" });
  await page.waitForSelector('[data-testid="new-script"]', { timeout: 5000 });
  await page.click('[data-testid="new-script"]');
  await page.waitForSelector(".new-script-chooser", { timeout: 5000 });
  const chooserText = await page.textContent(".new-script-chooser");
  expect(chooserText.includes("新建ZenlessZoneZeroOneDragon专项脚本实例"), "选择卡片层含「新建ZenlessZoneZeroOneDragon专项脚本实例」卡片").toBeTruthy();
  expect(await page.$$eval(".new-script-chooser .scroll-text", els => els.some(el => el.classList.contains("scrolling"))), "长卡片名溢出时启用文字滚动（.scrolling）").toBeTruthy();
  await page.click(".modal button:has-text('取消')");
});

test("专用插件：MaaEnd 适配 / probe / 固化 / 新建卡片", async ({ page }) => {
  const mRoot = path.join(runtimeDir, "sim-maaend");
  fs.rmSync(mRoot, { recursive: true, force: true });
  fs.mkdirSync(mRoot, { recursive: true });
  fs.writeFileSync(path.join(mRoot, "MaaEnd.exe"), "");

  const st = await (await fetch(baseUrl + "api/status")).json();
  const m = (st.plugins || []).find(p => p.name === "maaend");
  expect(m && m.kind === "specialized" && m.enabled, "MaaEnd 专用插件已加载且启用（kind=specialized）").toBeTruthy();
  expect(m && m.gameName === "明日方舟：终末地", "MaaEnd 插件提供游戏名（gameName=明日方舟：终末地）").toBeTruthy();

  const probeOk = await api("POST", "/api/scripts/probe", { rootPath: mRoot.replace(/\\/g, "\\\\"), pluginType: "maaend" });
  expect(probeOk.ok, "maaend probe 成功").toBeTruthy();
  const profile = (await probeOk.json()).profile;
  expect(profile.mainExe.endsWith("MaaEnd.exe"), "probe 主程序为 MaaEnd.exe").toBeTruthy();
  expect(profile.args === "--autostart --quit-after-run", "probe 推导启动参数 --autostart --quit-after-run").toBeTruthy();
  expect(profile.configPath.endsWith("config"), "probe 推导配置文件目录 config").toBeTruthy();
  expect(profile.logPath.includes("{YYYY-MM-DD}-*.log"), "probe 推导日志路径 debug/{YYYY-MM-DD}-*.log（通配）").toBeTruthy();
  expect(profile.successMarkers === undefined, "probe 不再返回完成标志字段（已废弃）").toBeTruthy();
  expect(profile.judgeScript && profile.judgeScript.includes("mxu-MaaEnd.json"), "probe 提供判断脚本（读取 mxu-MaaEnd.json）").toBeTruthy();

  const probeBad = await api("POST", "/api/scripts/probe", { rootPath: path.join(runtimeDir, "no-maaend"), pluginType: "maaend" });
  expect(probeBad.status === 400, "maaend probe 对无法推导的根目录返回 400").toBeTruthy();

  const created = await api("POST", "/api/scripts", {
    name: "专项MAA脚本", rootPath: mRoot.replace(/\\/g, "\\\\"), pluginType: "maaend", gameExe: PING_GAME,
    maxAttempts: 1, logStallTimeoutMinutes: 5, totalTimeoutMinutes: 120,
  });
  expect(created.ok, "API 创建 maaend 专项脚本实例成功").toBeTruthy();
  const sid = (await created.json()).id;
  const list = await (await fetch(baseUrl + "api/scripts")).json();
  const got = list.find(s => s.id === sid);
  expect(got && got.pluginType === "maaend", "专项实例保存 pluginType=maaend").toBeTruthy();
  expect(got.mainExe.endsWith("MaaEnd.exe") && got.args === "--autostart --quit-after-run", "主程序/启动参数由插件固化").toBeTruthy();
  expect(got.configPath.endsWith("config") && got.logPath.includes("{YYYY-MM-DD}-*.log"), "配置/日志路径由插件固化").toBeTruthy();
  expect(got.judgeScriptEnabled === true && got.judgeScriptLanguage === "javascript" && got.judgeScript.includes("mxu-MaaEnd.json"), "判断脚本由插件固化（JudgeScriptEnabled=true、JavaScript、读取 mxu-MaaEnd.json）").toBeTruthy();
  await api("DELETE", "/api/scripts/" + sid);

  await page.goto(baseUrl + "#/scripts", { waitUntil: "domcontentloaded" });
  await page.waitForSelector('[data-testid="new-script"]', { timeout: 5000 });
  await page.click('[data-testid="new-script"]');
  await page.waitForSelector(".new-script-chooser", { timeout: 5000 });
  const chooserText = await page.textContent(".new-script-chooser");
  expect(chooserText.includes("新建MaaEnd专项脚本实例"), "选择卡片层含「新建MaaEnd专项脚本实例」卡片").toBeTruthy();
  await page.click(".modal button:has-text('取消')");
});

test("插件配置二级页：布局 + 类型选择器 + generic 模板联动与样式", async ({ page }) => {
  await page.goto(baseUrl + "#/plugins", { waitUntil: "domcontentloaded" });
  await page.waitForSelector("h2");
  await page.waitForFunction(() => document.body.textContent.includes("通知推送"), null, { timeout: 5000 });
  const cfgBtn = await page.$('[data-action="plugin-config"]');
  expect(!!cfgBtn, "通知推送插件有「配置」按钮").toBeTruthy();
  await page.click('[data-action="plugin-config"]');
  await page.waitForFunction(() => document.body.textContent.includes("· 配置"), null, { timeout: 5000 });
  const body = await page.textContent("body");
  expect(body.includes("返回插件"), "插件配置页有返回箭头").toBeTruthy();
  expect(body.includes("Webhook 通知") && body.includes("SMTP 邮件通知"), "插件配置页含 Webhook/SMTP 折叠面板").toBeTruthy();
  expect(body.includes("配置信息") && body.includes("启用通知的脚本实例"), "插件配置页含配置信息（统计）").toBeTruthy();
  expect(body.includes("0 个"), "启用通知统计显示（脚本 0 / 队列 0）").toBeTruthy();
  const webhookToggleLayout = await page.$eval("#panel-wh > .switch-row", el => { const style = getComputedStyle(el); const button = el.querySelector("button"); const description = el.querySelector(".switch-copy .muted"); return { flexDirection: style.flexDirection, gap: style.gap, buttonRight: !!button && !!description && button.getBoundingClientRect().right >= description.getBoundingClientRect().right, hasTrack: !!el.querySelector(".switch-track") }; });
  expect(webhookToggleLayout.flexDirection === "row" && webhookToggleLayout.gap !== "0px" && webhookToggleLayout.buttonRight && webhookToggleLayout.hasTrack, "Webhook 开关与设置页服务行为使用统一的右侧控件布局").toBeTruthy();

  const typeOptions = await page.$$eval("#st-whtype option", els => els.map(e => e.textContent));
  expect(typeOptions.length === 6 && typeOptions[0] === "Feishu" && typeOptions[5] === "Generic", "Webhook 类型选项首字母大写（Feishu…Generic）").toBeTruthy();
  const defaultType = await page.$eval("#st-whtype", el => el.value);
  expect(defaultType === "feishu", "Webhook 类型默认 feishu（value 小写）").toBeTruthy();
  expect(await page.$eval("#st-whtpl-box", el => el.hidden), "默认（feishu）时 generic 模板框隐藏").toBeTruthy();
  await page.selectOption("#st-whtype", "generic");
  expect(await page.$eval("#st-whtype", el => el.value) === "generic", "切换后 value 保持小写（generic）").toBeTruthy();
  expect(!(await page.$eval("#st-whtpl-box", el => el.hidden)), "切换 generic 后模板框显示").toBeTruthy();
  await page.selectOption("#st-whtype", "dingtalk");
  expect(await page.$eval("#st-whtpl-box", el => el.hidden), "切换 dingtalk 后模板框再次隐藏").toBeTruthy();

  await page.click('nav a[href="#/plugins"]');
  await page.waitForFunction(() => document.querySelector("h2") && document.querySelector("h2").textContent.includes("插件"), null, { timeout: 5000 });
  expect(true, "返回插件列表正常").toBeTruthy();
});

test("通知复选框与插件状态绑定（禁用隐藏 / 启用恢复）", async ({ page }) => {
  const gDir = makeScriptDir("gating");
  const created = await createScript({ name: "门禁样式脚本", rootPath: gDir.root, mainExe: gDir.main, configPath: gDir.cfg, logPath: gDir.log });
  expect(created.ok, "预创建样式断言用脚本").toBeTruthy();

  const disable = await api("POST", "/api/plugins/notify/disable");
  expect(disable.ok, "API 禁用通知推送插件").toBeTruthy();

  await page.goto(baseUrl + "#/scripts", { waitUntil: "domcontentloaded" });
  await page.waitForFunction(() => document.querySelector("h2") && document.querySelector("h2").textContent.includes("脚本实例"), null, { timeout: 5000 });
  await page.waitForSelector(".script-card", { timeout: 5000 });
  expect(!(await page.$('[data-testid="script-card"] [data-testid="script-notify"]')), "插件禁用时脚本卡片隐藏通知徽章").toBeTruthy();
  await page.click('[data-testid="new-script"]');
  await page.waitForSelector(".new-script-chooser", { timeout: 5000 });
  await page.click('[data-action="open-script-type"][data-plugin=""]');
  await page.waitForSelector("#sm-name");
  expect(!(await page.isVisible("#sm-notify")), "插件禁用时脚本弹窗隐藏「运行通知」切换按钮").toBeTruthy();
  await page.click('[data-action="close-modal"]');

  await page.click('nav a[href="#/queues"]');
  await page.waitForFunction(() => document.querySelector("h2") && document.querySelector("h2").textContent.includes("调度队列"), null, { timeout: 5000 });
  await page.click('[data-action="open-queue-modal"]');
  await page.waitForSelector("#qm-name");
  expect(!(await page.isVisible("#qm-notify")), "插件禁用时队列弹窗隐藏「队列通知」切换按钮").toBeTruthy();
  await page.click('[data-action="close-modal"]');

  const enable = await api("POST", "/api/plugins/notify/enable");
  expect(enable.ok, "API 重新启用通知推送插件").toBeTruthy();

  await page.click('nav a[href="#/scripts"]');
  await page.waitForFunction(() => document.querySelector("h2") && document.querySelector("h2").textContent.includes("脚本实例"), null, { timeout: 5000 });
  await page.waitForSelector(".script-card", { timeout: 5000 });
  const notifyCell2 = await page.$eval('[data-testid="script-card"] [data-testid="script-notify"]', el => el.textContent.trim());
  expect(notifyCell2 === "通知未开启", "插件启用后脚本卡片显示通知状态").toBeTruthy();
  await page.click('[data-testid="new-script"]');
  await page.waitForSelector(".new-script-chooser", { timeout: 5000 });
  await page.click('[data-action="open-script-type"][data-plugin=""]');
  await page.waitForSelector("#sm-name");
  expect(await page.isVisible("#sm-notify"), "插件启用后脚本弹窗恢复显示「运行通知」切换按钮").toBeTruthy();
  await page.click('[data-action="close-modal"]');

  await page.click('nav a[href="#/queues"]');
  await page.waitForFunction(() => document.querySelector("h2") && document.querySelector("h2").textContent.includes("调度队列"), null, { timeout: 5000 });
  await page.click('[data-action="open-queue-modal"]');
  await page.waitForSelector("#qm-name");
  expect(await page.isVisible("#qm-notify"), "插件启用后队列弹窗恢复显示「队列通知」切换按钮").toBeTruthy();
  await page.click('[data-action="close-modal"]');

  await api("DELETE", "/api/scripts/" + created.id);
});

test("仪表盘统计区移除 + 插件能力精简", async ({ page }) => {
  const statDir = path.join(runtimeDir, "stat");
  fs.rmSync(statDir, { recursive: true, force: true });
  fs.mkdirSync(statDir, { recursive: true });
  const exitBat = path.join(statDir, "exit-ok.bat");
  fs.writeFileSync(exitBat, "@echo off\r\nexit /b 0\r\n");
  const created = await createScript({
    name: "统计脚本", rootPath: statDir, mainExe: exitBat,
    configPath: statDir,
    logPath: statDir,
    notifyEnabled: true,
  });
  const sid = created.id;
  const qr = await api("POST", "/api/queues", {
    name: "统计队列", autoRunMode: "scheduled", completionAction: "none",
    timeSets: [{ id: "", enabled: true, days: [0, 1, 2, 3, 4, 5, 6], time: "23:59" }],
    tasks: [{ id: "", index: 0, scriptInstanceId: sid }], notifyEnabled: true,
  });
  const qid = (await qr.json()).id;

  await page.goto(baseUrl + "#/dashboard", { waitUntil: "domcontentloaded" });
  await page.waitForSelector('[data-testid="dashboard-state"]');
  expect(await page.evaluate(() => !document.querySelector(".stat-grid-operational") && !document.querySelector("#next-q")), "仪表盘不再显示下一调度倒计时卡").toBeTruthy();
  expect(await page.locator(".plugin-card").count() === 0, "健康通知插件不再占用仪表盘卡片空间").toBeTruthy();

  await api("DELETE", "/api/queues/" + qid);
  await api("DELETE", "/api/scripts/" + sid);
});
