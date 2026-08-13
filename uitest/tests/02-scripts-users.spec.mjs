import { spawn, spawnSync } from "node:child_process";
import fs from "node:fs";
import path from "node:path";
import { test, expect } from "@playwright/test";
import { baseUrl, PING_GAME, runtimeDir, makeScriptDir, createScript, api, waitFor, waitNoRunning, waitAbsent, localDate, restartService } from "./helpers.mjs";

test("脚本实例：空状态 / 新建卡片组 / 必填校验 / 新建 / 编辑 / 删除", async ({ page }) => {
  await page.goto(baseUrl + "#/scripts", { waitUntil: "domcontentloaded" });
  await page.waitForSelector("h2");
  expect((await page.textContent("body")).includes("暂无脚本实例"), "无脚本时显示空状态提示而非空卡片").toBeTruthy();
  const newBtn = await page.$('[data-testid="new-script"]');
  expect(!!newBtn, "新建通用脚本实例按钮位于右上角（page-head 内）").toBeTruthy();
  await page.click('[data-testid="new-script"]');
  await page.waitForSelector(".new-script-chooser", { timeout: 5000 });
  expect((await page.$$(".chooser-card")).length === 5, "选择卡片层含通用与专项五张卡片").toBeTruthy();
  await page.click('[data-action="open-script-type"][data-plugin=""]');
  await page.waitForSelector(".modal-mask");

  await page.click(".modal button:has-text('保存')");
  await page.waitForTimeout(400);
  expect(await page.$(".modal-mask"), "必填未填时无法保存（弹窗保留）").toBeTruthy();
  await page.click(".modal button:has-text('保存')");
  await page.waitForTimeout(200);

  await page.fill("#sm-name", "测试脚本A");
  const crudDir = makeScriptDir("crud");
  await page.fill("#sm-root", crudDir.root.replace(/\\/g, "\\\\"));
  await page.fill("#sm-exe", crudDir.main.replace(/\\/g, "\\\\"));
  await page.fill("#sm-config", crudDir.cfg.replace(/\\/g, "\\\\"));
  await page.fill("#sm-log", crudDir.log.replace(/\\/g, "\\\\"));
  await page.fill("#sm-game-exe", crudDir.main.replace(/\\/g, "\\\\"));
  await page.click(".modal button:has-text('保存')");
  await page.waitForSelector(".modal-mask", { state: "detached", timeout: 5000 });
  await page.waitForFunction(() => document.body.textContent.includes("测试脚本A"), null, { timeout: 5000 });
  expect((await page.textContent("body")).includes("测试脚本A"), "新建后列表中显示脚本名称").toBeTruthy();
  expect(fs.existsSync(path.join(runtimeDir, "config", "scripts.json")), "配置文件写入 config 目录").toBeTruthy();

  await page.click('[data-action="edit-script"]');
  await page.waitForSelector("#sm-name");
  await page.waitForFunction(() => document.querySelector("#sm-root") && document.querySelector("#sm-root").value.length > 0, null, { timeout: 5000 });
  await page.fill("#sm-name", "测试脚本A-改");
  await page.click(".modal button:has-text('保存')");
  await page.waitForSelector(".modal-mask", { state: "detached", timeout: 5000 });
  await page.waitForFunction(() => document.body.textContent.includes("测试脚本A-改"), null, { timeout: 5000 });
  expect((await page.textContent("body")).includes("测试脚本A-改"), "编辑后名称已更新").toBeTruthy();

  await page.click('[data-action="delete-script"]');
  await page.waitForSelector(".modal-mask .modal", { timeout: 5000 });
  expect((await page.textContent(".modal")).includes("确定删除脚本实例"), "删除脚本弹出确认卡片（含确定/取消）").toBeTruthy();
  await page.click('[data-action="confirm-delete-script"]');
  await waitAbsent(page, "测试脚本A-改");
  expect(!(await page.textContent("body")).includes("测试脚本A-改"), "删除后列表不再显示该脚本").toBeTruthy();
});

test("用户管理：按钮改名 / 二级页 / 用户 CRUD / 配置快照与交换 / 运行选用户 / 队列用户下拉", async ({ page }) => {
  const cfgDir = path.join(runtimeDir, "user-cfg");
  const cfgFile = path.join(cfgDir, "configA.txt");
  const logDir = path.join(runtimeDir, "user-log");
  const exitBat = path.join(runtimeDir, "exit-ok.bat");
  fs.rmSync(cfgDir, { recursive: true, force: true });
  fs.rmSync(logDir, { recursive: true, force: true });
  fs.mkdirSync(cfgDir, { recursive: true });
  fs.mkdirSync(logDir, { recursive: true });
  fs.writeFileSync(cfgFile, "ORIGINAL");
  fs.writeFileSync(exitBat, "@echo off\r\nexit /b 0\r\n");

  const create = await api("POST", "/api/scripts", {
    name: "用户测试脚本", rootPath: runtimeDir.replace(/\\/g, "\\\\"), mainExe: exitBat.replace(/\\/g, "\\\\"),
    configPath: cfgDir.replace(/\\/g, "\\\\"), logPath: logDir.replace(/\\/g, "\\\\"), gameExe: PING_GAME,
    maxAttempts: 1, logStallTimeoutMinutes: 5, totalTimeoutMinutes: 120,
  });
  const script = await create.json();
  const sid = script.id;
  const dataDir = path.join(runtimeDir, "data", sid);

  await page.goto(baseUrl + "#/scripts", { waitUntil: "domcontentloaded" });
  await page.waitForFunction(() => document.body.textContent.includes("用户测试脚本"), null, { timeout: 5000 });
  let body = await page.textContent("body");
  expect(body.includes("编辑脚本"), "按钮改为「编辑脚本」").toBeTruthy();
  expect(body.includes("删除脚本"), "按钮改为「删除脚本」").toBeTruthy();
  expect(body.includes("用户管理"), "新增「用户管理」按钮").toBeTruthy();

  await page.click('[data-action="manage-users"]');
  await page.waitForFunction(() => document.body.textContent.includes("添加用户"), null, { timeout: 5000 });
  body = await page.textContent("body");
  expect(body.includes("返回脚本实例"), "用户管理页左上角有返回箭头").toBeTruthy();
  expect(body.includes("暂无用户"), "无用户时显示空状态").toBeTruthy();

  await page.click("button:has-text('添加用户')");
  await page.waitForSelector("#um-name");
  await page.fill("#um-name", "甲");
  await page.click(".modal button:has-text('保存')");
  await page.waitForFunction(() => document.body.textContent.includes(">甲<") || document.body.textContent.includes("已启用"), null, { timeout: 5000 });
  body = await page.textContent("body");
  expect(body.includes("甲") && body.includes("已启用"), "添加用户后卡片显示用户名与启用状态").toBeTruthy();
  expect(fs.existsSync(path.join(dataDir, "甲", "store", "configA.txt")), "首次添加用户生成配置快照（data/…/甲/store/configA.txt）").toBeTruthy();

  await page.click("button:has-text('添加用户')");
  await page.waitForSelector("#um-name");
  await page.fill("#um-name", "乙");
  await page.click(".modal button:has-text('保存')");
  await page.waitForFunction(() => document.body.textContent.includes("乙"), null, { timeout: 5000 });
  await page.click("button:has-text('添加用户')");
  await page.waitForSelector("#um-name");
  await page.fill("#um-name", "甲");
  await page.click(".modal button:has-text('保存')");
  await page.waitForFunction(() => document.body.textContent.includes("用户名重复"), null, { timeout: 5000 });
  body = await page.textContent("body");
  expect(body.includes("用户名重复"), "重复用户名被拒绝（弹窗保留）").toBeTruthy();
  await page.click(".modal button:has-text('取消')");

  await page.click('[data-action="edit-user"][data-name="甲"]');
  await page.waitForSelector("#um-name");
  await page.fill("#um-name", "甲改");
  await page.click(".modal button:has-text('保存')");
  await page.waitForFunction(() => document.body.textContent.includes("甲改"), null, { timeout: 5000 });
  expect(fs.existsSync(path.join(dataDir, "甲改", "store", "configA.txt")), "改名后用户数据目录已迁移").toBeTruthy();
  expect(!fs.existsSync(path.join(dataDir, "甲")), "改名后旧用户目录已不存在（重命名而非复制）").toBeTruthy();
  const user = "甲改";
  const userDir = path.join(dataDir, user);

  await page.click(`[data-action="edit-user-config"][data-name="${user}"]`);
  await page.waitForSelector(".modal", { timeout: 5000 });
  body = await page.textContent("body");
  expect(body.includes("配置编辑中"), "编辑配置弹窗显示提示").toBeTruthy();
  await page.keyboard.press("Escape");
  expect(await page.$(".modal"), "锁定弹窗：Esc 无法关闭（须完成或取消）").toBeTruthy();
  await page.mouse.click(20, 400);
  expect(await page.$(".modal"), "锁定弹窗：点击遮罩无法关闭").toBeTruthy();
  expect(!(await page.isVisible(".modal-close")), "锁定弹窗：隐藏关闭按钮").toBeTruthy();
  expect(fs.readFileSync(cfgFile, "utf8") === "ORIGINAL", "编辑配置开始后配置路径为内部储存副本").toBeTruthy();
  expect(fs.existsSync(path.join(userDir, "original", "configA.txt")), "原配置已移入缓存区").toBeTruthy();
  fs.writeFileSync(cfgFile, "NEWSETUP");
  await page.click('[data-action="edit-config-done"]');
  await page.waitForFunction(() => !document.querySelector(".modal"), null, { timeout: 5000 });
  await new Promise(r => setTimeout(r, 300));
  expect(fs.readFileSync(path.join(userDir, "store", "configA.txt"), "utf8") === "NEWSETUP", "完成后新配置已保存（store）").toBeTruthy();
  expect(fs.readFileSync(cfgFile, "utf8") === "ORIGINAL", "完成后原配置已还原到配置路径").toBeTruthy();
  expect(!fs.existsSync(path.join(userDir, "original", "configA.txt")), "完成后缓存区已清空").toBeTruthy();

  await page.click(`[data-action="edit-user-config"][data-name="${user}"]`);
  await page.waitForSelector(".modal", { timeout: 5000 });
  fs.writeFileSync(cfgFile, "HALF");
  await page.click('[data-action="edit-config-cancel"]');
  await page.waitForFunction(() => !document.querySelector(".modal"), null, { timeout: 5000 });
  await new Promise(r => setTimeout(r, 300));
  expect(fs.readFileSync(cfgFile, "utf8") === "ORIGINAL", "取消后原配置已还原").toBeTruthy();
  expect(fs.readFileSync(path.join(userDir, "store", "configA.txt"), "utf8") === "NEWSETUP", "取消不改变已保存的用户配置").toBeTruthy();

  await page.click('nav a[href="#/dispatch"]');
  await page.waitForSelector("#dc-script");
  await page.selectOption("#dc-script", { label: "用户测试脚本" });
  await page.waitForTimeout(300);
  expect(!(await page.$("#dc-user")), "调度中心无用户选择下拉（启用用户依次运行）").toBeTruthy();
  await page.click("button:has-text('执行')");
  await waitFor(async () => (await (await fetch(baseUrl + "api/status")).json()).running?.length > 0, 10000);
  expect(await waitNoRunning(20000), "运行任务已结束（含配置还原）").toBeTruthy();
  expect(await waitFor(() => {
    try { return fs.readFileSync(cfgFile, "utf8") === "ORIGINAL"; } catch { return false; }
  }, 5000), "运行结束后原配置已还原（实际：" + fs.readFileSync(cfgFile, "utf8") + "）").toBeTruthy();
  expect(fs.readFileSync(path.join(userDir, "store", "configA.txt"), "utf8") === "NEWSETUP", "运行结束后用户配置保留").toBeTruthy();
  const runHist = await (await fetch(baseUrl + "api/history?days=7")).json();
  const manualRecs = runHist.filter(h => h.scriptInstanceId === sid && h.mode === "manual");
  expect(manualRecs.length === 2, "手动执行按启用用户依次运行产生 2 条记录（实际 " + manualRecs.length + "）").toBeTruthy();

  await page.click('nav a[href="#/queues"]');
  await page.waitForSelector("h2");
  await page.click("button:has-text('新建调度队列')");
  await page.waitForSelector("#qm-name");
  await page.fill("#qm-name", "用户队列测试");
  await page.click("text=+ 添加任务");
  await page.selectOption('[data-task-idx="0"]', { label: "用户测试脚本" });
  await page.waitForTimeout(300);
  const taskUserSel = await page.$('[data-task-user-idx="0"]');
  expect(!taskUserSel, "队列任务行不再显示用户下拉（沿用脚本启用用户依次运行）").toBeTruthy();
  await page.click(".modal button:has-text('保存')");
  await page.waitForFunction(() => document.body.textContent.includes("用户队列测试"), null, { timeout: 5000 });

  await page.click('nav a[href="#/scripts"]');
  await page.waitForSelector("h2");
  await page.click('[data-action="delete-script"][data-name="用户测试脚本"]');
  await page.waitForSelector(".modal-mask .modal", { timeout: 5000 });
  expect((await page.textContent(".modal")).includes("确定删除脚本实例"), "删除脚本弹出确认卡片（含确定/取消）").toBeTruthy();
  await page.click('[data-action="confirm-delete-script"]');
  await waitAbsent(page, "用户测试脚本");
  expect(!fs.existsSync(dataDir), "删除脚本后 data 目录已清理").toBeTruthy();
  const queues = await (await fetch(baseUrl + "api/queues")).json();
  for (const q of queues) {
    if (q.name === "用户队列测试") await api("DELETE", "/api/queues/" + q.id);
  }
});

test("用户排序：API 顺序落盘 / 名单校验 / 运行时 409 / UI 上移下移 / 执行顺序准确", async ({ page }) => {
  const ordDir = makeScriptDir("ordering");
  const ordCfg = path.join(runtimeDir, "order-cfg");
  fs.rmSync(ordCfg, { recursive: true, force: true });
  fs.mkdirSync(ordCfg, { recursive: true });
  fs.writeFileSync(path.join(ordCfg, "config.txt"), "ORIGINAL");
  const orderFlag = path.join(runtimeDir, "order-seq.flag");
  fs.rmSync(orderFlag, { force: true });
  const preBat = user => path.join(runtimeDir, `order-pre-${user}.bat`);
  const mkPre = (user, tag) => fs.writeFileSync(preBat(user), [
    "@echo off",
    `echo ${tag} >> "${orderFlag}"`,
    "exit /b 0",
  ].join("\r\n"), "ascii");
  mkPre("甲", "FIRST");
  mkPre("乙", "SECOND");
  mkPre("丙", "THIRD");

  const created = await api("POST", "/api/scripts", {
    name: "排序脚本", rootPath: ordDir.root, mainExe: ordDir.main,
    configPath: ordCfg, logPath: ordDir.log, gameExe: PING_GAME,
    maxAttempts: 1, logStallTimeoutMinutes: 5, totalTimeoutMinutes: 120,
  });
  expect(created.ok, "创建排序测试脚本").toBeTruthy();
  const sid = (await created.json()).id;
  for (const name of ["甲", "乙", "丙"]) {
    await api("POST", `/api/scripts/${sid}/users`, { name, enabled: true, preRunScript: preBat(name) });
  }
  const orderApi = names => api("PUT", `/api/scripts/${sid}/users/order`, { names });

  let r = await orderApi(["丙", "乙", "甲"]);
  expect(r.ok, "提交完整顺序名单成功").toBeTruthy();
  let list = await (await fetch(baseUrl + "api/scripts")).json();
  let got = list.find(s => s.id === sid);
  expect(got.users.map(u => u.name).join() === "丙,乙,甲", "排序后用户顺序为 丙,乙,甲").toBeTruthy();

  expect((await orderApi(["丙", "乙"])).status === 400, "名单缺用户被拒（400）").toBeTruthy();
  expect((await orderApi(["丙", "乙", "丁"])).status === 400, "名单含不存在用户被拒（400）").toBeTruthy();
  expect((await orderApi(["丙", "乙", "乙"])).status === 400, "名单含重复用户被拒（400）").toBeTruthy();

  r = await orderApi(["甲", "丙", "乙"]);
  expect(r.ok, "恢复顺序 甲,丙,乙").toBeTruthy();
  await api("POST", "/api/dispatch/script", { scriptId: sid, mode: "manual" });
  expect(await waitNoRunning(30000), "排序脚本运行结束").toBeTruthy();
  const seq = fs.existsSync(orderFlag) ? fs.readFileSync(orderFlag, "utf8") : "";
  const pos = (tag) => seq.indexOf(tag);
  expect(pos("FIRST") >= 0 && pos("THIRD") > pos("FIRST") && pos("SECOND") > pos("THIRD"), "按用户排序顺序依次执行（实际：" + JSON.stringify(seq.trim()) + "）").toBeTruthy();

  const longBat = path.join(runtimeDir, "order-long.bat");
  fs.writeFileSync(longBat, "@echo off\r\nping -n 6 127.0.0.1 >nul\r\nexit /b 0\r\n", "ascii");
  const created2 = await api("POST", "/api/scripts", {
    name: "排序门禁脚本", rootPath: ordDir.root, mainExe: longBat.replace(/\\/g, "\\\\"),
    configPath: ordCfg, logPath: ordDir.log, gameExe: PING_GAME,
    maxAttempts: 1, logStallTimeoutMinutes: 5, totalTimeoutMinutes: 120,
  });
  expect(created2.ok, "创建排序门禁脚本成功").toBeTruthy();
  const sid2 = (await created2.json()).id;
  await api("POST", `/api/scripts/${sid2}/users`, { name: "甲", enabled: true });
  await api("POST", `/api/scripts/${sid2}/users`, { name: "乙", enabled: true });
  const dr2 = await api("POST", "/api/dispatch/script", { scriptId: sid2, mode: "manual" });
  expect(dr2.ok, "门禁脚本开始运行（dispatch 受理）").toBeTruthy();
  await waitFor(async () => (await (await fetch(baseUrl + "api/status")).json()).running?.length > 0, 10000);
  const during = await api("PUT", `/api/scripts/${sid2}/users/order`, { names: ["乙", "甲"] });
  expect(during.status === 409, "脚本运行中调整用户顺序被拒（409）").toBeTruthy();
  expect(await waitNoRunning(30000), "门禁脚本运行结束").toBeTruthy();

  await page.goto(baseUrl + "#/dashboard", { waitUntil: "domcontentloaded" });
  await page.goto(baseUrl + "#/scripts", { waitUntil: "domcontentloaded" });
  await page.waitForSelector('[data-testid="new-script"]', { timeout: 10000 });
  await page.waitForFunction(() => document.body.textContent.includes("排序脚本"), null, { timeout: 10000 });
  await page.click(`[data-action="manage-users"][data-id="${sid}"]`);
  await page.waitForFunction(() => document.body.textContent.includes("添加用户") && document.body.textContent.includes("丙"), null, { timeout: 10000 });
  await page.click('[data-action="move-user-up"][data-name="丙"]');
  await page.waitForFunction(() => { const cards = Array.from(document.querySelectorAll(".user-card .list-item-title strong")); return cards.length === 3 && cards[0].textContent === "丙"; }, null, { timeout: 10000 });
  expect(true, "点击上移后 丙 成为第一位（卡片顺序更新）").toBeTruthy();
  expect(await page.$eval('[data-action="move-user-up"][data-name="丙"]', el => el.disabled), "首位用户上移按钮禁用").toBeTruthy();
  expect(await page.$eval('[data-action="move-user-down"][data-name="乙"]', el => el.disabled), "末位用户下移按钮禁用").toBeTruthy();
  list = await (await fetch(baseUrl + "api/scripts")).json();
  got = list.find(s => s.id === sid);
  expect(got.users.map(u => u.name).join() === "丙,甲,乙", "UI 上移后顺序已落盘（丙,甲,乙）").toBeTruthy();
  await page.click('[data-action="move-user-down"][data-name="丙"]');
  await page.waitForFunction(() => { const cards = Array.from(document.querySelectorAll(".user-card .list-item-title strong")); return cards.length === 3 && cards[0].textContent === "甲"; }, null, { timeout: 10000 });
  expect(true, "点击下移后 丙 回到第二位（卡片顺序更新）").toBeTruthy();

  await api("DELETE", "/api/scripts/" + sid);
  await api("DELETE", "/api/scripts/" + sid2);
});

test("队列多用户依次运行 + 配置交换", async () => {
  const cfgDir = path.join(runtimeDir, "mu-cfg");
  const cfgFile = path.join(cfgDir, "configA.txt");
  const muLog = path.join(runtimeDir, "mu-log");
  const exitBat = path.join(runtimeDir, "exit-ok.bat");
  fs.rmSync(cfgDir, { recursive: true, force: true });
  fs.rmSync(muLog, { recursive: true, force: true });
  fs.mkdirSync(cfgDir, { recursive: true });
  fs.mkdirSync(muLog, { recursive: true });
  fs.writeFileSync(cfgFile, "ORIGINAL");
  fs.writeFileSync(exitBat, "@echo off\r\nexit /b 0\r\n");

  const create = await api("POST", "/api/scripts", {
    name: "多用户脚本", rootPath: runtimeDir.replace(/\\/g, "\\\\"), mainExe: exitBat.replace(/\\/g, "\\\\"),
    configPath: cfgDir.replace(/\\/g, "\\\\"), logPath: muLog.replace(/\\/g, "\\\\"), gameExe: PING_GAME,
    maxAttempts: 1, logStallTimeoutMinutes: 5, totalTimeoutMinutes: 120,
  });
  const sid = (await create.json()).id;

  await api("POST", "/api/scripts/" + sid + "/users", { name: "甲", enabled: true });
  await api("POST", "/api/scripts/" + sid + "/users", { name: "乙", enabled: true });

  const editCfg = (user, action) => api("POST", `api/scripts/${sid}/users/${encodeURIComponent(user)}/edit-config`, { action });

  let r = await editCfg("甲", "start");
  expect(r.ok, "编辑甲配置开始").toBeTruthy();
  fs.writeFileSync(cfgFile, "NEWA");
  r = await editCfg("甲", "done");
  expect(r.ok, "甲用户配置已提交（store=NEWA）").toBeTruthy();

  r = await editCfg("乙", "start");
  expect(r.ok, "编辑乙配置开始").toBeTruthy();
  fs.writeFileSync(cfgFile, "NEWB");
  r = await editCfg("乙", "done");
  expect(r.ok, "乙用户配置已提交（store=NEWB）").toBeTruthy();
  expect(fs.readFileSync(cfgFile, "utf8") === "ORIGINAL", "提交后配置路径已还原").toBeTruthy();

  const qr = await api("POST", "/api/queues", {
    name: "多用户队列", autoRunMode: "scheduled", completionAction: "none",
    timeSets: [{ id: "", enabled: true, days: [1], time: "08:00" }],
    tasks: [{ id: "", index: 0, scriptInstanceId: sid }], notifyEnabled: false,
  });
  const qid = (await qr.json()).id;

  const dr = await api("POST", "/api/dispatch/queue", { queueId: qid, mode: "manual" });
  expect(dr.ok, "队列已开始执行").toBeTruthy();
  expect(await waitNoRunning(60000), "队列运行已结束").toBeTruthy();

  const hist = await (await fetch(baseUrl + "api/history?days=7")).json();
  const recent = hist.filter(h => h.queueId === qid);
  expect(recent.length === 2, "队列多用户依次运行产生 2 条记录（实际 " + recent.length + "）").toBeTruthy();
  const names = recent.map(h => h.userName);
  expect(names.includes("甲") && names.includes("乙"), "两条记录分别属于启用用户（甲、乙）").toBeTruthy();
  expect(fs.readFileSync(cfgFile, "utf8") === "ORIGINAL", "队列运行结束后配置路径已还原").toBeTruthy();
  expect(fs.readFileSync(path.join(runtimeDir, "data", sid, "甲", "store", "configA.txt"), "utf8") === "NEWA", "甲用户配置保留").toBeTruthy();
  expect(fs.readFileSync(path.join(runtimeDir, "data", sid, "乙", "store", "configA.txt"), "utf8") === "NEWB", "乙用户配置保留").toBeTruthy();

  await api("DELETE", "/api/queues/" + qid);
  await api("DELETE", "/api/scripts/" + sid);
  expect(!fs.existsSync(path.join(runtimeDir, "data", sid)), "清理后数据目录已删除").toBeTruthy();
});

test("门禁释放：运行中禁止编辑配置，结束后可正常进入", async () => {
  const gateCfg = path.join(runtimeDir, "gate-cfg");
  const gateLog = path.join(runtimeDir, "gate-log");
  fs.rmSync(gateCfg, { recursive: true, force: true });
  fs.rmSync(gateLog, { recursive: true, force: true });
  fs.mkdirSync(gateCfg, { recursive: true });
  fs.mkdirSync(gateLog, { recursive: true });
  const ping = "C:\\Windows\\System32\\PING.EXE";
  const create = await api("POST", "/api/scripts", {
    name: "门禁测试脚本", rootPath: runtimeDir.replace(/\\/g, "\\\\"), mainExe: ping,
    args: "-n 8 127.0.0.1", configPath: gateCfg.replace(/\\/g, "\\\\"), logPath: gateLog.replace(/\\/g, "\\\\"), gameExe: PING_GAME,
    maxAttempts: 1, logStallTimeoutMinutes: 5, totalTimeoutMinutes: 120,
  });
  const sid = (await create.json()).id;
  await api("POST", "/api/scripts/" + sid + "/users", { name: "甲", enabled: true });

  await api("POST", "/api/dispatch/script", { scriptId: sid, mode: "manual", userName: "甲" });
  expect(await waitFor(async () => (await (await fetch(baseUrl + "api/status")).json()).running?.length > 0, 10000), "脚本已开始运行").toBeTruthy();
  const during = await api("POST", `api/scripts/${sid}/users/${encodeURIComponent("甲")}/edit-config`, { action: "start" });
  expect(during.status === 409, "运行中编辑配置被拒绝（409，门禁占用）").toBeTruthy();
  expect(await waitNoRunning(60000), "运行已结束").toBeTruthy();
  const after = await api("POST", `api/scripts/${sid}/users/${encodeURIComponent("甲")}/edit-config`, { action: "start" });
  expect(after.ok, "运行结束后可正常开始编辑配置（门禁已释放，可继续编辑）").toBeTruthy();
  const cancel = await api("POST", `api/scripts/${sid}/users/${encodeURIComponent("甲")}/edit-config`, { action: "cancel" });
  expect(cancel.ok, "取消编辑配置正常（会话关闭）").toBeTruthy();
  await api("DELETE", "/api/scripts/" + sid);
  expect(!fs.existsSync(path.join(runtimeDir, "data", sid)), "门禁测试脚本数据已清理").toBeTruthy();
});

test("批处理游戏启动：有效 stdio + 正常结束", async () => {
  const gameBat = path.join(runtimeDir, "game-launch.bat");
  const mainBat = path.join(runtimeDir, "game-main.bat");
  const marker = path.join(runtimeDir, "game-started.flag");
  const batchCfg = path.join(runtimeDir, "batch-cfg");
  fs.rmSync(marker, { force: true });
  fs.rmSync(batchCfg, { recursive: true, force: true });
  fs.mkdirSync(batchCfg, { recursive: true });
  fs.writeFileSync(gameBat, [
    "@echo off",
    "echo [GAME] started",
    "echo started > \"" + marker + "\"",
    "exit /b 0",
  ].join("\r\n"), "ascii");
  fs.writeFileSync(mainBat, "@echo off\r\necho [MAIN] finished\r\nexit /b 0\r\n", "ascii");

  const create = await api("POST", "/api/scripts", {
    name: "批处理游戏脚本", rootPath: runtimeDir, mainExe: mainBat,
    configPath: batchCfg, logPath: batchCfg, launchGame: true, gameExe: gameBat,
    gameArgs: "", gameWaitSeconds: 0, forceCloseGame: false,
    maxAttempts: 1, logStallTimeoutMinutes: 5, totalTimeoutMinutes: 10,
  });
  const script = await create.json();
  expect(create.ok, "创建批处理游戏测试脚本").toBeTruthy();
  await api("POST", `/api/scripts/${script.id}/users`, { name: "默认", enabled: true });

  const dispatch = await api("POST", "/api/dispatch/script", { scriptId: script.id, mode: "manual" });
  expect(dispatch.ok, "批处理游戏脚本已开始运行").toBeTruthy();

  let gameStarted = false;
  const ended = await waitFor(async () => {
    gameStarted = fs.existsSync(marker);
    return (await (await fetch(baseUrl + "api/status")).json()).running?.length === 0 && gameStarted;
  }, 20000, 200);
  expect(ended && gameStarted, "批处理游戏已启动且主脚本正常结束").toBeTruthy();
  await api("DELETE", "/api/scripts/" + script.id);
});

test("游戏进程确认：未勾选启动游戏跳过 / 填写路径检测双启动", async () => {
  const exitBat = path.join(runtimeDir, "exit-ok.bat");
  const gpcCfg = path.join(runtimeDir, "gpc-cfg");
  fs.rmSync(gpcCfg, { recursive: true, force: true });
  fs.mkdirSync(gpcCfg, { recursive: true });
  fs.writeFileSync(path.join(gpcCfg, "log.txt"), "");
  fs.writeFileSync(exitBat, "@echo off\r\nexit /b 0\r\n");

  const a = await api("POST", "/api/scripts", {
    name: "不启动游戏脚本", rootPath: runtimeDir, mainExe: exitBat.replace(/\\/g, "\\\\"),
    configPath: gpcCfg, logPath: path.join(gpcCfg, "log.txt"), launchGame: false, gameExe: PING_GAME,
    maxAttempts: 1, logStallTimeoutMinutes: 5, totalTimeoutMinutes: 10,
  });
  expect(a.ok, "创建未勾选启动游戏脚本（launchGame=false / gameExe 必填但跳过游戏启动）").toBeTruthy();
  const aid = (await a.json()).id;
  await api("POST", `/api/scripts/${aid}/users`, { name: "默认", enabled: true });
  await api("POST", "/api/dispatch/script", { scriptId: aid, mode: "manual" });
  expect(await waitNoRunning(60000), "未勾选启动游戏脚本运行结束").toBeTruthy();
  const aHist = await (await fetch(baseUrl + "api/history?days=7")).json();
  const aRec = aHist.filter(h => h.scriptInstanceId === aid).at(-1);
  expect(aRec && aRec.finalStatus === "success", "未勾选启动游戏时跳过游戏启动，运行成功（FinalStatus=success）").toBeTruthy();
  await api("DELETE", "/api/scripts/" + aid);

  const ping = "C:\\Windows\\System32\\PING.EXE";
  const b = await api("POST", "/api/scripts", {
    name: "双启动确认脚本", rootPath: runtimeDir, mainExe: exitBat.replace(/\\/g, "\\\\"),
    configPath: gpcCfg, logPath: path.join(gpcCfg, "log.txt"), launchGame: true, gameExe: ping,
    gameArgs: "-n 60 127.0.0.1", gameWaitSeconds: 10, forceCloseGame: true,
    maxAttempts: 1, logStallTimeoutMinutes: 5, totalTimeoutMinutes: 10,
  });
  expect(b.ok, "创建双启动确认脚本（游戏=PING，等待 10 秒确认）").toBeTruthy();
  const bid = (await b.json()).id;
  await api("POST", `/api/scripts/${bid}/users`, { name: "默认", enabled: true });
  await api("POST", "/api/dispatch/script", { scriptId: bid, mode: "manual" });
  const managerLog = path.join(runtimeDir, "logs", "nexus-pipeline-" + localDate() + ".log");
  const seenConfirm = await waitFor(() => {
    if (!fs.existsSync(managerLog)) return false;
    return fs.readFileSync(managerLog, "utf8").includes("已确认游戏进程启动");
  }, 30000, 500);
  expect(seenConfirm, "运行期间确认游戏进程启动（管理器日志含「已确认游戏进程启动」）").toBeTruthy();
  expect(await waitNoRunning(60000), "双启动确认脚本运行结束").toBeTruthy();
  const bHist = await (await fetch(baseUrl + "api/history?days=7")).json();
  const bRec = bHist.filter(h => h.scriptInstanceId === bid).at(-1);
  expect(bRec && bRec.finalStatus === "success", "游戏进程确认后脚本运行成功（FinalStatus=success）").toBeTruthy();
  await api("DELETE", "/api/scripts/" + bid);
});

test("强制关闭游戏独立于启动游戏：后端保留 + 前端复选框解绑", async ({ page }) => {
  const exitBat = path.join(runtimeDir, "exit-ok.bat");
  fs.writeFileSync(exitBat, "@echo off\r\nexit /b 0\r\n");
  const created = await api("POST", "/api/scripts", {
    name: "强制关闭解绑脚本", rootPath: runtimeDir, mainExe: exitBat.replace(/\\/g, "\\\\"),
    configPath: runtimeDir, logPath: runtimeDir, launchGame: false, gameExe: PING_GAME, forceCloseGame: true,
    maxAttempts: 1, logStallTimeoutMinutes: 5, totalTimeoutMinutes: 10,
  });
  expect(created.ok, "API 提交 launchGame=false / forceCloseGame=true / gameExe 必填 成功").toBeTruthy();
  const sid = (await created.json()).id;
  const list = await (await fetch(baseUrl + "api/scripts")).json();
  const got = list.find(s => s.id === sid);
  expect(got && got.forceCloseGame === true, "后端保留 ForceCloseGame=true（不再归一化为 false）").toBeTruthy();
  await api("DELETE", "/api/scripts/" + sid);

  await page.goto(baseUrl + "#/dashboard", { waitUntil: "domcontentloaded" });
  await page.goto(baseUrl + "#/scripts", { waitUntil: "domcontentloaded" });
  await page.click('[data-testid="new-script"]');
  await page.waitForSelector(".new-script-chooser", { timeout: 5000 });
  await page.click('[data-action="open-script-type"][data-plugin=""]');
  await page.waitForSelector("#sm-name");
  const smPressed = id => page.$eval(id, el => el.getAttribute("aria-pressed") === "true");
  expect(await page.$("#sm-force"), "强制关闭切换按钮存在（解绑独立）").toBeTruthy();
  expect(!(await smPressed("#sm-force")), "强制关闭默认未激活").toBeTruthy();
  await page.click("#sm-force");
  expect(await smPressed("#sm-force"), "未激活启动游戏也可激活强制关闭").toBeTruthy();
  await page.click("#sm-force");
  await page.click("#sm-launch");
  expect(!(await smPressed("#sm-force")), "激活启动游戏后强制关闭按钮不受影响").toBeTruthy();
  await page.click('[data-action="close-modal"]');
});

test("失败强制结束游戏进程 + 成功/取消按设置绑定", async () => {
  const failBat = path.join(runtimeDir, "game-fail.bat");
  fs.writeFileSync(failBat, "@echo off\r\nexit /b 1\r\n", "ascii");
  const okBat = path.join(runtimeDir, "game-ok.bat");
  fs.writeFileSync(okBat, "@echo off\r\nexit /b 0\r\n", "ascii");
  const logDir = path.join(runtimeDir, "fk-logs");
  const failLogDir = path.join(runtimeDir, "fk-fail-logs");
  fs.rmSync(logDir, { recursive: true, force: true });
  fs.rmSync(failLogDir, { recursive: true, force: true });
  fs.mkdirSync(logDir, { recursive: true });
  fs.mkdirSync(failLogDir, { recursive: true });
  const logFile = path.join(logDir, "run.log");
  fs.writeFileSync(logFile, "");
  const pingProc = () => spawn(PING_GAME, ["-n", "60", "127.0.0.1"], { stdio: "ignore" });
  const pingRunning = () => spawnSync("tasklist", ["/FI", "IMAGENAME eq ping.exe"], { stdio: "pipe", encoding: "utf8" }).stdout.toLowerCase().includes("ping.exe");
  const killPing = () => spawnSync("taskkill", ["/IM", "ping.exe", "/F"], { stdio: "ignore" });
  const fkCfg = path.join(runtimeDir, "fk-cfg");
  fs.rmSync(fkCfg, { recursive: true, force: true });
  fs.mkdirSync(fkCfg, { recursive: true });
  const base = { rootPath: runtimeDir, configPath: fkCfg, logPath: failLogDir, gameExe: PING_GAME, gameArgs: "-n 60 127.0.0.1", gameWaitSeconds: 1, maxAttempts: 1, logStallTimeoutMinutes: 5, totalTimeoutMinutes: 10 };

  pingProc();
  await new Promise(r => setTimeout(r, 1500));
  expect(pingRunning(), "游戏进程（ping）已启动作为前置").toBeTruthy();
  const f1 = await api("POST", "/api/scripts", { name: "失败杀游戏脚本", mainExe: failBat.replace(/\\/g, "\\\\"), successMarkers: "NEVER-SEEN-MARKER", ...base });
  expect(f1.ok, "创建失败杀游戏脚本（完成标志永不出现 → 任务失败需强制结束游戏）").toBeTruthy();
  const f1id = (await f1.json()).id;
  await api("POST", `/api/scripts/${f1id}/users`, { name: "默认", enabled: true });
  await api("POST", "/api/dispatch/script", { scriptId: f1id, mode: "manual" });
  expect(await waitNoRunning(30000), "失败脚本运行结束").toBeTruthy();
  await new Promise(r => setTimeout(r, 800));
  expect(!pingRunning(), "任务失败后游戏进程被强制结束（任何失败类型均强制）").toBeTruthy();
  await api("DELETE", "/api/scripts/" + f1id);
  killPing();

  pingProc();
  await new Promise(r => setTimeout(r, 1500));
  const f2 = await api("POST", "/api/scripts", { name: "成功留游戏脚本", mainExe: okBat.replace(/\\/g, "\\\\"), forceCloseGame: false, ...base, logPath: logFile });
  expect(f2.ok, "创建成功留游戏脚本（日志文件存在 + 无完成标志 → 进程退出判成功）").toBeTruthy();
  const f2id = (await f2.json()).id;
  await api("POST", `/api/scripts/${f2id}/users`, { name: "默认", enabled: true });
  await api("POST", "/api/dispatch/script", { scriptId: f2id, mode: "manual" });
  expect(await waitNoRunning(30000), "成功脚本运行结束").toBeTruthy();
  await new Promise(r => setTimeout(r, 500));
  expect(pingRunning(), "任务成功且未勾选强制关闭时游戏进程保留").toBeTruthy();
  await api("DELETE", "/api/scripts/" + f2id);
  killPing();

  pingProc();
  await new Promise(r => setTimeout(r, 1500));
  const f3 = await api("POST", "/api/scripts", { name: "成功杀游戏脚本", mainExe: okBat.replace(/\\/g, "\\\\"), forceCloseGame: true, ...base, logPath: logFile });
  expect(f3.ok, "创建成功杀游戏脚本（勾选强制关闭）").toBeTruthy();
  const f3id = (await f3.json()).id;
  await api("POST", `/api/scripts/${f3id}/users`, { name: "默认", enabled: true });
  await api("POST", "/api/dispatch/script", { scriptId: f3id, mode: "manual" });
  expect(await waitNoRunning(30000), "成功脚本运行结束").toBeTruthy();
  await new Promise(r => setTimeout(r, 800));
  expect(!pingRunning(), "任务成功且勾选强制关闭时游戏进程被结束").toBeTruthy();
  await api("DELETE", "/api/scripts/" + f3id);
  killPing();

  const cancelBat = path.join(runtimeDir, "game-cancel.bat");
  fs.writeFileSync(cancelBat, "@echo off\r\nping -n 30 127.0.0.1 >nul\r\nexit /b 0\r\n", "ascii");
  pingProc();
  await new Promise(r => setTimeout(r, 1500));
  const f4 = await api("POST", "/api/scripts", { name: "取消留游戏脚本", mainExe: cancelBat.replace(/\\/g, "\\\\"), forceCloseGame: false, ...base });
  const f4id = (await f4.json()).id;
  await api("POST", `/api/scripts/${f4id}/users`, { name: "默认", enabled: true });
  await api("POST", "/api/dispatch/script", { scriptId: f4id, mode: "manual" });
  await waitFor(async () => (await (await fetch(baseUrl + "api/status")).json()).running?.length > 0, 10000);
  const status = await (await fetch(baseUrl + "api/status")).json();
  const runId = (status.running || []).find(item => item.targetId === f4id)?.id;
  expect(!!runId, "取消前已获取运行任务 id").toBeTruthy();
  await api("POST", "/api/cancel", { runId });
  expect(await waitNoRunning(30000), "取消后脚本运行结束").toBeTruthy();
  await new Promise(r => setTimeout(r, 500));
  expect(pingRunning(), "手动取消且未勾选强制关闭时游戏进程保留（按设置绑定）").toBeTruthy();
  await api("DELETE", "/api/scripts/" + f4id);
  killPing();
});

test("编辑脚本保留用户（PUT 不含 users 不覆盖）", async () => {
  const keepCfg = path.join(runtimeDir, "keep-cfg");
  fs.rmSync(keepCfg, { recursive: true, force: true });
  fs.mkdirSync(keepCfg, { recursive: true });
  fs.writeFileSync(path.join(keepCfg, "cfg.txt"), "KEEP");
  const kDir = makeScriptDir("keep");
  const created = await createScript({ name: "保留用户脚本", rootPath: kDir.root, mainExe: kDir.main, configPath: keepCfg, logPath: kDir.log });
  expect(created.ok, "创建脚本").toBeTruthy();
  const sid = created.id;
  const ur = await api("POST", `/api/scripts/${sid}/users`, { name: "甲", enabled: true });
  expect(ur.ok, "添加用户甲").toBeTruthy();
  expect(fs.existsSync(path.join(runtimeDir, "data", sid, "甲", "store", "cfg.txt")), "添加用户生成配置快照").toBeTruthy();
  const put = await api("PUT", `/api/scripts/${sid}`, { name: "保留用户脚本-改", rootPath: kDir.root, mainExe: kDir.main, configPath: keepCfg, logPath: kDir.log, gameExe: PING_GAME, maxAttempts: 1, logStallTimeoutMinutes: 5, totalTimeoutMinutes: 120 });
  expect(put.ok, "PUT 改名（payload 不含 users，模拟前端）").toBeTruthy();
  const list = await (await fetch(baseUrl + "api/scripts")).json();
  const got = list.find(s => s.id === sid);
  expect(got && (got.users || []).length === 2 && got.users.some(u => u.name === "甲"), "改名后用户仍保留（默认+甲）").toBeTruthy();
  expect(fs.existsSync(path.join(runtimeDir, "data", sid, "甲", "store", "cfg.txt")), "改名后用户数据目录未被重建或丢失").toBeTruthy();
  await api("DELETE", `/api/scripts/${sid}`);
});

test("脚本已打开：编辑配置 409 + 手动执行被禁止（400）", async () => {
  const ping = "C:\\Windows\\System32\\PING.EXE";
  const cfgDir = path.join(runtimeDir, "open-cfg");
  const logDir = path.join(runtimeDir, "open-log");
  fs.rmSync(cfgDir, { recursive: true, force: true });
  fs.rmSync(logDir, { recursive: true, force: true });
  fs.mkdirSync(cfgDir, { recursive: true });
  fs.mkdirSync(logDir, { recursive: true });
  const created = await api("POST", "/api/scripts", {
    name: "占用检测脚本", rootPath: runtimeDir, mainExe: ping,
    configPath: cfgDir, logPath: logDir, gameExe: PING_GAME,
    maxAttempts: 1, logStallTimeoutMinutes: 5, totalTimeoutMinutes: 120,
  });
  const sid = (await created.json()).id;
  expect(created.ok, "创建占用检测脚本（mainExe=PING.EXE）").toBeTruthy();
  await api("POST", `/api/scripts/${sid}/users`, { name: "甲", enabled: true });

  const pinger = spawn(ping, ["-n", "60", "127.0.0.1"], { stdio: "ignore" });
  try {
    await new Promise(r => setTimeout(r, 1200));
    const start = await api("POST", `/api/scripts/${sid}/users/甲/edit-config`, { action: "start" });
    expect(start.status === 409, "编辑配置被拒（409，脚本程序已打开）").toBeTruthy();
    const startBody = await start.json();
    expect(startBody.error.includes("检测到已打开的脚本"), "拒绝原因提示「检测到已打开的脚本，退出脚本后才能编辑配置。」").toBeTruthy();

    const dr = await api("POST", "/api/dispatch/script", { scriptId: sid, mode: "manual" });
    expect(dr.status === 400, "脚本已打开时手动执行被禁止（400）").toBeTruthy();
    const drBody = await dr.json();
    expect((drBody.error || "").includes("正在运行"), "拒绝原因含「正在运行，请先退出后再执行」").toBeTruthy();
  } finally {
    pinger.kill();
  }
  await api("DELETE", `/api/scripts/${sid}`);
});

test("脚本路径引号去除（成对首尾引号）", async ({ page }) => {
  const qDir = makeScriptDir("quote");
  const created = await api("POST", "/api/scripts", {
    name: "引号路径脚本", rootPath: `"${qDir.root}"`, mainExe: `'${qDir.main}'`,
    configPath: `"${qDir.cfg}"`, logPath: `'${qDir.log}'`,
    gameExe: `"${qDir.main}"`, maxAttempts: 1, logStallTimeoutMinutes: 5, totalTimeoutMinutes: 120,
  });
  expect(created.ok, "POST 带引号路径成功").toBeTruthy();
  const sid = (await created.json()).id;
  const list = await (await fetch(baseUrl + "api/scripts")).json();
  const got = list.find(s => s.id === sid);
  expect(got && got.rootPath === qDir.root && got.mainExe === qDir.main
    && got.configPath === qDir.cfg && got.logPath === qDir.log
    && got.gameExe === qDir.main, "路径已去除成对引号").toBeTruthy();

  await page.goto(baseUrl + "#/dashboard", { waitUntil: "domcontentloaded" });
  await page.goto(baseUrl + "#/scripts", { waitUntil: "domcontentloaded" });
  await page.waitForSelector('[data-testid="new-script"]', { timeout: 10000 });
  await page.click('[data-action="edit-script"][data-id="' + sid + '"]');
  await page.waitForSelector("#sm-name");
  await page.fill("#sm-root", `"${qDir.root}"`);
  await page.fill("#sm-game-exe", `'${qDir.main}'`);
  await page.click(".modal button:has-text('保存')");
  await page.waitForSelector(".modal-mask", { state: "detached", timeout: 5000 });
  const list2 = await (await fetch(baseUrl + "api/scripts")).json();
  const got2 = list2.find(s => s.id === sid);
  expect(got2 && got2.rootPath === qDir.root && got2.gameExe === qDir.main, "前端编辑带引号路径保存成功且落盘去除成对引号").toBeTruthy();

  await api("DELETE", "/api/scripts/" + sid);
});

test("路径合规校验：通用脚本假路径 / 专项根目录 / 游戏路径 校验拒绝", async () => {
  const p = makeScriptDir("pathval");
  const post = body => api("POST", "/api/scripts", { maxAttempts: 1, logStallTimeoutMinutes: 5, totalTimeoutMinutes: 120, ...body });
  const badRoot = await post({ name: "假路径脚本", rootPath: "C:\\no-such-dir-xyz", mainExe: "C:\\no-such-dir-xyz\\run.bat", configPath: "C:\\no-such-dir-xyz\\cfg", logPath: "C:\\no-such-dir-xyz\\logs" });
  expect(badRoot.status === 400 && (await badRoot.json()).error.includes("根目录"), "根目录不存在被拒（400）").toBeTruthy();
  const badMain = await post({ name: "假主程序脚本", rootPath: p.root, mainExe: path.join(p.root, "no.exe"), configPath: p.cfg, logPath: p.log });
  expect(badMain.status === 400 && (await badMain.json()).error.includes("主程序"), "主程序不存在被拒（400）").toBeTruthy();
  const badCfg = await post({ name: "假配置脚本", rootPath: p.root, mainExe: p.main, configPath: path.join(p.root, "no-cfg"), logPath: p.log });
  expect(badCfg.status === 400 && (await badCfg.json()).error.includes("配置"), "配置路径不存在被拒（400）").toBeTruthy();
  const badLog = await post({ name: "非法日志脚本", rootPath: p.root, mainExe: p.main, configPath: p.cfg, logPath: path.join(p.log, "a?b.log") });
  expect(badLog.status === 400 && (await badLog.json()).error.includes("日志路径"), "日志路径含非法字符被拒（400）").toBeTruthy();
  const badGame = await post({ name: "空游戏脚本", rootPath: p.root, mainExe: p.main, configPath: p.cfg, logPath: p.log, launchGame: true, gameExe: "" });
  expect(badGame.status === 400 && (await badGame.json()).error.includes("游戏"), "勾选启动游戏且游戏路径为空被拒（400）").toBeTruthy();
  const badGame2 = await post({ name: "无游戏路径脚本", rootPath: p.root, mainExe: p.main, configPath: p.cfg, logPath: p.log, launchGame: false });
  expect(badGame2.status === 400 && (await badGame2.json()).error.includes("游戏"), "游戏路径必填：未勾选启动游戏且无游戏路径同样被拒（400）").toBeTruthy();
  const spBad = await post({ name: "专项假根目录", rootPath: "C:\\no-such-zenless", pluginType: "zzzonedragon" });
  expect(spBad.status === 400 && (await spBad.json()).error.includes("根目录"), "专项脚本根目录不存在被拒（400）").toBeTruthy();
  const spRoot = path.join(runtimeDir, "pathval-special");
  fs.rmSync(spRoot, { recursive: true, force: true });
  fs.mkdirSync(path.join(spRoot, "config"), { recursive: true });
  fs.writeFileSync(path.join(spRoot, "OneDragon-Launcher.exe"), "");
  const spNoGame = await post({ name: "专项空游戏路径", rootPath: spRoot, pluginType: "zzzonedragon" });
  expect(spNoGame.status === 400 && (await spNoGame.json()).error.includes("游戏"), "专项脚本同样要求游戏路径必填（400）").toBeTruthy();
  const good = await post({ name: "合规脚本", rootPath: p.root, mainExe: p.main, configPath: p.cfg, logPath: path.join(p.log, "{YYYY-MM-DD}.log"), gameExe: p.main });
  expect(good.ok, "真实路径且日志格式合规通过（存在性校验不误伤）").toBeTruthy();
  await api("DELETE", "/api/scripts/" + (await good.json()).id);
});

test("v0.2.0：幽灵联动 / 字段改名 / fs 浏览 / 通知开关 / 时间选择器", async ({ page }) => {
  await page.goto(baseUrl + "#/scripts", { waitUntil: "domcontentloaded" });
  await page.waitForSelector("h2");
  await page.click('[data-testid="new-script"]');
  await page.waitForSelector(".new-script-chooser", { timeout: 5000 });
  await page.click('[data-action="open-script-type"][data-plugin=""]');
  await page.waitForSelector("#sm-root");
  const exeDisabled = await page.$eval("#sm-exe", el => el.disabled);
  const argsDisabled = await page.$eval("#sm-args", el => el.disabled);
  const logDisabled = await page.$eval("#sm-log", el => el.disabled);
  expect(exeDisabled && argsDisabled && logDisabled, "根目录未填时主程序/参数/日志输入禁用（幽灵状态）").toBeTruthy();
  const vDir = makeScriptDir("v020");
  await page.fill("#sm-root", vDir.root.replace(/\\/g, "\\\\"));
  await page.waitForFunction(() => document.querySelector("#sm-exe") && !document.querySelector("#sm-exe").disabled, null, { timeout: 5000 });
  const exeEnabled = await page.$eval("#sm-exe", el => !el.disabled);
  expect(exeEnabled, "填写根目录后输入启用").toBeTruthy();
  expect((await page.textContent("body")).includes("日志路径"), "日志字段已改名「日志路径（支持日期占位符与通配符）」").toBeTruthy();
  await page.click(".modal button:has-text('取消')");

  const fsList = await (await fetch(baseUrl + "api/fs/browse")).json();
  expect((fsList.dirs || []).some(d => /^C:\\$/.test(d)), "fs browse 返回盘符列表（含 C:\\）").toBeTruthy();
  const fsSub = await (await fetch(baseUrl + "api/fs/browse?path=" + encodeURIComponent("C:\\"))).json();
  expect(Array.isArray(fsSub.dirs) && Array.isArray(fsSub.files), "fs browse 返回目录与文件列表").toBeTruthy();

  const put = await api("PUT", "/api/settings", { webhookEnabled: true, smtpEnabled: true });
  expect(put.ok, "PUT 设置通知开关成功").toBeTruthy();
  const got = await (await fetch(baseUrl + "api/settings")).json();
  const gWh = got.settings.webhookEnabled;
  const gSm = got.settings.smtpEnabled;
  expect(gWh === true && gSm === true, "GET 返回通知开关一致（webhook=" + gWh + " smtp=" + gSm + "）").toBeTruthy();
  await api("PUT", "/api/settings", { smtpEnabled: false });

  await page.click('nav a[href="#/settings"]');
  await page.waitForSelector("#st-port");
  const setBody = await page.textContent("body");
  expect(!setBody.includes("发送策略"), "设置页已无发送策略").toBeTruthy();
  expect(!setBody.includes("Webhook 通知"), "设置页不再包含通知配置（已移至插件配置）").toBeTruthy();

  await page.click('nav a[href="#/queues"]');
  await page.waitForSelector("h2");
  await page.click("button:has-text('新建调度队列')");
  await page.waitForSelector("#qm-name");
  const tsType = await page.$eval("[data-ts-time='0']", el => el.type);
  expect(tsType === "time", "定时执行时间为原生时间选择器（type=time）").toBeTruthy();
  await page.click(".modal button:has-text('取消')");
  await page.click('nav a[href="#/scripts"]');
  await page.waitForSelector("h2");
});

function scriptHistoryLog(scriptId) {
  const historyRoot = path.join(runtimeDir, "history");
  if (!fs.existsSync(historyRoot)) return "";
  const dirs = fs.readdirSync(historyRoot).filter(d => /^\d{4}-\d{2}-\d{2}$/.test(d)).sort().reverse();
  for (const dir of dirs) {
    const files = fs.readdirSync(path.join(historyRoot, dir)).filter(f => f.endsWith(".json")).sort();
    for (const f of files) {
      const rec = JSON.parse(fs.readFileSync(path.join(historyRoot, dir, f), "utf8").replace(/^\uFEFF/, ""));
      if (rec.ScriptInstanceId === scriptId) {
        const logFile = path.join(historyRoot, dir, f.replace(".json", "-1.log"));
        return fs.existsSync(logFile) ? fs.readFileSync(logFile, "utf8").replace(/^\uFEFF/, "") : "";
      }
    }
  }
  return "";
}

test("专用插件：BetterGI 适配 / probe / 简化弹窗 / 新建卡片 / 图标", async ({ page }) => {
  const bgiRoot = path.join(runtimeDir, "sim-bettergi");
  fs.rmSync(bgiRoot, { recursive: true, force: true });
  fs.mkdirSync(path.join(bgiRoot, "log"), { recursive: true });
  fs.writeFileSync(path.join(bgiRoot, "BetterGI.exe"), "");

  const st = await (await fetch(baseUrl + "api/status")).json();
  const bgi = (st.plugins || []).find(p => p.name === "bettergi");
  expect(bgi && bgi.kind === "specialized" && bgi.enabled, "BetterGI 专用插件已加载且启用（kind=specialized）").toBeTruthy();
  expect(bgi && bgi.gameName === "原神", "BetterGI 插件提供游戏名（gameName=原神）").toBeTruthy();

  const probeOk = await api("POST", "/api/scripts/probe", { rootPath: bgiRoot.replace(/\\/g, "\\\\"), pluginType: "bettergi" });
  const profile = (await probeOk.json()).profile;
  expect(probeOk.ok && profile.mainExe.endsWith("BetterGI.exe"), "probe 推导出主程序路径").toBeTruthy();
  expect(profile.args === "--startOneDragon", "probe 推导出自启动参数 --startOneDragon").toBeTruthy();
  expect(profile.configPath.includes("NexusPipeline.json"), "probe 推导出配置文件路径（NexusPipeline.json）").toBeTruthy();
  expect(profile.logPath.endsWith("better-genshin-impact.log"), "probe 推导出日志路径（Serilog 当前文件 better-genshin-impact.log，带日期为归档）").toBeTruthy();
  expect(profile.successMarkers === "", "probe 无完成标志（BetterGI 判定由判断脚本驱动）").toBeTruthy();
  expect(profile.judgeScript && profile.judgeScript.includes("一条龙和配置组任务结束"), "probe 提供判断脚本（含运行结束关键字）").toBeTruthy();
  const probeBad = await api("POST", "/api/scripts/probe", { rootPath: path.join(runtimeDir, "no-bgi"), pluginType: "bettergi" });
  expect(probeBad.status === 400, "probe 对无法推导的根目录返回 400").toBeTruthy();

  const created = await api("POST", "/api/scripts", { name: "专项脚本A", rootPath: bgiRoot.replace(/\\/g, "\\\\"), pluginType: "bettergi", gameExe: PING_GAME, maxAttempts: 1, logStallTimeoutMinutes: 5, totalTimeoutMinutes: 120 });
  expect(created.ok, "API 创建专用脚本实例成功").toBeTruthy();
  const sid = (await created.json()).id;
  const list = await (await fetch(baseUrl + "api/scripts")).json();
  const got = list.find(s => s.id === sid);
  expect(got && got.pluginType === "bettergi", "专用实例保存 pluginType=bettergi").toBeTruthy();
  expect(got.mainExe.endsWith("BetterGI.exe") && got.args === "--startOneDragon", "主程序/自启动参数由插件固化").toBeTruthy();
  expect(got.configPath.includes("NexusPipeline.json") && got.logPath.endsWith("better-genshin-impact.log"), "配置/日志路径由插件固化").toBeTruthy();
  expect(got.successMarkers === "", "专项实例不再固化完成标志（判定由判断脚本驱动）").toBeTruthy();
  const cfg = JSON.parse(fs.readFileSync(path.join(runtimeDir, "config", "scripts.json"), "utf8").replace(/^\uFEFF/, ""));
  const cfgGot = cfg.find(s => s.Id === sid);
  expect(cfgGot && cfgGot.PluginType === "bettergi", "scripts.json 落盘 PluginType（PascalCase）").toBeTruthy();
  expect(fs.readFileSync(path.join(runtimeDir, "config", "scripts.json"), "utf8").includes("专项脚本A"), "scripts.json 中文以原字符落盘（无 \\u 转义）").toBeTruthy();

  const bad = await api("POST", "/api/scripts", { name: "专项脚本B", rootPath: path.join(runtimeDir, "no-bgi"), pluginType: "bettergi", maxAttempts: 1, logStallTimeoutMinutes: 5, totalTimeoutMinutes: 120 });
  expect(bad.status === 400, "根目录无法推导时创建被拒（400）").toBeTruthy();

  const iconOk = await createScript({ name: "图标脚本", rootPath: runtimeDir, mainExe: "C:\\Windows\\explorer.exe", configPath: runtimeDir, logPath: runtimeDir });
  expect(iconOk.ok, "创建图标测试脚本（mainExe 为带高分辨率图标的系统 exe）").toBeTruthy();
  const iconRes = await fetch(baseUrl + "api/scripts/" + iconOk.id + "/icon");
  expect(iconRes.status === 200 && (iconRes.headers.get("content-type") || "").includes("image/png"), "图标 API 返回 PNG").toBeTruthy();
  const iconBytes = Buffer.from(await iconRes.arrayBuffer());
  expect(iconBytes.length > 24 && iconBytes.readUInt32BE(16) >= 48, "图标 API 返回最高分辨率图标（PNG 宽度 ≥ 48）").toBeTruthy();
  await api("DELETE", "/api/scripts/" + iconOk.id);
  const noIconBat = path.join(runtimeDir, "no-icon.bat");
  fs.writeFileSync(noIconBat, "@echo off\r\nexit /b 0\r\n", "ascii");
  const noIcon = await createScript({ name: "无图标脚本", rootPath: runtimeDir, mainExe: noIconBat.replace(/\\/g, "\\\\"), configPath: runtimeDir, logPath: runtimeDir });
  const icon404 = await fetch(baseUrl + "api/scripts/" + noIcon.id + "/icon");
  expect(icon404.status === 404, "主程序无图标资源时图标 API 返回 404").toBeTruthy();
  await api("DELETE", "/api/scripts/" + noIcon.id);

  await page.goto(baseUrl + "#/dashboard", { waitUntil: "domcontentloaded" });
  await page.goto(baseUrl + "#/scripts", { waitUntil: "domcontentloaded" });
  await page.waitForFunction(() => document.body.textContent.includes("专项脚本A"), null, { timeout: 5000 });
  const specialCard = await page.$$eval('[data-testid="script-card"]', els => {
    const el = els.find(e => e.textContent.includes("专项脚本A"));
    return el ? el.textContent : "";
  });
  expect(specialCard.includes("原神专项"), "脚本卡片显示专项标识（原神专项，游戏名由插件提供）").toBeTruthy();
  expect(await page.$('[data-testid="script-card"] img.script-ico'), "脚本卡片含主程序图标（img）").toBeTruthy();

  await page.click('[data-testid="new-script"]');
  await page.waitForSelector(".new-script-chooser", { timeout: 5000 });
  const chooserText = await page.textContent(".new-script-chooser");
  expect(chooserText.includes("新建通用脚本实例") && chooserText.includes("新建BetterGI专项脚本实例"), "选择卡片层含「新建通用脚本实例」与「新建BetterGI专项脚本实例」两张卡片").toBeTruthy();
  await page.click('[data-action="open-script-type"][data-plugin="bettergi"]');
  await page.waitForSelector("#sm-name");
  expect(!(await page.$("#sm-exe")) && !(await page.$("#sm-args")) && !(await page.$("#sm-config")) && !(await page.$("#sm-log")), "简化弹窗移除主程序/参数/配置/日志字段").toBeTruthy();
  await page.fill("#sm-name", "专项UI脚本");
  await page.fill("#sm-root", bgiRoot.replace(/\\/g, "\\\\"));
  await page.fill("#sm-game-exe", PING_GAME);
  await page.click(".modal button:has-text('保存')");
  await page.waitForFunction(() => document.body.textContent.includes("专项UI脚本"), null, { timeout: 5000 });
  expect(true, "简化弹窗保存成功（根目录 change 触发 probe 校验）").toBeTruthy();
  await page.click('[data-action="edit-script"][data-id="' + sid + '"]');
  await page.waitForSelector("#sm-name");
  expect(!(await page.$("#sm-exe")), "编辑专用实例仍为简化弹窗（无主程序字段）").toBeTruthy();
  await page.click(".modal button:has-text('取消')");

  await page.click('[data-action="delete-script"][data-name="专项UI脚本"]');
  await page.waitForSelector(".modal-mask .modal", { timeout: 5000 });
  expect((await page.textContent(".modal")).includes("确定删除脚本实例"), "删除专项 UI 脚本弹出确认卡片").toBeTruthy();
  await page.click('[data-action="confirm-delete-script"]');
  await waitAbsent(page, "专项UI脚本");
  expect(true, "删除专项 UI 脚本成功").toBeTruthy();
  await api("DELETE", "/api/scripts/" + sid);
});

test("专项脚本编辑配置：模板生成/隐藏默认配置/cancel 恢复/done 快照/残留自愈", async () => {
  const root = path.join(runtimeDir, "sim-bgi-edit");
  fs.rmSync(root, { recursive: true, force: true });
  fs.mkdirSync(path.join(root, "User", "OneDragon"), { recursive: true });
  fs.copyFileSync("C:\\Windows\\System32\\cmd.exe", path.join(root, "BetterGI.exe"));
  fs.writeFileSync(path.join(root, "User", "OneDragon", "默认配置.json"), JSON.stringify({ Name: "默认配置", TaskEnabledList: {} }), "utf8");
  const created = await api("POST", "/api/scripts", { name: "编辑配置模板", pluginType: "bettergi", rootPath: root.replace(/\\/g, "\\\\"), gameExe: PING_GAME, maxAttempts: 1, logStallTimeoutMinutes: 5, totalTimeoutMinutes: 30 });
  expect(created.ok, "API 创建专项脚本（cmd 冒充 BetterGI.exe）").toBeTruthy();
  const sp = await created.json();
  await api("POST", `/api/scripts/${sp.id}/users`, { name: "默认", enabled: true });
  const cfgPath = path.join(root, "User", "OneDragon", "NexusPipeline.json");
  const defaultCfg = path.join(root, "User", "OneDragon", "默认配置.json");
  const editBase = `/api/scripts/${sp.id}/users/${encodeURIComponent("默认")}/edit-config`;

  const start = await api("POST", editBase, { action: "start" });
  expect(start.ok, "首次编辑配置 start 成功（生成模板）").toBeTruthy();
  expect(fs.existsSync(cfgPath) && !fs.statSync(cfgPath).isDirectory(), "首次编辑生成 NexusPipeline.json 文件（非目录）").toBeTruthy();
  expect(!fs.existsSync(defaultCfg), "编辑期间默认配置被隐藏（BetterGI 仅 NexusPipeline 可选）").toBeTruthy();
  const text = fs.readFileSync(cfgPath, "utf8");
  expect(text.includes('"TaskEnabledList"') && text.includes('"NexusPipeline"'), "模板含任务列表结构与配置名").toBeTruthy();
  expect(text.includes('"TaskDefinitions"') && text.includes('"CompletionAction"'), "模板含任务定义与完成动作键").toBeTruthy();

  const cancel = await api("POST", editBase, { action: "cancel" });
  expect(cancel.ok, "取消编辑成功").toBeTruthy();
  expect(!fs.existsSync(cfgPath), "cancel 清理本次生成的模板").toBeTruthy();
  expect(fs.existsSync(defaultCfg), "cancel 后默认配置恢复").toBeTruthy();

  const start2 = await api("POST", editBase, { action: "start" });
  expect(start2.ok, "再次 start 成功（快照恢复）").toBeTruthy();
  expect(fs.existsSync(cfgPath) && !fs.statSync(cfgPath).isDirectory(), "再次 start NexusPipeline.json 为文件（快照恢复，非目录残留）").toBeTruthy();
  fs.writeFileSync(cfgPath, JSON.stringify({ Name: "用户配置", TaskEnabledList: {} }, null, 2), "utf8");
  const done = await api("POST", editBase, { action: "done" });
  expect(done.ok, "完成编辑成功").toBeTruthy();
  expect(!fs.existsSync(cfgPath), "done 后 config 位置清理（还原语义）").toBeTruthy();
  expect(fs.existsSync(defaultCfg), "done 后默认配置恢复").toBeTruthy();
  const store = path.join(runtimeDir, "data", sp.id, "默认", "store");
  expect(fs.existsSync(path.join(store, "NexusPipeline.json")), "编辑产物已入库用户快照").toBeTruthy();
  expect(fs.readFileSync(path.join(store, "NexusPipeline.json"), "utf8").includes("用户配置"), "快照内容为编辑后的配置").toBeTruthy();

  fs.mkdirSync(cfgPath);
  fs.writeFileSync(path.join(cfgPath, "NexusPipeline.json"), "{}", "utf8");
  const start3 = await api("POST", editBase, { action: "start" });
  expect(start3.ok, "残留目录场景 start 成功").toBeTruthy();
  expect(fs.existsSync(cfgPath) && !fs.statSync(cfgPath).isDirectory(), "残留目录被清理并生成模板文件（自愈）").toBeTruthy();
  const cancel2 = await api("POST", editBase, { action: "cancel" });
  expect(cancel2.ok, "自愈会话取消成功").toBeTruthy();

  await api("DELETE", "/api/scripts/" + sp.id);
});

test("编辑配置会话：弹窗锁定 / 刷新后恢复锁定弹窗 / 重启后配置恢复", async ({ page }) => {
  const root = path.join(runtimeDir, "sim-bgi-lock");
  fs.rmSync(root, { recursive: true, force: true });
  fs.mkdirSync(path.join(root, "User", "OneDragon"), { recursive: true });
  fs.copyFileSync("C:\\Windows\\System32\\cmd.exe", path.join(root, "BetterGI.exe"));
  fs.writeFileSync(path.join(root, "User", "OneDragon", "默认配置.json"), JSON.stringify({ Name: "默认配置", TaskEnabledList: {} }), "utf8");
  const created = await api("POST", "/api/scripts", { name: "编辑会话锁定", pluginType: "bettergi", rootPath: root.replace(/\\/g, "\\\\"), gameExe: PING_GAME, maxAttempts: 1, logStallTimeoutMinutes: 5, totalTimeoutMinutes: 30 });
  expect(created.ok, "API 创建专项脚本（cmd 冒充 BetterGI.exe）").toBeTruthy();
  const sp = await created.json();
  await api("POST", `/api/scripts/${sp.id}/users`, { name: "默认", enabled: true });
  const cfgPath = path.join(root, "User", "OneDragon", "NexusPipeline.json");
  const defaultCfg = path.join(root, "User", "OneDragon", "默认配置.json");
  const editBase = `/api/scripts/${sp.id}/users/${encodeURIComponent("默认")}/edit-config`;

  const start = await api("POST", editBase, { action: "start" });
  expect(start.ok, "编辑配置 start 成功").toBeTruthy();
  await page.goto(baseUrl + `#/scripts/${sp.id}/users`, { waitUntil: "domcontentloaded" });
  await page.waitForSelector(".modal", { timeout: 5000 });
  expect((await page.textContent(".modal")).includes("配置编辑中"), "刷新后自动恢复「配置编辑中」锁定卡片").toBeTruthy();
  await page.keyboard.press("Escape");
  expect(await page.$(".modal"), "锁定弹窗：Esc 无法关闭").toBeTruthy();
  await page.mouse.click(20, 400);
  expect(await page.$(".modal"), "锁定弹窗：点击遮罩无法关闭").toBeTruthy();
  expect(!(await page.isVisible(".modal-close")), "锁定弹窗：无关闭按钮").toBeTruthy();
  await page.click('[data-action="edit-config-cancel"]');
  await page.waitForSelector(".modal-mask", { state: "detached", timeout: 5000 });
  expect(!fs.existsSync(cfgPath), "取消后本次生成的模板已清理").toBeTruthy();
  expect(fs.existsSync(defaultCfg), "取消后默认配置恢复").toBeTruthy();

  const start2 = await api("POST", editBase, { action: "start" });
  expect(start2.ok, "再次 start 成功（重启恢复前置）").toBeTruthy();
  expect(fs.existsSync(cfgPath), "模板已生成").toBeTruthy();
  await restartService();
  expect(!fs.existsSync(cfgPath), "重启后编辑会话生成的模板已清理（恢复编辑前状态）").toBeTruthy();
  expect(fs.existsSync(defaultCfg), "重启后隐藏的默认配置已恢复").toBeTruthy();

  await api("DELETE", "/api/scripts/" + sp.id);
});

test("数据目录命名迁移：旧名残留（config/cache/edit-hide）迁移为新名且崩溃现场可恢复", async () => {
  const migrateDir = makeScriptDir("migrate");
  const create = await api("POST", "/api/scripts", {
    name: "迁移测试脚本", rootPath: migrateDir.root.replace(/\\/g, "\\\\"),
    mainExe: migrateDir.main.replace(/\\/g, "\\\\"),
    configPath: migrateDir.cfg.replace(/\\/g, "\\\\"), logPath: migrateDir.log.replace(/\\/g, "\\\\"), gameExe: PING_GAME,
    maxAttempts: 1, logStallTimeoutMinutes: 5, totalTimeoutMinutes: 120,
  });
  const script = await create.json();
  const sid = script.id;
  const uDir = path.join(runtimeDir, "data", sid, "甲");
  fs.mkdirSync(uDir, { recursive: true });
  const cfgFile = path.join(migrateDir.cfg, "configA.txt");
  fs.writeFileSync(cfgFile, "CURRENT", "utf8");

  fs.mkdirSync(path.join(uDir, "config"), { recursive: true });
  fs.writeFileSync(path.join(uDir, "config", "configA.txt"), "STORED", "utf8");
  fs.mkdirSync(path.join(uDir, "cache"), { recursive: true });
  fs.writeFileSync(path.join(uDir, "cache", "configA.txt"), "ORIGINAL", "utf8");
  fs.mkdirSync(path.join(uDir, "edit-hide"), { recursive: true });
  fs.writeFileSync(path.join(uDir, "edit-hide", "other.json"), "{}", "utf8");
  fs.writeFileSync(path.join(uDir, ".session"), JSON.stringify({
    scriptId: sid, userName: "甲", configPath: migrateDir.cfg, originalKind: "dir",
    phase: "run", generatedTemplate: false,
  }), "utf8");

  await restartService();

  expect(fs.readFileSync(path.join(uDir, "store", "configA.txt"), "utf8") === "STORED", "旧 config/ 已迁移为 store/").toBeTruthy();
  expect(!fs.existsSync(path.join(uDir, "config")), "旧 config/ 目录已不存在").toBeTruthy();
  expect(!fs.existsSync(path.join(uDir, "cache")), "旧 cache/ 目录已不存在").toBeTruthy();
  expect(!fs.existsSync(path.join(uDir, "edit-hide")), "旧 edit-hide/ 目录已不存在（迁移为 edit-hidden 并由会话恢复消费）").toBeTruthy();
  expect(!fs.existsSync(path.join(uDir, "edit-hidden")), "edit-hidden 恢复后已清理").toBeTruthy();
  expect(fs.readFileSync(cfgFile, "utf8") === "ORIGINAL", "迁移后的崩溃现场已恢复（原配置还原到配置路径）").toBeTruthy();
  expect(!fs.existsSync(path.join(uDir, ".session")), "崩溃现场恢复后 .session 标记已清除").toBeTruthy();
  expect(!fs.existsSync(path.join(uDir, "original")) || fs.readdirSync(path.join(uDir, "original")).length === 0, "恢复后 original 已清空（内容已移回配置路径）").toBeTruthy();

  await api("DELETE", "/api/scripts/" + sid);
});
