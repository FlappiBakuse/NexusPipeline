import { spawn, spawnSync } from "node:child_process";
import fs from "node:fs";
import path from "node:path";
import { test, expect } from "@playwright/test";
import { baseUrl, PING_GAME, runtimeDir, runtimeExe, makeScriptDir, createScript, api, waitFor, waitNoRunning, waitAbsent, localDate, restartService, stopService, startService, waitForService, ensureService } from "./helpers.mjs";

await ensureService();

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
  expect(await page.$(".field-error-text"), "字段错误不插入会改变布局的错误文字").toBeFalsy();
  expect(await page.$("#sm-name.field-error"), "字段错误仅以红色输入框高亮").toBeTruthy();
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

  const editedScriptCard = page.locator('[data-testid="script-card"]').filter({ hasText: "测试脚本A-改" }).first();
  await editedScriptCard.locator(".overflow-trigger").click();
  await editedScriptCard.locator('[role="menuitem"][data-action="delete-script"]').click();
  await page.waitForSelector(".modal-mask .modal", { timeout: 5000 });
  expect((await page.textContent(".modal")).includes("确定删除脚本实例"), "删除脚本弹出确认卡片（含确定/取消）").toBeTruthy();
  await page.click('[data-action="confirm-delete-script"]');
  await waitAbsent(page, "测试脚本A-改");
  expect(!(await page.textContent("body")).includes("测试脚本A-改"), "删除后列表不再显示该脚本").toBeTruthy();
});

test("POST 注入已存在 Id：新建一律重新生成，不产生重复记录（v0.7.1 KN-02）", async () => {
  const dir = makeScriptDir("dupid");
  const post = (name, id, logName) => api("POST", "/api/scripts", {
    id, name, rootPath: dir.root, mainExe: dir.main, configPath: dir.cfg,
    logPath: path.join(dir.log, logName), gameExe: PING_GAME,
    maxAttempts: 1, logStallTimeoutMinutes: 5, totalTimeoutMinutes: 120,
  });
  const first = await (await post("dup-id-first", "", "run1.log")).json();
  const injected = await post("dup-id-injected", first.id, "run2.log");
  expect(injected.ok, "注入已存在 Id 的新建请求被接受").toBeTruthy();
  const second = await injected.json();
  expect(second.id, "新建脚本 Id 重新生成，不等于注入的已存在 Id").not.toBe(first.id);
  const scripts = await (await api("GET", "/api/scripts")).json();
  expect(scripts.filter(s => s.id === first.id).length, "集合中不存在重复 Id 记录").toBe(1);
  for (const s of scripts.filter(s => s.name.startsWith("dup-id-"))) {
    await api("DELETE", "/api/scripts/" + s.id);
  }
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
  expect(await page.$('[data-action="manage-users"]'), "脚本卡片提供直接用户入口").toBeTruthy();

  await page.click('[data-action="manage-users"]');
  await page.waitForFunction(() => document.body.textContent.includes("添加用户"), null, { timeout: 5000 });
  body = await page.textContent("body");
  expect(body.includes("返回脚本实例"), "用户管理页左上角有返回箭头").toBeTruthy();
  expect(body.includes("暂无用户"), "无用户时显示空状态").toBeTruthy();

  await page.click("button:has-text('添加用户')");
  await page.waitForSelector("#um-name");
  await page.fill("#um-name", "甲");
  await page.click(".modal button:has-text('保存')");
  await page.waitForSelector(".modal-mask", { state: "detached", timeout: 5000 });
  await page.waitForSelector('.user-card[data-dnd-id="甲"]', { timeout: 5000 });
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
  const userTestScriptCard = page.locator('[data-testid="script-card"]').filter({ hasText: "用户测试脚本" }).first();
  await userTestScriptCard.locator(".overflow-trigger").click();
  await userTestScriptCard.locator('[role="menuitem"][data-action="delete-script"]').click();
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
  // 防御清理（v0.6.9+ F2）：先前失败残留的「排序脚本/排序门禁脚本」按名称删除，避免用户数/卡片断言失准
  const staleList = await (await fetch(baseUrl + "api/scripts")).json();
  for (const s of staleList) {
    if (s.name === "排序脚本" || s.name === "排序门禁脚本") {
      try { await api("DELETE", "/api/scripts/" + s.id); } catch { /* 清理失败不阻塞 */ }
    }
  }
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
  let sid2 = null;
  try {
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
  sid2 = (await created2.json()).id;
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

  // 拖拽排序（v0.6.8+）：把手拖动到目标卡片顶部；re-render 竞态下 boundingBox 可能为 null，重试一次
  const dragUser = async (name, toBox) => {
    let lastError = null;
    for (let attempt = 0; attempt < 3; attempt++) {
      let mouseDown = false;
      try {
        // 用户列表会被轮询刷新；每次重试都重新创建 locator，避免复用已脱离 DOM 的句柄。
        const handle = page.locator(`.user-card[data-dnd-id="${name}"] .drag-handle`);
        await handle.waitFor({ timeout: 10000 });
        await handle.scrollIntoViewIfNeeded();
        let box = await handle.boundingBox();
        if (!box) { await page.waitForTimeout(400); box = await handle.boundingBox(); }
        if (!box) throw new Error("拖拽把手不可见：" + name);
        await page.mouse.move(box.x + box.width / 2, box.y + box.height / 2);
        await page.mouse.down();
        mouseDown = true;
        await page.mouse.move(toBox.x + toBox.width / 2, toBox.y + 2, { steps: 8 });
        await page.mouse.up();
        return;
      } catch (error) {
        lastError = error;
        if (mouseDown) await page.mouse.up().catch(() => {});
        await page.waitForTimeout(500);
      }
    }
    throw lastError;
  };

  const targetBox = async (selector) => {
    const locator = page.locator(selector);
    await locator.waitFor({ timeout: 10000 });
    let box = await locator.boundingBox();
    if (!box) { await page.waitForTimeout(400); box = await locator.boundingBox(); }
    if (!box) throw new Error("拖拽目标不可见：" + selector);
    return box;
  };

  await dragUser("丙", await targetBox('.user-card[data-dnd-id="甲"]'));
  await page.waitForFunction(() => { const cards = Array.from(document.querySelectorAll(".user-card .list-item-title .user-name-link")); return cards.length === 3 && cards[0].textContent.trim() === "丙"; }, null, { timeout: 10000 });
  expect(true, "拖拽后 丙 成为第一位（卡片顺序更新）").toBeTruthy();
  list = await (await fetch(baseUrl + "api/scripts")).json();
  got = list.find(s => s.id === sid);
  expect(got.users.map(u => u.name).join() === "丙,甲,乙", "UI 拖拽后顺序已落盘（丙,甲,乙）").toBeTruthy();

  await dragUser("乙", await targetBox('.user-card[data-dnd-id="丙"]'));
  await page.waitForFunction(() => { const cards = Array.from(document.querySelectorAll(".user-card .list-item-title .user-name-link")); return cards.length === 3 && cards[0].textContent.trim() === "乙"; }, null, { timeout: 10000 });
  expect(true, "拖拽后 乙 成为第一位（卡片顺序更新）").toBeTruthy();
  list = await (await fetch(baseUrl + "api/scripts")).json();
  got = list.find(s => s.id === sid);
  expect(got.users.map(u => u.name).join() === "乙,丙,甲", "UI 拖拽后顺序已落盘（乙,丙,甲）").toBeTruthy();
  const user乙Card = page.locator('.user-card[data-dnd-id="乙"]').first();
  await user乙Card.locator(".overflow-trigger").click();
  await user乙Card.locator('[role="menuitem"][data-action="delete-user"]').click();
  await page.waitForSelector(".modal-mask .modal", { timeout: 5000 });
  expect((await page.textContent(".modal")).includes("确定删除用户"), "删除用户先弹出确认卡片").toBeTruthy();
  await page.click('[data-action="confirm-delete-user"]');
  await page.waitForSelector(".modal-mask", { state: "detached", timeout: 5000 });
  expect(!(await page.$('.user-card[data-dnd-id="乙"]')), "删除用户成功后确认弹窗关闭且用户卡片消失").toBeTruthy();
  } finally {
    // v0.6.9+ F2 根治：删除需确认成功（res.ok）+ 轮询确认从列表消失（此前仅「请求无异常」即 break，
    // DELETE 返回非 2xx 时残留导致后续卡片数断言失准）；sid2 未赋值（try 内提前失败）时跳过，避免删 null。
    const targets = [sid, sid2].filter(id => !!id);
    for (const target of targets) {
      for (let attempt = 0; attempt < 3; attempt++) {
        try {
          const res = await api("DELETE", "/api/scripts/" + target);
          if (!res.ok) { await new Promise(r => setTimeout(r, 300)); continue; }
          const gone = await waitFor(async () => {
            const list = await (await fetch(baseUrl + "api/scripts")).json();
            return !list.some(s => s.id === target);
          }, 5000, 300);
          if (gone) break;
        } catch { /* 重试 */ }
        await new Promise(r => setTimeout(r, 300));
      }
    }
  }
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
  const safeConfig = path.join(runtimeDir, "force-close-config");
  fs.rmSync(safeConfig, { recursive: true, force: true });
  fs.mkdirSync(safeConfig, { recursive: true });
  const created = await api("POST", "/api/scripts", {
    name: "强制关闭解绑脚本", rootPath: runtimeDir, mainExe: exitBat.replace(/\\/g, "\\\\"),
    configPath: safeConfig, logPath: path.join(safeConfig, "log.txt"), launchGame: false, gameExe: PING_GAME, forceCloseGame: true,
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
  const f1 = await api("POST", "/api/scripts", { name: "失败杀游戏脚本", mainExe: failBat.replace(/\\/g, "\\\\"), successKeywords: "NEVER-SEEN-MARKER", ...base });
  expect(f1.ok, "创建失败杀游戏脚本（完成关键字永不出现 → 任务失败需强制结束游戏）").toBeTruthy();
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
  expect(fsSub.error && fsSub.error.includes("允许浏览范围"), "fs browse 白名单：未配置脚本时任意目录浏览被拒（403）").toBeTruthy();

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
  expect(setBody.includes("Webhook 通知") && setBody.includes("SMTP 邮件通知"), "设置页包含宿主内置 Webhook 与 SMTP 通知配置").toBeTruthy();

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
  expect(bgi && bgi.kind === "data-specialized" && bgi.configuredEnabled && bgi.runtimeEnabled, "BetterGI 数据化专项插件已加载且启用").toBeTruthy();
  expect(bgi && bgi.gameName === "原神", "BetterGI 插件提供游戏名（gameName=原神）").toBeTruthy();

  const probeOk = await api("POST", "/api/scripts/probe", { rootPath: bgiRoot.replace(/\\/g, "\\\\"), pluginType: "bettergi" });
  const profile = (await probeOk.json()).profile;
  expect(probeOk.ok && profile.mainExe.endsWith("BetterGI.exe"), "probe 推导出主程序路径").toBeTruthy();
  expect(profile.args === "--startOneDragon", "probe 推导出自启动参数 --startOneDragon").toBeTruthy();
  expect(profile.configPath.includes("NexusPipeline.json"), "probe 推导出配置文件路径（NexusPipeline.json）").toBeTruthy();
  expect(profile.logPath.endsWith("better-genshin-impact.log"), "probe 推导出日志路径（Serilog 当前文件 better-genshin-impact.log，带日期为归档）").toBeTruthy();
  expect(profile.successMarkers === undefined, "probe 不再返回完成标志字段（已废弃）").toBeTruthy();
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
  expect(got.successMarkers === undefined, "专项实例不再返回完成标志字段（已废弃）").toBeTruthy();
  const cfg = JSON.parse(fs.readFileSync(path.join(runtimeDir, "config", "scripts.json"), "utf8").replace(/^\uFEFF/, ""));
  const cfgGot = cfg.find(s => s.Id === sid);
  expect(cfgGot && cfgGot.PluginType === "bettergi", "scripts.json 落盘 PluginType（PascalCase）").toBeTruthy();
  expect(fs.readFileSync(path.join(runtimeDir, "config", "scripts.json"), "utf8").includes("专项脚本A"), "scripts.json 中文以原字符落盘（无 \\u 转义）").toBeTruthy();

  const bad = await api("POST", "/api/scripts", { name: "专项脚本B", rootPath: path.join(runtimeDir, "no-bgi"), pluginType: "bettergi", maxAttempts: 1, logStallTimeoutMinutes: 5, totalTimeoutMinutes: 120 });
  expect(bad.status === 400, "根目录无法推导时创建被拒（400）").toBeTruthy();

  const iconCfg = path.join(runtimeDir, "icon-cfg");
  fs.rmSync(iconCfg, { recursive: true, force: true });
  fs.mkdirSync(iconCfg, { recursive: true });
  const iconOk = await createScript({ name: "图标脚本", rootPath: runtimeDir, mainExe: "C:\\Windows\\explorer.exe", configPath: iconCfg, logPath: runtimeDir });
  expect(iconOk.ok, "创建图标测试脚本（mainExe 为带高分辨率图标的系统 exe）").toBeTruthy();
  const iconRes = await fetch(baseUrl + "api/scripts/" + iconOk.id + "/icon");
  expect(iconRes.status === 200 && (iconRes.headers.get("content-type") || "").includes("image/png"), "图标 API 返回 PNG").toBeTruthy();
  const iconBytes = Buffer.from(await iconRes.arrayBuffer());
  expect(iconBytes.length > 24 && iconBytes.readUInt32BE(16) >= 48, "图标 API 返回最高分辨率图标（PNG 宽度 ≥ 48）").toBeTruthy();
  const shell = await fetch(baseUrl);
  const csp = shell.headers.get("content-security-policy") || "";
  expect(/img-src[^;]*\bblob:/.test(csp), "页面 CSP 允许图标 Blob URL（img-src 包含 blob:）").toBeTruthy();
  await page.goto(baseUrl + "#/scripts", { waitUntil: "domcontentloaded" });
  await page.waitForFunction((id) => {
    const image = [...document.querySelectorAll("img[data-icon-id]")].find(el => el.dataset.iconId === id);
    return !!image && image.src.startsWith("blob:") && image.complete && image.naturalWidth > 0;
  }, sid, { timeout: 10000 });
  expect(true, "脚本卡片实际加载主程序图标（Blob URL）").toBeTruthy();
  const iconQueue = await api("POST", "/api/queues", {
    name: "图标队列", autoRunMode: "none", completionAction: "none", timeSets: [],
    tasks: [{ id: "", index: 0, scriptInstanceId: sid }],
  });
  const iconQueueId = (await iconQueue.json()).id;
  await page.goto(baseUrl + "#/queues", { waitUntil: "domcontentloaded" });
  await page.waitForFunction((id) => {
    const card = [...document.querySelectorAll('[data-testid="queue-card"]')].find(el => el.textContent.includes("图标队列"));
    const image = card?.querySelector("img[data-icon-id]");
    return !!image && image.dataset.iconId === id && image.src.startsWith("blob:") && image.complete && image.naturalWidth > 0;
  }, sid, { timeout: 10000 });
  expect(true, "调度队列卡片实际加载首个脚本图标（Blob URL）").toBeTruthy();
  await api("DELETE", "/api/queues/" + iconQueueId);
  await api("DELETE", "/api/scripts/" + iconOk.id);
  const noIconBat = path.join(runtimeDir, "no-icon.bat");
  fs.writeFileSync(noIconBat, "@echo off\r\nexit /b 0\r\n", "ascii");
  const noIcon = await createScript({ name: "无图标脚本", rootPath: runtimeDir, mainExe: noIconBat.replace(/\\/g, "\\\\"), configPath: iconCfg, logPath: runtimeDir });
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

  const specialUiScriptCard = page.locator('[data-testid="script-card"]').filter({ hasText: "专项UI脚本" }).first();
  await specialUiScriptCard.locator(".overflow-trigger").click();
  await specialUiScriptCard.locator('[role="menuitem"][data-action="delete-script"]').click();
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

test("编辑配置：文件被占用时提交失败、释放后重试成功且无残留", async () => {
  const root = path.join(runtimeDir, "sim-bgi-hold");
  fs.rmSync(root, { recursive: true, force: true });
  fs.mkdirSync(path.join(root, "User", "OneDragon"), { recursive: true });
  fs.copyFileSync("C:\\Windows\\System32\\cmd.exe", path.join(root, "BetterGI.exe"));
  fs.writeFileSync(path.join(root, "User", "OneDragon", "默认配置.json"), JSON.stringify({ Name: "默认配置", TaskEnabledList: {} }), "utf8");
  const created = await api("POST", "/api/scripts", { name: "编辑占用重试", pluginType: "bettergi", rootPath: root.replace(/\\/g, "\\\\"), gameExe: PING_GAME, maxAttempts: 1, logStallTimeoutMinutes: 5, totalTimeoutMinutes: 30 });
  expect(created.ok, "API 创建专项脚本（cmd 冒充 BetterGI.exe）").toBeTruthy();
  const sp = await created.json();
  await api("POST", `/api/scripts/${sp.id}/users`, { name: "默认", enabled: true });
  const cfgPath = path.join(root, "User", "OneDragon", "NexusPipeline.json");
  const defaultCfg = path.join(root, "User", "OneDragon", "默认配置.json");
  const editBase = `/api/scripts/${sp.id}/users/${encodeURIComponent("默认")}/edit-config`;

  const start = await api("POST", editBase, { action: "start" });
  expect(start.ok, "编辑配置 start 成功（生成模板）").toBeTruthy();
  fs.writeFileSync(cfgPath, JSON.stringify({ Name: "用户配置", TaskEnabledList: {} }), "utf8");

  // PowerShell 持只读共享句柄（FileShare.Read：允许复制、阻止删除——复现「复制成功、删除源失败」路径）；
  // READY 信号确认句柄已建立后再提交。
  const holder = spawn("powershell", ["-NoProfile", "-NonInteractive", "-Command",
    `Write-Output 'READY'; $fs = [System.IO.File]::Open('${cfgPath}', [System.IO.FileMode]::Open, [System.IO.FileAccess]::ReadWrite, [System.IO.FileShare]::Read); Start-Sleep 30`],
    { stdio: ["ignore", "pipe", "ignore"] });
  await new Promise(resolve => holder.stdout.once("data", resolve));
  await new Promise(r => setTimeout(r, 300));
  const done1 = await api("POST", editBase, { action: "done" });
  expect(done1.status === 400, "文件被占用时提交失败（400）").toBeTruthy();
  const errBody = await done1.json();
  expect(errBody.error.includes("提交失败"), "失败原因含「提交失败」（复制成功但删除源失败）").toBeTruthy();
  expect(fs.existsSync(cfgPath), "提交失败后配置文件仍在（未被误删）").toBeTruthy();
  expect(fs.existsSync(path.join(runtimeDir, "data", sp.id, "默认", ".session")), "提交失败后 .session 标记保留（自愈前提）").toBeTruthy();

  spawnSync("taskkill", ["/PID", String(holder.pid), "/F"], { stdio: "ignore" });
  await new Promise(r => setTimeout(r, 500));
  const done2 = await api("POST", editBase, { action: "done" });
  expect(done2.ok, "释放占用后重试提交成功").toBeTruthy();
  expect(!fs.existsSync(cfgPath), "提交成功后 config 位置无残留").toBeTruthy();
  expect(fs.existsSync(defaultCfg), "提交成功后默认配置恢复").toBeTruthy();
  const store = path.join(runtimeDir, "data", sp.id, "默认", "store");
  expect(fs.readFileSync(path.join(store, "NexusPipeline.json"), "utf8").includes("用户配置"), "编辑产物已入库用户快照").toBeTruthy();
  expect(!fs.existsSync(path.join(runtimeDir, "data", sp.id, "默认", ".session")), "提交成功后 .session 已清除").toBeTruthy();

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
  // v0.6.6+：恢复逻辑等待脚本进程退出；重启前清理孤儿进程（cmd 副本不随服务退出，会挡住启动恢复）。
  spawnSync("taskkill", ["/IM", "BetterGI.exe", "/F"], { stdio: "ignore" });
  await new Promise(r => setTimeout(r, 400));
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

test("CLI run-script：服务运行时经 HTTP 提交并轮询结果（退出码 0 + 记录输出）", async () => {
  const cliDir = path.join(runtimeDir, "cli-run");
  fs.rmSync(cliDir, { recursive: true, force: true });
  fs.mkdirSync(cliDir, { recursive: true });
  const cliLog = path.join(cliDir, "cli.log");
  const cliBat = path.join(cliDir, "nexustest-cli.bat");
  fs.writeFileSync(cliBat, "@echo off\r\necho CLI-RAN >> \"" + cliLog + "\"\r\nexit /b 0\r\n", "ascii");
  const created = await api("POST", "/api/scripts", {
    name: "CLI脚本", rootPath: cliDir.replace(/\\/g, "\\\\"), mainExe: cliBat.replace(/\\/g, "\\\\"),
    configPath: cliDir.replace(/\\/g, "\\\\"), logPath: cliLog.replace(/\\/g, "\\\\"), gameExe: PING_GAME,
    maxAttempts: 1, logStallTimeoutMinutes: 5, totalTimeoutMinutes: 120,
  });
  expect(created.ok, "创建 CLI 用例脚本").toBeTruthy();
  const cliScript = await created.json();
  await api("POST", `/api/scripts/${cliScript.id}/users`, { name: "默认", enabled: true });
  try {
    const r = spawnSync(runtimeExe, ["run-script", cliScript.id, "-manual"], { cwd: runtimeDir, encoding: "utf8", timeout: 90000 });
    const out = r.stdout || "";
    expect(r.status === 0, "CLI run-script 退出码 0（全部记录 success；stdout 尾部：" + out.slice(-160) + "）").toBeTruthy();
    expect(out.includes("===== CLI脚本 ====="), "CLI 输出含记录分隔行（===== CLI脚本 =====）").toBeTruthy();
    expect(out.includes("状态：success"), "CLI 输出含 success 状态行").toBeTruthy();
  } finally {
    await api("DELETE", "/api/scripts/" + cliScript.id);
  }
});

test("CLI run-script：服务未运行时自动拉起常驻服务并完成任务", async () => {
  const cliDir = path.join(runtimeDir, "cli-run2");
  fs.rmSync(cliDir, { recursive: true, force: true });
  fs.mkdirSync(cliDir, { recursive: true });
  const cliLog = path.join(cliDir, "cli.log");
  const cliBat = path.join(cliDir, "nexustest-cli.bat");
  fs.writeFileSync(cliBat, "@echo off\r\necho CLI-RAN >> \"" + cliLog + "\"\r\nexit /b 0\r\n", "ascii");
  const created = await api("POST", "/api/scripts", {
    name: "CLI拉起脚本", rootPath: cliDir.replace(/\\/g, "\\\\"), mainExe: cliBat.replace(/\\/g, "\\\\"),
    configPath: cliDir.replace(/\\/g, "\\\\"), logPath: cliLog.replace(/\\/g, "\\\\"), gameExe: PING_GAME,
    maxAttempts: 1, logStallTimeoutMinutes: 5, totalTimeoutMinutes: 120,
  });
  expect(created.ok, "创建 CLI 自动拉起用例脚本").toBeTruthy();
  const cliScript = await created.json();
  await api("POST", `/api/scripts/${cliScript.id}/users`, { name: "默认", enabled: true });
  await stopService();
  try {
    // 注意：CLI 自动拉起的常驻服务进程会继承 CLI 的 stdout 管道（spawnSync 会一直等到管道 EOF，
    // 而服务常驻导致 120s 超时）；改用异步 spawn + exit 事件（进程退出即返回，不依赖管道 EOF）。
    const cli = spawn(runtimeExe, ["run-script", cliScript.id], { cwd: runtimeDir });
    let out = "";
    cli.stdout.on("data", d => { out += d; });
    cli.stderr.on("data", d => { out += d; });
    const exitCode = await Promise.race([
      new Promise(resolve => cli.on("exit", code => resolve(code))),
      new Promise(resolve => setTimeout(() => { cli.kill(); resolve(null); }, 90000)),
    ]);
    expect(exitCode === 0, "服务未运行时 CLI 自动拉起服务并完成任务（退出码 0；stdout 尾部：" + out.slice(-160) + "）").toBeTruthy();
    expect(out.includes("正在自动拉起"), "CLI 提示自动拉起常驻服务").toBeTruthy();
    expect(out.includes("===== CLI拉起脚本 ====="), "CLI 输出含记录分隔行").toBeTruthy();
  } finally {
    // 清理 CLI 自动拉起的常驻服务（托盘模式，不写 pid 文件），再恢复标准测试服务
    try {
      spawnSync("powershell", ["-NoProfile", "-NonInteractive", "-Command",
        "$p = Get-CimInstance Win32_Process -Filter \"Name='nexus-pipeline.exe'\" | Where-Object { $_.ExecutablePath -like '*tests\\e2e\\runtime\\*' }; $p | ForEach-Object { Stop-Process -Id $_.ProcessId -Force }"],
        { stdio: "ignore" });
    } catch { /* 清理失败不阻塞（后续 startService 端口 +1 重试兜底） */ }
    await startService();
    await waitForService();
    try { await api("DELETE", "/api/scripts/" + cliScript.id); } catch { /* 清理失败不阻塞 */ }
  }
});

test("脚本卡片拖拽排序：页内拖拽落盘 + 名单校验", async ({ page }) => {
  // 清理先前用例失败残留的脚本（防御：残留会导致卡片数断言失准）
  const stale = await (await fetch(baseUrl + "api/scripts")).json();
  for (const item of stale) {
    try { await api("DELETE", "/api/scripts/" + item.id); } catch { /* 清理失败不阻塞 */ }
  }
  const dirs = ["dnd-a", "dnd-b", "dnd-c"].map(name => makeScriptDir(name));
  const ids = [];
  for (const dir of dirs) {
    const created = await api("POST", "/api/scripts", {
      name: "拖拽脚本" + dirs.indexOf(dir), rootPath: dir.root, mainExe: dir.main,
      configPath: dir.cfg, logPath: dir.log, gameExe: PING_GAME,
      maxAttempts: 1, logStallTimeoutMinutes: 5, totalTimeoutMinutes: 120,
    });
    expect(created.ok, "创建拖拽排序脚本成功").toBeTruthy();
    ids.push((await created.json()).id);
  }
  try {
    await page.goto(baseUrl + "#/scripts", { waitUntil: "domcontentloaded" });
    await page.waitForFunction(() => document.querySelectorAll('[data-testid="script-card"]').length === 3, null, { timeout: 10000 });
    await page.evaluate(() => { document.body.style.paddingBottom = "2000px"; window.scrollTo(0, 40); });
    const scrollBefore = await page.evaluate(() => window.scrollY);
    const dragScript = async (fromIndex, toBox) => {
      const cards = page.locator('[data-testid="script-card"]');
      const handle = cards.nth(fromIndex).locator(".drag-handle");
      await handle.waitFor({ timeout: 10000 });
      let box = await handle.boundingBox();
      if (!box) { await page.waitForTimeout(400); box = await handle.boundingBox(); }
      if (!box) throw new Error("拖拽把手不可见");
      await page.mouse.move(box.x + box.width / 2, box.y + box.height / 2);
      await page.mouse.down();
      await page.mouse.move(toBox.x + toBox.width / 2, toBox.y + 2, { steps: 8 });
      await page.mouse.up();
    };
    // 第三张拖到第一张顶部 → 顺序变为 拖拽脚本2,拖拽脚本0,拖拽脚本1
    const boxes = [];
    for (let i = 0; i < 3; i++) boxes.push(await page.locator('[data-testid="script-card"]').nth(i).boundingBox());
    await dragScript(2, boxes[0]);
    await page.waitForFunction(() => Array.from(document.querySelectorAll('[data-testid="script-card"] .script-name-row strong')).every(el => el.textContent), null, { timeout: 10000 });
    await page.waitForFunction(() => {
      const cards = Array.from(document.querySelectorAll('[data-testid="script-card"]'));
      return cards.length === 3 && cards[0].textContent.includes("拖拽脚本2");
    }, null, { timeout: 10000 });
    expect(true, "拖拽后 拖拽脚本2 成为第一张卡片").toBeTruthy();
    const scrollAfter = await page.evaluate(() => window.scrollY);
    expect(Math.abs(scrollAfter - scrollBefore) <= 2, `拖拽卡片后页面滚动位置保持（${scrollBefore} → ${scrollAfter}）`).toBeTruthy();
    // v0.6.10 修复：dnd onDrop 不等待 PUT 落盘完成，立即 fetch 存在竞态——轮询等待服务端顺序生效
    const orderOk = await waitFor(async () => {
      const l = await (await fetch(baseUrl + "api/scripts")).json();
      return l.map(s => s.name).join() === "拖拽脚本2,拖拽脚本0,拖拽脚本1";
    }, 10000);
    expect(orderOk, "拖拽后顺序已落盘（拖拽脚本2,拖拽脚本0,拖拽脚本1）").toBeTruthy();

    // 名单校验：缺项 / 不存在 / 重复
    expect((await api("PUT", "/api/scripts/order", { ids: ids.slice(0, 2) })).status === 400, "顺序名单缺项被拒（400）").toBeTruthy();
    expect((await api("PUT", "/api/scripts/order", { ids: [...ids, "no-such-id"] })).status === 400, "顺序名单含不存在 id 被拒（400）").toBeTruthy();
    expect((await api("PUT", "/api/scripts/order", { ids: [ids[0], ids[0], ids[1]] })).status === 400, "顺序名单含重复 id 被拒（400）").toBeTruthy();
  } finally {
    for (const id of ids) { try { await api("DELETE", "/api/scripts/" + id); } catch { /* 清理失败不阻塞 */ } }
  }
});

test("脚本卡片拖拽排序：第二页只重排当前页，不移动第一页", async ({ page }) => {
  const stale = await (await fetch(baseUrl + "api/scripts")).json();
  for (const item of stale) {
    try { await api("DELETE", "/api/scripts/" + item.id); } catch { /* 清理失败不阻塞 */ }
  }
  const entries = Array.from({ length: 25 }, (_, index) => ({
    name: `跨页脚本${String(index + 1).padStart(2, "0")}`,
    dir: makeScriptDir(`dnd-page-${index + 1}`),
  }));
  const ids = [];
  for (const entry of entries) {
    const created = await api("POST", "/api/scripts", {
      name: entry.name, rootPath: entry.dir.root, mainExe: entry.dir.main,
      configPath: entry.dir.cfg, logPath: entry.dir.log, gameExe: PING_GAME,
      maxAttempts: 1, logStallTimeoutMinutes: 5, totalTimeoutMinutes: 120,
    });
    expect(created.ok, "创建跨页拖拽排序脚本成功").toBeTruthy();
    ids.push((await created.json()).id);
  }
  try {
    await page.goto(baseUrl + "#/scripts", { waitUntil: "domcontentloaded" });
    await page.waitForFunction(() => document.querySelectorAll('[data-testid="script-card"]').length === 20, null, { timeout: 10000 });
    const before = await (await fetch(baseUrl + "api/scripts")).json();
    const firstPageBefore = before.slice(0, 20).map(script => script.name);
    await page.click('[data-action="pager-page"][data-pager="scripts"][data-page="2"]');
    await page.waitForFunction(() => document.querySelector('[data-testid="pager-scripts"]')?.dataset.pageCurrent === "2", null, { timeout: 5000 });
    await page.waitForFunction(() => document.querySelectorAll('[data-testid="script-card"]').length === 5, null, { timeout: 5000 });

    const cards = page.locator('[data-testid="script-card"]');
    const source = cards.nth(2).locator(".drag-handle");
    const destination = await cards.nth(0).boundingBox();
    const sourceBox = await source.boundingBox();
    if (!destination || !sourceBox) throw new Error("第二页拖拽把手或目标卡片不可见");
    await page.mouse.move(sourceBox.x + sourceBox.width / 2, sourceBox.y + sourceBox.height / 2);
    await page.mouse.down();
    await page.mouse.move(destination.x + destination.width / 2, destination.y + 2, { steps: 8 });
    await page.mouse.up();

    const orderOk = await waitFor(async () => {
      const list = await (await fetch(baseUrl + "api/scripts")).json();
      return list.slice(0, 20).map(script => script.name).join() === firstPageBefore.join()
        && list.slice(20).map(script => script.name).join() === "跨页脚本23,跨页脚本21,跨页脚本22,跨页脚本24,跨页脚本25";
    }, 10000);
    expect(orderOk, "第二页拖拽只改变第二页局部顺序，第一页全局顺序保持不变").toBeTruthy();
  } finally {
    for (const id of ids) { try { await api("DELETE", "/api/scripts/" + id); } catch { /* 清理失败不阻塞 */ } }
  }
});

test("长时脚本：-1 成对校验 / 保存 / 长时徽章", async ({ page }) => {
  const dir = makeScriptDir("longrun");
  try {
    await page.goto(baseUrl + "#/scripts", { waitUntil: "domcontentloaded" });
    await page.waitForSelector("h2");
    await page.click('[data-testid="new-script"]');
    await page.waitForSelector(".new-script-chooser", { timeout: 5000 });
    await page.click('[data-action="open-script-type"][data-plugin=""]');
    await page.waitForSelector(".modal-mask");

    await page.fill("#sm-name", "长时脚本测试");
    await page.fill("#sm-root", dir.root.replace(/\\/g, "\\\\"));
    await page.fill("#sm-exe", dir.main.replace(/\\/g, "\\\\"));
    await page.fill("#sm-config", dir.cfg.replace(/\\/g, "\\\\"));
    await page.fill("#sm-log", dir.log.replace(/\\/g, "\\\\"));
    await page.fill("#sm-game-exe", dir.main.replace(/\\/g, "\\\\"));
    await page.fill("#sm-stall", "-1");
    await page.fill("#sm-total", "120");
    await page.click(".modal button:has-text('保存')");
    await page.waitForTimeout(400);
    expect(await page.$(".modal-mask"), "半 -1（stall=-1 而 total 正常）保存被拒（弹窗保留）").toBeTruthy();
    expect((await page.textContent("body")).includes("长时脚本需将"), "半 -1 提示长时成对语义（两个超时都设为 -1）").toBeTruthy();

    await page.fill("#sm-total", "-1");
    await page.click(".modal button:has-text('保存')");
    await page.waitForSelector(".modal-mask", { state: "detached", timeout: 5000 });
    await page.waitForFunction(() => document.body.textContent.includes("长时脚本测试"), null, { timeout: 5000 });
    expect(await page.$('[data-testid="script-long-badge"]'), "长时脚本卡片显示「长时」徽章").toBeTruthy();
    const list = await (await api("GET", "/api/scripts")).json();
    const saved = list.find(s => s.name === "长时脚本测试");
    expect(!!saved && saved.logStallTimeoutMinutes === -1 && saved.totalTimeoutMinutes === -1, "长时脚本两个超时均已落盘为 -1").toBeTruthy();
  } finally {
    const list = await (await api("GET", "/api/scripts")).json();
    const target = list.find(s => s.name === "长时脚本测试");
    if (target) { try { await api("DELETE", "/api/scripts/" + target.id); } catch { /* 清理失败不阻塞 */ } }
  }
});
