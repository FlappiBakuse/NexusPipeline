import { spawn, spawnSync } from "node:child_process";
import { chromium } from "playwright";
import fs from "node:fs";
import path from "node:path";
import { fileURLToPath } from "node:url";

const __dirname = path.dirname(fileURLToPath(import.meta.url));
const projectRoot = path.resolve(__dirname, "..");
const releaseDir = path.join(projectRoot, "release");
const runtimeDir = path.join(__dirname, "runtime");
const runtimeExe = path.join(runtimeDir, "nexus-pipeline.exe");
const baseUrl = "http://127.0.0.1:58731/";
const JSON_HDR = { "Content-Type": "application/json" };

const CI = process.argv.includes("--ci");
const CI_SKIP = new Set([
  "testResponsiveShell",
]);
const EXPECTED = 354;
const CI_EXPECTED = 331;

let passed = 0;
let failed = 0;
let child = null;

function localDate() {
  const d = new Date();
  const y = d.getFullYear();
  const m = String(d.getMonth() + 1).padStart(2, "0");
  const day = String(d.getDate()).padStart(2, "0");
  return `${y}-${m}-${day}`;
}

function assert(cond, msg) {
  if (cond) {
    passed++;
    console.log("  [PASS] " + msg);
  } else {
    failed++;
    console.log("  [FAIL] " + msg);
  }
}

function isElevated() {
  try {
    const r = spawnSync("net", ["session"], { stdio: "ignore", windowsHide: true });
    return r.status === 0;
  } catch {
    return false;
  }
}

const sleep = ms => new Promise(r => setTimeout(r, ms));

async function waitFor(predicate, timeoutMs = 5000, intervalMs = 200) {
  const deadline = Date.now() + timeoutMs;
  while (Date.now() < deadline) {
    if (await predicate()) return true;
    await new Promise(r => setTimeout(r, intervalMs));
  }
  return !!(await predicate());
}

async function waitForService(timeoutMs = 30000) {
  const deadline = Date.now() + timeoutMs;
  while (Date.now() < deadline) {
    try {
      const res = await fetch(baseUrl + "api/status");
      if (res.ok) return;
    } catch { /* retry */ }
    await sleep(500);
  }
  throw new Error("服务未在 " + timeoutMs + "ms 内启动");
}

async function api(method, pathName, body) {
  const options = { method };
  if (body !== undefined) {
    options.headers = JSON_HDR;
    options.body = JSON.stringify(body);
  }
  return fetch(baseUrl + pathName.replace(/^\/+/, ""), options);
}

async function createScript(body) {
  const res = await api("POST", "/api/scripts", { maxAttempts: 1, logStallTimeoutMinutes: 5, totalTimeoutMinutes: 120, ...body });
  return { ok: res.ok, id: (await res.json()).id };
}

/** 创建真实存在的脚本目录（根目录/占位脚本/配置目录/日志目录），路径校验用例使用。
 *  占位脚本用唯一命名（nexustest-*），避免 IsExeRunning 按进程名误报（如 run.bat → 进程名 run）。 */
function makeScriptDir(label) {
  const dir = path.join(runtimeDir, "test-" + label);
  fs.rmSync(dir, { recursive: true, force: true });
  fs.mkdirSync(path.join(dir, "cfg"), { recursive: true });
  fs.mkdirSync(path.join(dir, "logs"), { recursive: true });
  fs.writeFileSync(path.join(dir, `nexustest-${label}.bat`), "@echo off\r\nexit /b 0\r\n", "ascii");
  return { root: dir, main: path.join(dir, `nexustest-${label}.bat`), cfg: path.join(dir, "cfg"), log: path.join(dir, "logs") };
}

async function runningCount() {
  const status = await (await fetch(baseUrl + "api/status")).json();
  return (status.running || []).length;
}

async function waitNoRunning(timeoutMs = 60000, intervalMs = 300) {
  return waitFor(async () => (await runningCount()) === 0, timeoutMs, intervalMs);
}

async function waitAbsent(page, text, timeoutMs = 5000) {
  return page.waitForFunction(t => !document.body.textContent.includes(t), text, { timeout: timeoutMs });
}

function latestHistoryDay() {
  const historyRoot = path.join(runtimeDir, "history");
  const dirs = fs.existsSync(historyRoot) ? fs.readdirSync(historyRoot).filter(d => /^\d{4}-\d{2}-\d{2}$/.test(d)).sort() : [];
  return dirs.length ? dirs[dirs.length - 1] : localDate();
}

function setupRuntime() {
  fs.rmSync(runtimeDir, { recursive: true, force: true });
  fs.mkdirSync(runtimeDir, { recursive: true });
  const sourceExe = path.join(releaseDir, "nexus-pipeline.exe");
  if (!fs.existsSync(sourceExe)) {
    throw new Error("release/nexus-pipeline.exe 不存在，请先运行 build.cmd");
  }
  fs.copyFileSync(sourceExe, runtimeExe);
  fs.cpSync(path.join(releaseDir, "wwwroot"), path.join(runtimeDir, "wwwroot"), { recursive: true });
  if (fs.existsSync(path.join(releaseDir, "plugins"))) {
    fs.cpSync(path.join(releaseDir, "plugins"), path.join(runtimeDir, "plugins"), { recursive: true });
  } else {
    fs.mkdirSync(path.join(runtimeDir, "plugins"), { recursive: true });
  }
  if (!fs.existsSync(runtimeExe)) {
    throw new Error("runtime exe 拷贝失败，拒绝运行（避免测试数据写入项目根）");
  }
}

function startService() {
  child = spawn(runtimeExe, ["web"], { cwd: runtimeDir, stdio: "ignore" });
}

async function stopService() {
  if (child) {
    child.kill();
    await sleep(500);
    child = null;
  }
}

async function restartService() {
  await stopService();
  await sleep(400);
  startService();
  await waitForService();
  await sleep(500);
}

/* ---------------- 用例 ---------------- */

async function testDashboard(page) {
  console.log("[用例] 仪表盘：统计卡片 + 版本 + 插件配置信息");
  await page.goto(baseUrl, { waitUntil: "domcontentloaded" });
  await page.waitForSelector(".stat-grid", { timeout: 15000 });
  const body = await page.textContent("body");
  assert(body.includes("通知推送"), "插件「通知推送」在页面可见");
  assert(body.includes("脚本实例") && body.includes("调度队列"), "首行含脚本实例与调度队列统计卡片");
  assert(body.includes("当前版本"), "首行含当前版本卡片");
  assert(body.includes("0.3.3"), "版本显示 0.3.3（x.x.x 不带 v）");
  assert(body.includes("下一调度队列"), "首行含下一调度队列卡片");
  const nums = await page.$$eval(".stat .num", els => els.map(e => e.textContent.trim()));
  assert(nums.includes("无"), "无定时队列时下一调度显示「无」");
  const pcards = await page.$$eval(".plugin-card", els => els.map(e => e.textContent.trim()));
  assert(pcards.some(t => t.includes("通知推送")), "插件小卡片含「通知推送」");
  assert(body.includes("已启用通知"), "仪表盘插件卡片显示通知配置信息");
}

async function testResponsiveShell(page) {
  console.log("[用例] 响应式外壳：手机 / 平板 / 电脑 + 主题 + 粒子效果");
  const sizes = [
    { width: 360, height: 800, name: "手机" },
    { width: 768, height: 900, name: "平板" },
    { width: 1280, height: 900, name: "电脑" },
  ];
  for (const size of sizes) {
    await page.setViewportSize({ width: size.width, height: size.height });
    await page.goto(baseUrl + "#/dashboard", { waitUntil: "domcontentloaded" });
    await page.waitForSelector(".stat-grid", { timeout: 10000 });
    const metrics = await page.evaluate(() => ({
      noOverflow: document.documentElement.scrollWidth <= window.innerWidth + 1,
      canvas: document.querySelector("#ambient-particles")?.getAttribute("aria-hidden") === "true"
        && getComputedStyle(document.querySelector("#ambient-particles")).pointerEvents === "none",
      topbar: getComputedStyle(document.querySelector(".topbar")).display !== "none",
    }));
    assert(metrics.noOverflow, `${size.name}视口没有横向溢出（${size.width}px）`);
    assert(metrics.canvas, `${size.name}视口粒子层不拦截交互（${size.width}px）`);
    assert(metrics.topbar === (size.width <= 820), `${size.name}视口导航形态正确（${size.width}px）`);
  }

  await page.evaluate(() => localStorage.removeItem("nexus-theme"));
  await page.reload({ waitUntil: "domcontentloaded" });
  await page.waitForSelector(".stat-grid");
  await page.locator('[data-action="toggle-theme"]:visible').click();
  const lightTheme = await page.evaluate(() => document.body.dataset.theme);
  assert(lightTheme === "light", "主题切换可进入浅色模式");
  await page.locator('[data-action="toggle-theme"]:visible').click();
  const darkTheme = await page.evaluate(() => document.body.dataset.theme);
  assert(darkTheme === "dark", "主题切换可进入深色模式");
  await page.locator('[data-action="toggle-theme"]:visible').click();

  await page.emulateMedia({ reducedMotion: "reduce" });
  await page.reload({ waitUntil: "domcontentloaded" });
  await page.waitForSelector(".stat-grid");
  assert(await page.evaluate(() => getComputedStyle(document.querySelector("#ambient-particles")).display !== "none" && document.querySelector("#ambient-particles").dataset.ready === "true"), "减少动画模式保留静态粒子");
  await page.emulateMedia({ reducedMotion: "no-preference" });

  await page.setViewportSize({ width: 360, height: 800 });
  await page.click('[data-action="open-nav"]');
  assert(await page.evaluate(() => document.body.classList.contains("nav-open")), "手机端可以打开导航抽屉");
  await page.click('[data-action="close-nav"]');
  assert(!(await page.evaluate(() => document.body.classList.contains("nav-open"))), "手机端可以关闭导航抽屉");

  await page.goto(baseUrl + "#/scripts", { waitUntil: "domcontentloaded" });
  await page.waitForSelector("h2");
  await page.click('[data-testid="new-script"]');
  await page.waitForSelector(".new-script-chooser", { timeout: 5000 });
  const chooserStack = await page.evaluate(() => {
    const cards = Array.from(document.querySelectorAll(".chooser-card"));
    if (cards.length !== 4) return false;
    const a = cards[0].getBoundingClientRect();
    const b = cards[1].getBoundingClientRect();
    const c = cards[2].getBoundingClientRect();
    const d = cards[3].getBoundingClientRect();
    return b.top > a.bottom && c.top > b.bottom && d.top > c.bottom && Math.abs(a.left - b.left) <= 1 && Math.abs(b.left - c.left) <= 1 && Math.abs(c.left - d.left) <= 1;
  });
  assert(chooserStack, "手机端新建选择卡片堆叠");
  await page.click('[data-action="open-script-type"][data-plugin=""]');
  await page.waitForSelector(".modal");
  await page.waitForFunction(() => document.activeElement?.id === "sm-name", null, { timeout: 2000 });
  const modalMetrics = await page.evaluate(() => {
    const modal = document.querySelector(".modal");
    const exe = document.querySelector("#sm-exe");
    const args = document.querySelector("#sm-args");
    return { fits: modal.getBoundingClientRect().width <= window.innerWidth, stacked: args.getBoundingClientRect().top > exe.getBoundingClientRect().top, dialog: modal.getAttribute("role") === "dialog" && modal.getAttribute("aria-modal") === "true", focus: document.activeElement?.id === "sm-name" };
  });
  assert(modalMetrics.fits, "手机端弹窗不超出视口");
  assert(modalMetrics.stacked, "手机端脚本表单自动堆叠");
  assert(modalMetrics.dialog, "弹窗包含可访问语义");
  assert(modalMetrics.focus, "弹窗打开后焦点进入第一个字段");
  await page.click('[data-action="close-modal"]');

  await page.goto(baseUrl + "#/queues", { waitUntil: "domcontentloaded" });
  await page.waitForSelector("h2");
  await page.click("button:has-text('新建调度队列')");
  await page.waitForSelector("#qm-name");
  await page.click("text=+ 添加任务");
  await page.waitForSelector(".task-row");
  const taskRowInline = await page.evaluate(() => {
    const row = document.querySelector(".task-row");
    if (!row) return false;
    const parts = Array.from(row.querySelectorAll("select, button"));
    if (parts.length < 4) return false;
    const tops = parts.map(x => x.getBoundingClientRect().top);
    return Math.max(...tops) - Math.min(...tops) <= 4;
  });
  assert(taskRowInline, "手机端任务列表选择器与上移/下移/删除按钮同一行");
  await page.click(".modal button:has-text('取消')");

  await page.setViewportSize({ width: 1280, height: 900 });
  await page.goto(baseUrl + "#/dispatch", { waitUntil: "domcontentloaded" });
  await page.waitForSelector("#dc-script");
  const dispatchButtons = await page.evaluate(() => Array.from(document.querySelectorAll(".control-action button")).map(button => ({ width: button.getBoundingClientRect().width, card: button.closest(".card").getBoundingClientRect().width })));
  assert(dispatchButtons.length === 2 && dispatchButtons.every(item => item.width / item.card <= 0.25), "调度中心执行按钮保持紧凑宽度");
  const cardRow = await page.evaluate(() => {
    const cards = Array.from(document.querySelectorAll(".dispatch-cards > .card"));
    if (cards.length !== 2) return false;
    const a = cards[0].getBoundingClientRect();
    const b = cards[1].getBoundingClientRect();
    return a.right <= b.left + 1 && Math.abs(a.top - b.top) <= 1;
  });
  assert(cardRow, "桌面端脚本/队列执行卡片同排");
  await page.setViewportSize({ width: 360, height: 800 });
  await page.goto(baseUrl + "#/dispatch", { waitUntil: "domcontentloaded" });
  await page.waitForSelector("#dc-script");
  const cardStack = await page.evaluate(() => {
    const cards = Array.from(document.querySelectorAll(".dispatch-cards > .card"));
    if (cards.length !== 2) return false;
    const a = cards[0].getBoundingClientRect();
    const b = cards[1].getBoundingClientRect();
    return a.bottom <= b.top + 1 && Math.abs(a.left - b.left) <= 1;
  });
  assert(cardStack, "手机竖屏脚本/队列执行卡片保持堆叠");
  await page.setViewportSize({ width: 1280, height: 900 });
}

async function testResponsiveSmoke(page) {
  console.log("[用例] 响应式冒烟：手机 / 平板 / 电脑视口无横向溢出（粗检）");
  const sizes = [
    { width: 360, height: 800, name: "手机" },
    { width: 768, height: 900, name: "平板" },
    { width: 1280, height: 900, name: "电脑" },
  ];
  for (const size of sizes) {
    await page.setViewportSize({ width: size.width, height: size.height });
    await page.goto(baseUrl + "#/dashboard", { waitUntil: "domcontentloaded" });
    await page.waitForSelector(".stat-grid", { timeout: 10000 });
    const noOverflow = await page.evaluate(() => document.documentElement.scrollWidth <= window.innerWidth + 1);
    assert(noOverflow, `${size.name}视口没有横向溢出（${size.width}px）`);
  }
  await page.setViewportSize({ width: 1280, height: 900 });
}

async function testNavigation(page) {
  console.log("[用例] 菜单切换：无回弹");
  const pages = {
    scripts: "脚本实例", queues: "调度队列", dispatch: "调度中心",
    history: "历史记录", plugins: "插件", settings: "设置", dashboard: "仪表盘",
  };
  for (const [hash, title] of Object.entries(pages)) {
    await page.click('nav a[href="#/' + hash + '"]');
    await page.waitForFunction(t => {
      const h2 = document.querySelector("h2");
      return h2 && h2.textContent.includes(t);
    }, title, { timeout: 5000 });
    const h2 = await page.textContent("h2");
    assert(h2.includes(title), "页面「" + title + "」正常打开（h2=" + h2.trim() + "）");
  }
  await page.click('nav a[href="#/scripts"]');
  await page.waitForTimeout(3600);
  const h2 = await page.textContent("h2");
  assert(h2.includes("脚本实例"), "停留在脚本实例页 3.5 秒后未被仪表盘轮询覆盖（回弹已修复）");
}

async function testScriptCrud(page) {
  console.log("[用例] 脚本实例：空状态 / 新建卡片组 / 必填校验 / 新建 / 编辑 / 删除");
  await page.click('nav a[href="#/scripts"]');
  await page.waitForSelector("h2");
  assert((await page.textContent("body")).includes("暂无脚本实例"), "无脚本时显示空状态提示而非空卡片");
  const newBtn = await page.$('[data-testid="new-script"]');
  assert(!!newBtn, "新建通用脚本实例按钮位于右上角（page-head 内）");
  await page.click('[data-testid="new-script"]');
  await page.waitForSelector(".new-script-chooser", { timeout: 5000 });
  assert((await page.$$(".chooser-card")).length === 4, "选择卡片层含通用与专项四张卡片");
  await page.click('[data-action="open-script-type"][data-plugin=""]');
  await page.waitForSelector(".modal-mask");

  await page.click(".modal button:has-text('保存')");
  await page.waitForTimeout(400);
  assert(await page.$(".modal-mask"), "必填未填时无法保存（弹窗保留）");
  await page.click(".modal button:has-text('保存')");
  await page.waitForTimeout(200);

  await page.fill("#sm-name", "测试脚本A");
  const crudDir = makeScriptDir("crud");
  await page.fill("#sm-root", crudDir.root.replace(/\\/g, "\\\\"));
  await page.fill("#sm-exe", crudDir.main.replace(/\\/g, "\\\\"));
  await page.fill("#sm-config", crudDir.cfg.replace(/\\/g, "\\\\"));
  await page.fill("#sm-log", crudDir.log.replace(/\\/g, "\\\\"));
  await page.click(".modal button:has-text('保存')");
  await page.waitForSelector(".modal-mask", { state: "detached", timeout: 5000 });
  await page.waitForFunction(() => document.body.textContent.includes("测试脚本A"), null, { timeout: 5000 });
  assert((await page.textContent("body")).includes("测试脚本A"), "新建后列表中显示脚本名称");
  assert(fs.existsSync(path.join(runtimeDir, "config", "scripts.json")), "配置文件写入 config 目录");

  await page.click('[data-action="edit-script"]');
  await page.waitForSelector("#sm-name");
  await page.waitForFunction(() => document.querySelector("#sm-root") && document.querySelector("#sm-root").value.length > 0, null, { timeout: 5000 });
  await page.fill("#sm-name", "测试脚本A-改");
  await page.click(".modal button:has-text('保存')");
  await page.waitForSelector(".modal-mask", { state: "detached", timeout: 5000 });
  await page.waitForFunction(() => document.body.textContent.includes("测试脚本A-改"), null, { timeout: 5000 });
  assert((await page.textContent("body")).includes("测试脚本A-改"), "编辑后名称已更新");

  page.once("dialog", d => d.accept());
  await page.click('[data-action="delete-script"]');
  await waitAbsent(page, "测试脚本A-改");
  assert(!(await page.textContent("body")).includes("测试脚本A-改"), "删除后列表不再显示该脚本");
}

async function testQueueCrud(page) {
  console.log("[用例] 调度队列：新建（定时+任务）/ 编辑 / 删除");
  const qDir = makeScriptDir("queue");
  const created = await createScript({ name: "队列用脚本", rootPath: qDir.root, mainExe: qDir.main, configPath: qDir.cfg, logPath: qDir.log, maxAttempts: 3, logStallTimeoutMinutes: 5, totalTimeoutMinutes: 120 });
  assert(created.ok, "通过 API 预创建队列用脚本");
  await page.click('nav a[href="#/queues"]');
  await page.waitForSelector("h2");
  await page.click("text=新建调度队列");
  await page.waitForSelector(".modal-mask");
  await page.fill("#qm-name", "测试队列A");
  const modeVal = await page.$eval("#qm-mode", el => el.value);
  assert(modeVal === "none", "新建队列默认自动运行方式为「不运行」");
  const modeOpts = await page.$$eval("#qm-mode option", els => els.map(e => e.textContent));
  assert(modeOpts.length === 3 && modeOpts[0] === "不运行", "自动运行方式含「不运行」选项且置顶");

  const dayState = await page.$eval(".timeset-days", el => {
    const inputs = Array.from(el.querySelectorAll("[data-ts-days]"));
    return { count: inputs.length, checked: inputs.filter(input => input.checked).length };
  });
  assert(dayState.count === 7, "执行周期为整体复选框组（7 项）");
  assert(dayState.checked === 5, "执行周期默认选中工作日");

  await page.click("text=+ 添加任务");
  await page.selectOption('[data-task-idx="0"]', { label: "队列用脚本" });
  await page.click(".modal button:has-text('保存')");
  await page.waitForSelector(".modal-mask", { state: "detached", timeout: 5000 });
  await page.waitForFunction(() => document.body.textContent.includes("测试队列A"), null, { timeout: 5000 });
  assert(true, "新建后卡片显示队列名称");
  assert(await page.$(".card .script-grid .queue-card"), "队列卡片位于外层大卡片网格内（与脚本卡片同构）");

  const qm = await api("POST", "/api/queues", { name: "缺省模式队列", autoRunMode: "invalid", completionAction: "none", timeSets: [], tasks: [{ id: "", index: 0, scriptInstanceId: created.id }] });
  assert(qm.ok, "POST 非法 autoRunMode 成功（归一化）");
  const qmId = (await qm.json()).id;
  const qmList = await (await fetch(baseUrl + "api/queues")).json();
  const qmGot = qmList.find(q => q.id === qmId);
  assert(qmGot && qmGot.autoRunMode === "none", "非法 autoRunMode 归一为 none");
  await api("DELETE", "/api/queues/" + qmId);

  await page.click('[data-action="edit-queue"]');
  await page.waitForSelector("#qm-name");
  await page.fill("#qm-name", "测试队列A-改");
  await page.click(".modal button:has-text('保存')");
  await page.waitForSelector(".modal-mask", { state: "detached", timeout: 5000 });
  await page.waitForFunction(() => document.body.textContent.includes("测试队列A-改"), null, { timeout: 5000 });
  assert(true, "编辑后队列名称已更新");

  const qCard = await page.$('[data-testid="queue-card"]');
  assert(!!qCard && !!await qCard.$("img.script-ico"), "队列卡片左侧显示首个脚本实例图标");
  const qBody = await page.textContent('[data-testid="queue-card"]');
  assert(!qBody.includes("定时：") && !qBody.includes("任务："), "队列卡片移除定时与任务信息行");
  assert(!qBody.includes("开始运行"), "不运行队列无运行时间提示");

  const sq = await api("POST", "/api/queues", { name: "启动队列", autoRunMode: "startup", completionAction: "none", timeSets: [], tasks: [{ id: "", index: 0, scriptInstanceId: created.id }] });
  assert(sq.ok, "API 创建启动时运行队列");
  await page.goto(baseUrl + "#/dashboard", { waitUntil: "domcontentloaded" });
  await page.goto(baseUrl + "#/queues", { waitUntil: "domcontentloaded" });
  await page.waitForFunction(() => document.body.textContent.includes("启动队列"), null, { timeout: 5000 });
  assert((await page.textContent("body")).includes("将在下次启动开始运行"), "启动时运行队列显示「将在下次启动开始运行」");
  const sqList = await (await fetch(baseUrl + "api/queues")).json();
  const sqGot = sqList.find(q => q.name === "启动队列");
  assert(sqGot && sqGot.nextTrigger === null, "启动时运行队列 nextTrigger 为 null");
  await api("DELETE", "/api/queues/" + sqGot.id);

  const tq = await api("POST", "/api/queues", { name: "定时倒计时队列", autoRunMode: "scheduled", completionAction: "none", timeSets: [{ id: "", enabled: true, days: [0, 1, 2, 3, 4, 5, 6], time: "23:59" }], tasks: [{ id: "", index: 0, scriptInstanceId: created.id }] });
  assert(tq.ok, "API 创建定时运行队列");
  await page.goto(baseUrl + "#/dashboard", { waitUntil: "domcontentloaded" });
  await page.goto(baseUrl + "#/queues", { waitUntil: "domcontentloaded" });
  await page.waitForFunction(() => document.body.textContent.includes("定时倒计时队列"), null, { timeout: 5000 });
  await page.waitForFunction(() => { const el = document.querySelector('[data-testid="queue-card"] .queue-next'); return el && /\d{2}:\d{2}:\d{2}后开始/.test(el.textContent); }, null, { timeout: 5000 });
  assert(true, "定时运行队列显示倒计时（xx:xx:xx后开始）");
  assert((await page.textContent('[data-testid="queue-card"] [data-testid="queue-notify"]')).includes("队列级通知：关"), "定时队列卡片显示「队列级通知：关」");
  const tqList = await (await fetch(baseUrl + "api/queues")).json();
  const tqGot = tqList.find(q => q.name === "定时倒计时队列");
  assert(tqGot && tqGot.nextTrigger, "定时运行队列 API 返回 nextTrigger");
  await api("DELETE", "/api/queues/" + tqGot.id);

  page.once("dialog", d => d.accept());
  await page.click('[data-action="delete-queue"]');
  await waitAbsent(page, "测试队列A-改");
  assert(!(await page.textContent("body")).includes("测试队列A-改"), "删除后卡片消失");
}

async function testDispatchAndHistory(page) {
  console.log("[用例] 调度中心执行 + 历史记录详情");
  const dDir = makeScriptDir("dispatch");
  const created = await createScript({ name: "跑批脚本", rootPath: dDir.root, mainExe: dDir.main, configPath: dDir.cfg, logPath: dDir.log, maxAttempts: 3, logStallTimeoutMinutes: 5, totalTimeoutMinutes: 120 });
  assert(created.ok, "通过 API 预创建调度中心用脚本");

  await page.click('nav a[href="#/dispatch"]');
  await page.waitForSelector("#dc-script");
  await page.selectOption("#dc-script", { label: "跑批脚本" });
  await page.click("button:has-text('执行')");
  await page.waitForFunction(() => !!document.querySelector("#dispatch-running .list-item"), null, { timeout: 8000 });
  assert(true, "调度中心出现正在运行的任务卡片（执行已受理）");
  await page.waitForTimeout(1500);

  await page.click('nav a[href="#/history"]');
  await page.waitForSelector("h2");
  await page.waitForFunction(() => document.body.textContent.includes("跑批脚本"), null, { timeout: 10000 });
  const body = await page.textContent("body");
  assert(body.includes("跑批脚本"), "历史记录出现新运行记录");
  await page.click('[data-action="history-detail"]');
  await page.waitForSelector(".modal-mask", { timeout: 5000 });
  const modalText = await page.textContent(".modal");
  assert(modalText.includes("运行详情") && modalText.includes("失败"), "历史详情弹窗显示运行失败详情");
  assert(modalText.includes("脚本日志"), "历史详情弹窗含脚本日志区块");
  await page.click(".modal button:has-text('关闭')");
}

async function testLogScroll(page) {
  console.log("[用例] 调度中心：运行中任务实时日志滚动（重试后成功 → 部分失败）");
  const batPath = path.join(runtimeDir, "live.bat");
  const logPath = path.join(runtimeDir, "logs", "live.log");
  const flagPath = path.join(runtimeDir, "logs", "first.done");
  fs.mkdirSync(path.dirname(logPath), { recursive: true });
  fs.rmSync(logPath, { force: true });
  fs.rmSync(flagPath, { force: true });
  fs.writeFileSync(batPath, [
    "@echo off",
    "setlocal EnableDelayedExpansion",
    "set LOG=" + logPath,
    "set FLAG=" + flagPath,
    "echo [SCRIPT] attempt started >> %LOG%",
    "if exist %FLAG% goto success",
    "echo x > %FLAG%",
    "echo [SCRIPT] ERROR something went wrong >> %LOG%",
    "echo [CONSOLE] console: attempt failed",
    "exit /b 1",
    ":success",
    "ping 127.0.0.1 -n 4 >nul",
    "echo [SCRIPT] ALL-DONE-MARKER >> %LOG%",
    "echo [CONSOLE] console: all done",
    "exit /b 0",
  ].join("\r\n"), "ascii");
  const created = await createScript({ name: "日志脚本", rootPath: runtimeDir, mainExe: batPath, configPath: runtimeDir, logPath, maxAttempts: 2, logStallTimeoutMinutes: 5, totalTimeoutMinutes: 10, successMarkers: "ALL-DONE-MARKER" });
  assert(created.ok, "创建日志测试脚本");

  await page.click('nav a[href="#/dispatch"]');
  await page.waitForSelector("#dc-script");
  await page.selectOption("#dc-script", { label: "日志脚本" });
  await page.click("button:has-text('执行')");
  await page.waitForSelector(".run-log", { timeout: 10000 });
  await page.waitForFunction(() => { const el = document.querySelector(".run-log"); return el && el.textContent.includes("SCRIPT"); }, null, { timeout: 10000 });
  const logText = await page.textContent(".run-log");
  assert(logText.includes("SCRIPT"), "日志框实时显示脚本输出（首行：" + (logText.split("\n")[0] || "") + "）");
  const scrolled = await page.evaluate(() => {
    const el = document.querySelector(".run-log");
    return el.scrollHeight - el.scrollTop - el.clientHeight < 10;
  });
  assert(scrolled, "日志框自动滚动到底部");
  await page.waitForFunction(() => !document.querySelector(".run-log"), null, { timeout: 25000 });
  assert(true, "运行结束后日志框随任务消失");
}

async function testHistoryFiles() {
  console.log("[用例] 历史文件夹：.log/.json 配对 + 脚本日志与控制台输出分离 + partial 判定");
  const historyRoot = path.join(runtimeDir, "history");
  const dayDir = path.join(historyRoot, latestHistoryDay());
  await waitFor(() => fs.existsSync(dayDir), 8000);
  assert(fs.existsSync(dayDir), "history/YYYY-MM-DD 目录存在");
  const jsons = fs.existsSync(dayDir) ? fs.readdirSync(dayDir).filter(f => f.endsWith(".json")) : [];
  const logs = fs.existsSync(dayDir) ? fs.readdirSync(dayDir).filter(f => f.endsWith(".log")) : [];
  assert(jsons.length >= 1, "存在 .json 状态文件（" + jsons.length + " 个）");
  assert(logs.length >= 1, "存在 .log 日志文件（" + logs.length + " 个）");
  assert(jsons.some(f => logs.includes(f.replace(".json", ".log"))), ".log 与 .json 同名配对");

  const newestJson = jsons[jsons.length - 1];
  const readText = p => fs.readFileSync(p, "utf8").replace(/^\uFEFF/, "");
  const record = JSON.parse(readText(path.join(dayDir, newestJson)));
  assert(record.FinalStatus === "partial", "重试后成功判定为部分失败（FinalStatus=" + record.FinalStatus + "）");
  assert(record.Attempts === 2, "重试次数记录为 2（Attempts=" + record.Attempts + "）");
  assert(record.LogFile === newestJson, "json 记录 LogFile 引用");

  const scriptLog = readText(path.join(dayDir, newestJson.replace(".json", ".log")));
  assert(scriptLog.includes("[SCRIPT]"), "历史 .log 含脚本日志内容");
  assert(!scriptLog.includes("[CONSOLE]"), "历史 .log 不含控制台输出（分离）");

  const logRoot = path.join(runtimeDir, "logs");
  const latestConsole = () => {
    const files = fs.existsSync(logRoot) ? fs.readdirSync(logRoot).filter(f => /^\d{4}-\d{2}-\d{2}\.log$/.test(f)).sort() : [];
    return files.length ? files[files.length - 1] : localDate() + ".log";
  };
  const consoleFile = path.join(logRoot, latestConsole());
  assert(fs.existsSync(consoleFile), "logs/YYYY-MM-DD.log 存在");
  if (fs.existsSync(consoleFile)) {
    const consoleText = readText(consoleFile);
    assert(consoleText.includes("[CONSOLE]"), "控制台日志含控制台输出");
    assert(!consoleText.includes("[SCRIPT]"), "控制台日志不含脚本日志内容（分离）");
  }
}

async function testAudit(page) {
  console.log("[用例] 审计日志：增删改/查询记录 + 轮询豁免");
  const logFile = path.join(runtimeDir, "logs", "nexus-pipeline-" + localDate().replace(/-/g, "") + ".log");
  const readLog = () => fs.readFileSync(logFile, "utf8").replace(/^\uFEFF/, "");

  const aDir = makeScriptDir("audit");
  const created = await api("POST", "/api/scripts", {
    name: "审计脚本", rootPath: aDir.root, mainExe: aDir.main,
    configPath: aDir.cfg, logPath: aDir.log,
    maxAttempts: 3, logStallTimeoutMinutes: 5, totalTimeoutMinutes: 120,
  });
  assert(created.ok, "API 创建脚本");
  await sleep(400);
  assert(readLog().includes("[审计] web | 添加脚本实例（审计脚本"), "创建脚本产生审计行");

  const list = await (await fetch(baseUrl + "api/scripts")).json();
  const target = list.find(x => x.name === "审计脚本");
  assert(!!target, "列表可查询到审计脚本");
  const updated = await api("PUT", "/api/scripts/" + target.id, {
    id: target.id, name: "审计脚本改", rootPath: aDir.root, mainExe: aDir.main,
    configPath: aDir.cfg, logPath: aDir.log,
    maxAttempts: 3, logStallTimeoutMinutes: 5, totalTimeoutMinutes: 120,
  });
  assert(updated.ok, "API 修改脚本");
  await sleep(400);
  assert(readLog().includes("[审计] web | 修改脚本实例（审计脚本改"), "修改脚本产生审计行");

  await page.click('nav a[href="#/history"]');
  await page.waitForFunction(() => document.querySelector("h2") && document.querySelector("h2").textContent.includes("历史记录"));
  await sleep(600);
  assert(readLog().includes("[审计] web | 查询历史记录"), "打开历史页产生查询审计行");

  const count1 = (readLog().match(/\[审计\]/g) || []).length;
  await page.waitForTimeout(2600);
  const count2 = (readLog().match(/\[审计\]/g) || []).length;
  assert(count1 === count2, "历史页停留无新增审计行（status 轮询已豁免）");

  const del = await api("DELETE", "/api/scripts/" + target.id);
  assert(del.ok, "API 删除脚本");
  await sleep(400);
  assert(readLog().includes("[审计] web | 删除脚本实例（审计脚本改"), "删除脚本产生审计行");
}

async function testLogLevel(page) {
  console.log("[用例] 日志级别：设置 UI / 落盘 / 阈值过滤 / DEBUG 请求记录");
  const logFile = path.join(runtimeDir, "logs", "nexus-pipeline-" + localDate().replace(/-/g, "") + ".log");
  const readLog = () => fs.existsSync(logFile) ? fs.readFileSync(logFile, "utf8").replace(/^\uFEFF/, "") : "";

  await page.click('nav a[href="#/settings"]');
  await page.waitForSelector("#st-loglevel");
  const defaultLevel = await page.$eval("#st-loglevel", el => el.value);
  assert(defaultLevel === "info", "设置页含「日志级别」下拉且默认 info");
  const levelOptions = await page.$$eval("#st-loglevel option", els => els.map(e => e.textContent));
  assert(levelOptions.length === 5 && levelOptions[0] === "Debug" && levelOptions[4] === "Fatal", "日志级别选项首字母大写（Debug…Fatal）");

  let put = await api("PUT", "/api/settings", { logLevel: "warn" });
  assert(put.ok, "PUT logLevel=warn 成功");
  const got = await (await fetch(baseUrl + "api/settings")).json();
  assert(got.settings.logLevel === "warn", "GET 返回 logLevel=warn");
  const cfg = JSON.parse(fs.readFileSync(path.join(runtimeDir, "config", "settings.json"), "utf8").replace(/^\uFEFF/, ""));
  assert(cfg.LogLevel === "warn", "settings.json 已落盘 LogLevel=warn");

  const lgDir = makeScriptDir("loglevel");
  const created = await api("POST", "/api/scripts", {
    name: "日志级别脚本", rootPath: lgDir.root, mainExe: lgDir.main,
    configPath: lgDir.cfg, logPath: lgDir.log,
    maxAttempts: 1, logStallTimeoutMinutes: 5, totalTimeoutMinutes: 120,
  });
  assert(created.ok, "创建日志级别测试脚本（触发 INFO 审计）");
  const sid = (await created.json()).id;
  await sleep(400);
  assert(!readLog().includes("[审计] web | 添加脚本实例（日志级别脚本"), "warn 阈值下 INFO 审计行被过滤");

  put = await api("PUT", "/api/settings", { logLevel: "debug" });
  assert(put.ok, "PUT logLevel=debug 成功");
  await fetch(baseUrl + "api/scripts");
  await sleep(400);
  assert(readLog().includes("[DEBUG] [Web] GET /api/scripts"), "debug 级别记录 Web API 请求");
  await fetch(baseUrl + "api/status");
  await sleep(400);
  assert(!readLog().includes("[Web] GET /api/status"), "GET /api/status 轮询豁免（不记录）");

  put = await api("PUT", "/api/settings", { logLevel: "info" });
  assert(put.ok, "恢复 logLevel=info 成功");
  const del = await api("DELETE", "/api/scripts/" + sid);
  assert(del.ok, "清理日志级别测试脚本");
}

async function testUserManagement(page) {
  console.log("[用例] 用户管理：按钮改名 / 二级页 / 用户 CRUD / 配置快照与交换 / 运行选用户 / 队列用户下拉");
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
    configPath: cfgDir.replace(/\\/g, "\\\\"), logPath: logDir.replace(/\\/g, "\\\\"),
    maxAttempts: 1, logStallTimeoutMinutes: 5, totalTimeoutMinutes: 120,
  });
  const script = await create.json();
  const sid = script.id;
  const dataDir = path.join(runtimeDir, "data", sid);

  await page.reload({ waitUntil: "domcontentloaded" });
  await page.waitForFunction(() => document.body.textContent.includes("用户测试脚本"), null, { timeout: 5000 });
  let body = await page.textContent("body");
  assert(body.includes("编辑脚本"), "按钮改为「编辑脚本」");
  assert(body.includes("删除脚本"), "按钮改为「删除脚本」");
  assert(body.includes("用户管理"), "新增「用户管理」按钮");

  await page.click('[data-action="manage-users"]');
  await page.waitForFunction(() => document.body.textContent.includes("添加用户"), null, { timeout: 5000 });
  body = await page.textContent("body");
  assert(body.includes("返回脚本实例"), "用户管理页左上角有返回箭头");
  assert(body.includes("暂无用户"), "无用户时显示空状态");

  await page.click("button:has-text('添加用户')");
  await page.waitForSelector("#um-name");
  await page.fill("#um-name", "甲");
  await page.click(".modal button:has-text('保存')");
  await page.waitForFunction(() => document.body.textContent.includes(">甲<") || document.body.textContent.includes("已启用"), null, { timeout: 5000 });
  body = await page.textContent("body");
  assert(body.includes("甲") && body.includes("已启用"), "添加用户后卡片显示用户名与启用状态");
  assert(fs.existsSync(path.join(dataDir, "甲", "config", "configA.txt")), "首次添加用户生成配置快照（data/…/甲/config/configA.txt）");

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
  assert(body.includes("用户名重复"), "重复用户名被拒绝（弹窗保留）");
  await page.click(".modal button:has-text('取消')");

  await page.click('[data-action="edit-user"][data-name="甲"]');
  await page.waitForSelector("#um-name");
  await page.fill("#um-name", "甲改");
  await page.click(".modal button:has-text('保存')");
  await page.waitForFunction(() => document.body.textContent.includes("甲改"), null, { timeout: 5000 });
  assert(fs.existsSync(path.join(dataDir, "甲改", "config", "configA.txt")), "改名后用户数据目录已迁移");
  assert(!fs.existsSync(path.join(dataDir, "甲")), "改名后旧用户目录已不存在（重命名而非复制）");
  const user = "甲改";
  const userDir = path.join(dataDir, user);

  await page.click(`[data-action="edit-user-config"][data-name="${user}"]`);
  await page.waitForSelector(".modal", { timeout: 5000 });
  body = await page.textContent("body");
  assert(body.includes("配置编辑中"), "编辑配置弹窗显示提示");
  assert(fs.readFileSync(cfgFile, "utf8") === "ORIGINAL", "编辑配置开始后配置路径为内部储存副本");
  assert(fs.existsSync(path.join(userDir, "cache", "configA.txt")), "原配置已移入缓存区");
  fs.writeFileSync(cfgFile, "NEWSETUP");
  await page.click('[data-action="edit-config-done"]');
  await page.waitForFunction(() => !document.querySelector(".modal"), null, { timeout: 5000 });
  await sleep(300);
  assert(fs.readFileSync(path.join(userDir, "config", "configA.txt"), "utf8") === "NEWSETUP", "完成后新配置已保存（store）");
  assert(fs.readFileSync(cfgFile, "utf8") === "ORIGINAL", "完成后原配置已还原到配置路径");
  assert(!fs.existsSync(path.join(userDir, "cache", "configA.txt")), "完成后缓存区已清空");

  await page.click(`[data-action="edit-user-config"][data-name="${user}"]`);
  await page.waitForSelector(".modal", { timeout: 5000 });
  fs.writeFileSync(cfgFile, "HALF");
  await page.click('[data-action="edit-config-cancel"]');
  await page.waitForFunction(() => !document.querySelector(".modal"), null, { timeout: 5000 });
  await sleep(300);
  assert(fs.readFileSync(cfgFile, "utf8") === "ORIGINAL", "取消后原配置已还原");
  assert(fs.readFileSync(path.join(userDir, "config", "configA.txt"), "utf8") === "NEWSETUP", "取消不改变已保存的用户配置");

  await page.click('nav a[href="#/dispatch"]');
  await page.waitForSelector("#dc-script");
  await page.selectOption("#dc-script", { label: "用户测试脚本" });
  await page.waitForTimeout(300);
  assert(!(await page.$("#dc-user")), "调度中心无用户选择下拉（启用用户依次运行）");
  await page.click("button:has-text('执行')");
  await waitFor(async () => (await runningCount()) > 0, 10000);
  assert(await waitNoRunning(20000), "运行任务已结束（含配置还原）");
  assert(await waitFor(() => {
    try { return fs.readFileSync(cfgFile, "utf8") === "ORIGINAL"; } catch { return false; }
  }, 5000), "运行结束后原配置已还原（实际：" + fs.readFileSync(cfgFile, "utf8") + "）");
  assert(fs.readFileSync(path.join(userDir, "config", "configA.txt"), "utf8") === "NEWSETUP", "运行结束后用户配置保留");
  const runHist = await (await fetch(baseUrl + "api/history?days=7")).json();
  const manualRecs = runHist.filter(h => h.scriptInstanceId === sid && h.mode === "manual");
  assert(manualRecs.length === 2, "手动执行按启用用户依次运行产生 2 条记录（实际 " + manualRecs.length + "）");

  await page.click('nav a[href="#/queues"]');
  await page.waitForSelector("h2");
  await page.click("button:has-text('新建调度队列')");
  await page.waitForSelector("#qm-name");
  await page.fill("#qm-name", "用户队列测试");
  await page.click("text=+ 添加任务");
  await page.selectOption('[data-task-idx="0"]', { label: "用户测试脚本" });
  await page.waitForTimeout(300);
  const taskUserSel = await page.$('[data-task-user-idx="0"]');
  assert(!taskUserSel, "队列任务行不再显示用户下拉（沿用脚本启用用户依次运行）");
  await page.click(".modal button:has-text('保存')");
  await page.waitForFunction(() => document.body.textContent.includes("用户队列测试"), null, { timeout: 5000 });

  await page.click('nav a[href="#/scripts"]');
  await page.waitForSelector("h2");
  page.once("dialog", d => d.accept());
  await page.click('[data-action="delete-script"][data-name="用户测试脚本"]');
  await waitAbsent(page, "用户测试脚本");
  assert(!fs.existsSync(dataDir), "删除脚本后 data 目录已清理");
  const queues = await (await fetch(baseUrl + "api/queues")).json();
  for (const q of queues) {
    if (q.name === "用户队列测试") await api("DELETE", "/api/queues/" + q.id);
  }
}

async function testQueueMultiUser() {
  console.log("[用例] 队列多用户依次运行 + 配置交换");
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
    configPath: cfgDir.replace(/\\/g, "\\\\"), logPath: muLog.replace(/\\/g, "\\\\"),
    maxAttempts: 1, logStallTimeoutMinutes: 5, totalTimeoutMinutes: 120,
  });
  const sid = (await create.json()).id;

  await api("POST", "/api/scripts/" + sid + "/users", { name: "甲", enabled: true });
  await api("POST", "/api/scripts/" + sid + "/users", { name: "乙", enabled: true });

  const editCfg = (user, action) => api("POST", `api/scripts/${sid}/users/${encodeURIComponent(user)}/edit-config`, { action });

  let r = await editCfg("甲", "start");
  assert(r.ok, "编辑甲配置开始");
  fs.writeFileSync(cfgFile, "NEWA");
  r = await editCfg("甲", "done");
  assert(r.ok, "甲用户配置已提交（store=NEWA）");

  r = await editCfg("乙", "start");
  assert(r.ok, "编辑乙配置开始");
  fs.writeFileSync(cfgFile, "NEWB");
  r = await editCfg("乙", "done");
  assert(r.ok, "乙用户配置已提交（store=NEWB）");
  assert(fs.readFileSync(cfgFile, "utf8") === "ORIGINAL", "提交后配置路径已还原");

  const qr = await api("POST", "/api/queues", {
    name: "多用户队列", autoRunMode: "scheduled", completionAction: "none",
    timeSets: [{ id: "", enabled: true, days: [1], time: "08:00" }],
    tasks: [{ id: "", index: 0, scriptInstanceId: sid }], notifyEnabled: false,
  });
  const qid = (await qr.json()).id;

  const dr = await api("POST", "/api/dispatch/queue", { queueId: qid, mode: "manual" });
  assert(dr.ok, "队列已开始执行");
  assert(await waitNoRunning(60000), "队列运行已结束");

  const hist = await (await fetch(baseUrl + "api/history?days=7")).json();
  const recent = hist.filter(h => h.queueId === qid);
  assert(recent.length === 2, "队列多用户依次运行产生 2 条记录（实际 " + recent.length + "）");
  const names = recent.map(h => h.userName);
  assert(names.includes("甲") && names.includes("乙"), "两条记录分别属于启用用户（甲、乙）");
  assert(fs.readFileSync(cfgFile, "utf8") === "ORIGINAL", "队列运行结束后配置路径已还原");
  assert(fs.readFileSync(path.join(runtimeDir, "data", sid, "甲", "config", "configA.txt"), "utf8") === "NEWA", "甲用户配置保留");
  assert(fs.readFileSync(path.join(runtimeDir, "data", sid, "乙", "config", "configA.txt"), "utf8") === "NEWB", "乙用户配置保留");

  await api("DELETE", "/api/queues/" + qid);
  await api("DELETE", "/api/scripts/" + sid);
  assert(!fs.existsSync(path.join(runtimeDir, "data", sid)), "清理后数据目录已删除");
}

async function testGateRelease() {
  console.log("[用例] 门禁释放：运行中禁止编辑配置，结束后可正常进入");
  const gateCfg = path.join(runtimeDir, "gate-cfg");
  const gateLog = path.join(runtimeDir, "gate-log");
  fs.rmSync(gateCfg, { recursive: true, force: true });
  fs.rmSync(gateLog, { recursive: true, force: true });
  fs.mkdirSync(gateCfg, { recursive: true });
  fs.mkdirSync(gateLog, { recursive: true });
  const ping = "C:\\Windows\\System32\\PING.EXE";
  const create = await api("POST", "/api/scripts", {
    name: "门禁测试脚本", rootPath: runtimeDir.replace(/\\/g, "\\\\"), mainExe: ping,
    args: "-n 8 127.0.0.1", configPath: gateCfg.replace(/\\/g, "\\\\"), logPath: gateLog.replace(/\\/g, "\\\\"),
    maxAttempts: 1, logStallTimeoutMinutes: 5, totalTimeoutMinutes: 120,
  });
  const sid = (await create.json()).id;
  await api("POST", "/api/scripts/" + sid + "/users", { name: "甲", enabled: true });

  await api("POST", "/api/dispatch/script", { scriptId: sid, mode: "manual", userName: "甲" });
  assert(await waitFor(async () => (await runningCount()) > 0, 10000), "脚本已开始运行");
  const during = await api("POST", `api/scripts/${sid}/users/${encodeURIComponent("甲")}/edit-config`, { action: "start" });
  assert(during.status === 409, "运行中编辑配置被拒绝（409，门禁占用）");
  assert(await waitNoRunning(60000), "运行已结束");
  const after = await api("POST", `api/scripts/${sid}/users/${encodeURIComponent("甲")}/edit-config`, { action: "start" });
  assert(after.ok, "运行结束后可正常开始编辑配置（门禁已释放，可继续编辑）");
  const cancel = await api("POST", `api/scripts/${sid}/users/${encodeURIComponent("甲")}/edit-config`, { action: "cancel" });
  assert(cancel.ok, "取消编辑配置正常（会话关闭）");
  await api("DELETE", "/api/scripts/" + sid);
  assert(!fs.existsSync(path.join(runtimeDir, "data", sid)), "门禁测试脚本数据已清理");
}

async function testBatchGameLaunch() {
  console.log("[用例] 批处理游戏启动：有效 stdio + 正常结束");
  const gameBat = path.join(runtimeDir, "game-launch.bat");
  const mainBat = path.join(runtimeDir, "game-main.bat");
  const marker = path.join(runtimeDir, "game-started.flag");
  fs.rmSync(marker, { force: true });
  fs.writeFileSync(gameBat, [
    "@echo off",
    "echo [GAME] started",
    "echo started > \"" + marker + "\"",
    "exit /b 0",
  ].join("\r\n"), "ascii");
  fs.writeFileSync(mainBat, "@echo off\r\necho [MAIN] finished\r\nexit /b 0\r\n", "ascii");

  const create = await api("POST", "/api/scripts", {
    name: "批处理游戏脚本", rootPath: runtimeDir, mainExe: mainBat,
    configPath: runtimeDir, logPath: runtimeDir, launchGame: true, gameExe: gameBat,
    gameArgs: "", gameWaitSeconds: 0, forceCloseGame: false,
    maxAttempts: 1, logStallTimeoutMinutes: 5, totalTimeoutMinutes: 10,
  });
  const script = await create.json();
  assert(create.ok, "创建批处理游戏测试脚本");

  const dispatch = await api("POST", "/api/dispatch/script", { scriptId: script.id, mode: "manual" });
  assert(dispatch.ok, "批处理游戏脚本已开始运行");

  let gameStarted = false;
  const ended = await waitFor(async () => {
    gameStarted = fs.existsSync(marker);
    return (await runningCount()) === 0 && gameStarted;
  }, 20000, 200);
  assert(ended && gameStarted, "批处理游戏已启动且主脚本正常结束");
  await api("DELETE", "/api/scripts/" + script.id);
}

async function testGameProcessConfirm() {
  console.log("[用例] 游戏进程确认：未勾选启动游戏跳过 / 填写路径检测双启动");
  const exitBat = path.join(runtimeDir, "exit-ok.bat");
  fs.writeFileSync(exitBat, "@echo off\r\nexit /b 0\r\n");

  const a = await api("POST", "/api/scripts", {
    name: "空游戏路径脚本", rootPath: runtimeDir, mainExe: exitBat.replace(/\\/g, "\\\\"),
    configPath: runtimeDir, logPath: runtimeDir, launchGame: false, gameExe: "",
    maxAttempts: 1, logStallTimeoutMinutes: 5, totalTimeoutMinutes: 10,
  });
  assert(a.ok, "创建未勾选启动游戏脚本（launchGame=false / gameExe 为空）");
  const aid = (await a.json()).id;
  await api("POST", "/api/dispatch/script", { scriptId: aid, mode: "manual" });
  assert(await waitNoRunning(60000), "未勾选启动游戏脚本运行结束");
  const aHist = await (await fetch(baseUrl + "api/history?days=7")).json();
  const aRec = aHist.filter(h => h.scriptInstanceId === aid).at(-1);
  assert(aRec && aRec.finalStatus === "success", "未勾选启动游戏时跳过游戏启动，运行成功（FinalStatus=success）");
  await api("DELETE", "/api/scripts/" + aid);

  const ping = "C:\\Windows\\System32\\PING.EXE";
  const b = await api("POST", "/api/scripts", {
    name: "双启动确认脚本", rootPath: runtimeDir, mainExe: exitBat.replace(/\\/g, "\\\\"),
    configPath: runtimeDir, logPath: runtimeDir, launchGame: true, gameExe: ping,
    gameArgs: "-n 60 127.0.0.1", gameWaitSeconds: 10, forceCloseGame: true,
    maxAttempts: 1, logStallTimeoutMinutes: 5, totalTimeoutMinutes: 10,
  });
  assert(b.ok, "创建双启动确认脚本（游戏=PING，等待 10 秒确认）");
  const bid = (await b.json()).id;
  await api("POST", "/api/dispatch/script", { scriptId: bid, mode: "manual" });
  const managerLog = path.join(runtimeDir, "logs", "nexus-pipeline-" + localDate().replace(/-/g, "") + ".log");
  const seenConfirm = await waitFor(() => {
    if (!fs.existsSync(managerLog)) return false;
    return fs.readFileSync(managerLog, "utf8").includes("已确认游戏进程启动");
  }, 30000, 500);
  assert(seenConfirm, "运行期间确认游戏进程启动（管理器日志含「已确认游戏进程启动」）");
  assert(await waitNoRunning(60000), "双启动确认脚本运行结束");
  const bHist = await (await fetch(baseUrl + "api/history?days=7")).json();
  const bRec = bHist.filter(h => h.scriptInstanceId === bid).at(-1);
  assert(bRec && bRec.finalStatus === "success", "游戏进程确认后脚本运行成功（FinalStatus=success）");
  await api("DELETE", "/api/scripts/" + bid);
}

async function testForceCloseGating(page) {
  console.log("[用例] 强制关闭游戏依赖启动游戏：后端归一化 + 前端联动禁用");
  const exitBat = path.join(runtimeDir, "exit-ok.bat");
  fs.writeFileSync(exitBat, "@echo off\r\nexit /b 0\r\n");
  const created = await api("POST", "/api/scripts", {
    name: "强制关闭联动脚本", rootPath: runtimeDir, mainExe: exitBat.replace(/\\/g, "\\\\"),
    configPath: runtimeDir, logPath: runtimeDir, launchGame: false, gameExe: "C:\\nonexist\\game.exe", forceCloseGame: true,
    maxAttempts: 1, logStallTimeoutMinutes: 5, totalTimeoutMinutes: 10,
  });
  assert(created.ok, "API 提交 launchGame=false / forceCloseGame=true 成功");
  const sid = (await created.json()).id;
  const list = await (await fetch(baseUrl + "api/scripts")).json();
  const got = list.find(s => s.id === sid);
  assert(got && got.forceCloseGame === false, "后端归一化：LaunchGame=false 时 ForceCloseGame 强制为 false");
  await api("DELETE", "/api/scripts/" + sid);

  await page.goto(baseUrl + "#/dashboard", { waitUntil: "domcontentloaded" });
  await page.goto(baseUrl + "#/scripts", { waitUntil: "domcontentloaded" });
  await page.click('[data-testid="new-script"]');
  await page.waitForSelector(".new-script-chooser", { timeout: 5000 });
  await page.click('[data-action="open-script-type"][data-plugin=""]');
  await page.waitForSelector("#sm-name");
  assert(await page.$eval("#sm-force", el => el.disabled), "未勾选「运行脚本前启动游戏」时强制关闭复选框禁用");
  assert(!(await page.$eval("#sm-force", el => el.checked)), "未勾选启动游戏时强制关闭默认不勾选");
  await page.click("#sm-launch");
  assert(!(await page.$eval("#sm-force", el => el.disabled)), "勾选启动游戏后强制关闭启用");
  await page.click("#sm-force");
  assert(await page.$eval("#sm-force", el => el.checked), "启用后可勾选强制关闭");
  await page.click("#sm-launch");
  assert(await page.$eval("#sm-force", el => el.disabled && !el.checked), "取消启动游戏后强制关闭恢复禁用并取消勾选");
  await page.click(".modal button:has-text('取消')");
}

async function testScriptEditPreservesUsers() {
  console.log("[用例] 编辑脚本保留用户（PUT 不含 users 不覆盖）");
  const keepCfg = path.join(runtimeDir, "keep-cfg");
  fs.rmSync(keepCfg, { recursive: true, force: true });
  fs.mkdirSync(keepCfg, { recursive: true });
  fs.writeFileSync(path.join(keepCfg, "cfg.txt"), "KEEP");
  const kDir = makeScriptDir("keep");
  const created = await createScript({ name: "保留用户脚本", rootPath: kDir.root, mainExe: kDir.main, configPath: keepCfg, logPath: kDir.log });
  assert(created.ok, "创建脚本");
  const sid = created.id;
  const ur = await api("POST", `/api/scripts/${sid}/users`, { name: "甲", enabled: true });
  assert(ur.ok, "添加用户甲");
  assert(fs.existsSync(path.join(runtimeDir, "data", sid, "甲", "config", "cfg.txt")), "添加用户生成配置快照");
  const put = await api("PUT", `/api/scripts/${sid}`, { name: "保留用户脚本-改", rootPath: kDir.root, mainExe: kDir.main, configPath: keepCfg, logPath: kDir.log, maxAttempts: 1, logStallTimeoutMinutes: 5, totalTimeoutMinutes: 120 });
  assert(put.ok, "PUT 改名（payload 不含 users，模拟前端）");
  const list = await (await fetch(baseUrl + "api/scripts")).json();
  const got = list.find(s => s.id === sid);
  assert(got && (got.users || []).length === 1 && got.users[0].name === "甲", "改名后用户仍保留");
  assert(fs.existsSync(path.join(runtimeDir, "data", sid, "甲", "config", "cfg.txt")), "改名后用户数据目录未被重建或丢失");
  await api("DELETE", `/api/scripts/${sid}`);
}

async function testExeOpenGuard() {
  console.log("[用例] 脚本已打开：编辑配置 409 + 手动执行被禁止（400）");
  const ping = "C:\\Windows\\System32\\PING.EXE";
  const cfgDir = path.join(runtimeDir, "open-cfg");
  const logDir = path.join(runtimeDir, "open-log");
  fs.rmSync(cfgDir, { recursive: true, force: true });
  fs.rmSync(logDir, { recursive: true, force: true });
  fs.mkdirSync(cfgDir, { recursive: true });
  fs.mkdirSync(logDir, { recursive: true });
  const created = await api("POST", "/api/scripts", {
    name: "占用检测脚本", rootPath: runtimeDir, mainExe: ping,
    configPath: cfgDir, logPath: logDir,
    maxAttempts: 1, logStallTimeoutMinutes: 5, totalTimeoutMinutes: 120,
  });
  const sid = (await created.json()).id;
  assert(created.ok, "创建占用检测脚本（mainExe=PING.EXE）");
  await api("POST", `/api/scripts/${sid}/users`, { name: "甲", enabled: true });

  const pinger = spawn(ping, ["-n", "60", "127.0.0.1"], { stdio: "ignore" });
  try {
    await sleep(1200);
    const start = await api("POST", `/api/scripts/${sid}/users/甲/edit-config`, { action: "start" });
    assert(start.status === 409, "编辑配置被拒（409，脚本程序已打开）");
    const startBody = await start.json();
    assert(startBody.error.includes("检测到已打开的脚本"), "拒绝原因提示「检测到已打开的脚本，退出脚本后才能编辑配置。」");

    const dr = await api("POST", "/api/dispatch/script", { scriptId: sid, mode: "manual" });
    assert(dr.status === 400, "脚本已打开时手动执行被禁止（400）");
    const drBody = await dr.json();
    assert((drBody.error || "").includes("正在运行"), "拒绝原因含「正在运行，请先退出后再执行」");
  } finally {
    pinger.kill();
  }
  await api("DELETE", `/api/scripts/${sid}`);
}

async function testPathQuoteNormalize() {
  console.log("[用例] 脚本路径引号去除（成对首尾引号）");
  const qDir = makeScriptDir("quote");
  const created = await api("POST", "/api/scripts", {
    name: "引号路径脚本", rootPath: `"${qDir.root}"`, mainExe: `'${qDir.main}'`,
    configPath: `"${qDir.cfg}"`, logPath: `'${qDir.log}'`,
    gameExe: `"${qDir.main}"`, maxAttempts: 1, logStallTimeoutMinutes: 5, totalTimeoutMinutes: 120,
  });
  assert(created.ok, "POST 带引号路径成功");
  const sid = (await created.json()).id;
  const list = await (await fetch(baseUrl + "api/scripts")).json();
  const got = list.find(s => s.id === sid);
  assert(got && got.rootPath === qDir.root && got.mainExe === qDir.main
    && got.configPath === qDir.cfg && got.logPath === qDir.log
    && got.gameExe === qDir.main, "路径已去除成对引号");
  await api("DELETE", "/api/scripts/" + sid);
}

async function testPathValidation() {
  console.log("[用例] 路径合规校验：通用脚本假路径 / 专项根目录 / 游戏路径 校验拒绝");
  const p = makeScriptDir("pathval");
  const post = body => api("POST", "/api/scripts", { maxAttempts: 1, logStallTimeoutMinutes: 5, totalTimeoutMinutes: 120, ...body });
  const badRoot = await post({ name: "假路径脚本", rootPath: "C:\\no-such-dir-xyz", mainExe: "C:\\no-such-dir-xyz\\run.bat", configPath: "C:\\no-such-dir-xyz\\cfg", logPath: "C:\\no-such-dir-xyz\\logs" });
  assert(badRoot.status === 400 && (await badRoot.json()).error.includes("根目录"), "根目录不存在被拒（400）");
  const badMain = await post({ name: "假主程序脚本", rootPath: p.root, mainExe: path.join(p.root, "no.exe"), configPath: p.cfg, logPath: p.log });
  assert(badMain.status === 400 && (await badMain.json()).error.includes("主程序"), "主程序不存在被拒（400）");
  const badCfg = await post({ name: "假配置脚本", rootPath: p.root, mainExe: p.main, configPath: path.join(p.root, "no-cfg"), logPath: p.log });
  assert(badCfg.status === 400 && (await badCfg.json()).error.includes("配置"), "配置路径不存在被拒（400）");
  const badLog = await post({ name: "非法日志脚本", rootPath: p.root, mainExe: p.main, configPath: p.cfg, logPath: path.join(p.log, "a?b.log") });
  assert(badLog.status === 400 && (await badLog.json()).error.includes("日志路径"), "日志路径含非法字符被拒（400）");
  const badGame = await post({ name: "空游戏脚本", rootPath: p.root, mainExe: p.main, configPath: p.cfg, logPath: p.log, launchGame: true, gameExe: "" });
  assert(badGame.status === 400 && (await badGame.json()).error.includes("游戏"), "勾选启动游戏且游戏路径为空被拒（400）");
  const spBad = await post({ name: "专项假根目录", rootPath: "C:\\no-such-zenless", pluginType: "zzzonedragon" });
  assert(spBad.status === 400 && (await spBad.json()).error.includes("根目录"), "专项脚本根目录不存在被拒（400）");
  const good = await post({ name: "合规脚本", rootPath: p.root, mainExe: p.main, configPath: p.cfg, logPath: path.join(p.log, "{YYYY-MM-DD}.log") });
  assert(good.ok, "真实路径且日志格式合规通过（存在性校验不误伤）");
  await api("DELETE", "/api/scripts/" + (await good.json()).id);
}

async function testV020Features(page) {
  console.log("[用例] v0.2.0：幽灵联动 / 字段改名 / fs 浏览 / 通知开关 / 时间选择器");
  await page.click('nav a[href="#/scripts"]');
  await page.waitForSelector("h2");
  await page.click('[data-testid="new-script"]');
  await page.waitForSelector(".new-script-chooser", { timeout: 5000 });
  await page.click('[data-action="open-script-type"][data-plugin=""]');
  await page.waitForSelector("#sm-root");
  const exeDisabled = await page.$eval("#sm-exe", el => el.disabled);
  const argsDisabled = await page.$eval("#sm-args", el => el.disabled);
  const logDisabled = await page.$eval("#sm-log", el => el.disabled);
  assert(exeDisabled && argsDisabled && logDisabled, "根目录未填时主程序/参数/日志输入禁用（幽灵状态）");
  const vDir = makeScriptDir("v020");
  await page.fill("#sm-root", vDir.root.replace(/\\/g, "\\\\"));
  await page.waitForFunction(() => document.querySelector("#sm-exe") && !document.querySelector("#sm-exe").disabled, null, { timeout: 5000 });
  const exeEnabled = await page.$eval("#sm-exe", el => !el.disabled);
  assert(exeEnabled, "填写根目录后输入启用");
  assert((await page.textContent("body")).includes("日志路径"), "日志字段已改名「日志路径（支持日期占位符与通配符）」");
  await page.click(".modal button:has-text('取消')");

  const fsList = await (await fetch(baseUrl + "api/fs/browse")).json();
  assert((fsList.dirs || []).some(d => /^C:\\$/.test(d)), "fs browse 返回盘符列表（含 C:\\）");
  const fsSub = await (await fetch(baseUrl + "api/fs/browse?path=" + encodeURIComponent("C:\\"))).json();
  assert(Array.isArray(fsSub.dirs) && Array.isArray(fsSub.files), "fs browse 返回目录与文件列表");

  const put = await api("PUT", "/api/settings", { webhookEnabled: true, smtpEnabled: true });
  assert(put.ok, "PUT 设置通知开关成功");
  const got = await (await fetch(baseUrl + "api/settings")).json();
  const gWh = got.settings.webhookEnabled;
  const gSm = got.settings.smtpEnabled;
  assert(gWh === true && gSm === true, "GET 返回通知开关一致（webhook=" + gWh + " smtp=" + gSm + "）");
  await api("PUT", "/api/settings", { smtpEnabled: false });

  await page.click('nav a[href="#/settings"]');
  await page.waitForSelector("#st-port");
  const setBody = await page.textContent("body");
  assert(!setBody.includes("发送策略"), "设置页已无发送策略");
  assert(!setBody.includes("Webhook 通知"), "设置页不再包含通知配置（已移至插件配置）");

  await page.click('nav a[href="#/queues"]');
  await page.waitForSelector("h2");
  await page.click("button:has-text('新建调度队列')");
  await page.waitForSelector("#qm-name");
  const tsType = await page.$eval("[data-ts-time='0']", el => el.type);
  assert(tsType === "time", "定时执行时间为原生时间选择器（type=time）");
  await page.click(".modal button:has-text('取消')");
  await page.click('nav a[href="#/scripts"]');
  await page.waitForSelector("h2");
}

function scriptHistoryLog(scriptId) {
  const historyRoot = path.join(runtimeDir, "history");
  if (!fs.existsSync(historyRoot)) return "";
  const dirs = fs.readdirSync(historyRoot).filter(d => /^\d{4}-\d{2}-\d{2}$/.test(d)).sort().reverse();
  for (const dir of dirs) {
    const files = fs.readdirSync(path.join(historyRoot, dir)).filter(f => f.endsWith(".json")).sort();
    for (const f of files) {
      const rec = JSON.parse(fs.readFileSync(path.join(historyRoot, dir, f), "utf8").replace(/^\uFEFF/, ""));
      if (rec.ScriptInstanceId === scriptId) {
        const logFile = path.join(historyRoot, dir, f.replace(".json", ".log"));
        return fs.existsSync(logFile) ? fs.readFileSync(logFile, "utf8").replace(/^\uFEFF/, "") : "";
      }
    }
  }
  return "";
}

async function testSpecializedScript(page) {
  console.log("[用例] 专用插件：BetterGI 适配 / probe / 简化弹窗 / 新建卡片 / 图标");
  const bgiRoot = path.join(runtimeDir, "sim-bettergi");
  fs.rmSync(bgiRoot, { recursive: true, force: true });
  fs.mkdirSync(path.join(bgiRoot, "log"), { recursive: true });
  fs.writeFileSync(path.join(bgiRoot, "BetterGI.exe"), "");

  const st = await (await fetch(baseUrl + "api/status")).json();
  const bgi = (st.plugins || []).find(p => p.name === "bettergi");
  assert(bgi && bgi.kind === "specialized" && bgi.enabled, "BetterGI 专用插件已加载且启用（kind=specialized）");
  assert(bgi && bgi.gameName === "原神", "BetterGI 插件提供游戏名（gameName=原神）");

  const probeOk = await api("POST", "/api/scripts/probe", { rootPath: bgiRoot.replace(/\\/g, "\\\\"), pluginType: "bettergi" });
  const profile = (await probeOk.json()).profile;
  assert(probeOk.ok && profile.mainExe.endsWith("BetterGI.exe"), "probe 推导出主程序路径");
  assert(profile.args === "--startOneDragon", "probe 推导出自启动参数 --startOneDragon");
  assert(profile.configPath.includes("默认配置.json"), "probe 推导出配置文件路径");
  assert(profile.logPath.includes("{YYYYMMDD}"), "probe 推导出日志格式路径（better-genshin-impact{YYYYMMDD}.log）");
  assert(profile.successMarkers === "一条龙和配置组任务结束", "probe 推导完成标志（一条龙和配置组任务结束）");
  const probeBad = await api("POST", "/api/scripts/probe", { rootPath: path.join(runtimeDir, "no-bgi"), pluginType: "bettergi" });
  assert(probeBad.status === 400, "probe 对无法推导的根目录返回 400");

  const created = await api("POST", "/api/scripts", { name: "专项脚本A", rootPath: bgiRoot.replace(/\\/g, "\\\\"), pluginType: "bettergi", maxAttempts: 1, logStallTimeoutMinutes: 5, totalTimeoutMinutes: 120 });
  assert(created.ok, "API 创建专用脚本实例成功");
  const sid = (await created.json()).id;
  const list = await (await fetch(baseUrl + "api/scripts")).json();
  const got = list.find(s => s.id === sid);
  assert(got && got.pluginType === "bettergi", "专用实例保存 pluginType=bettergi");
  assert(got.mainExe.endsWith("BetterGI.exe") && got.args === "--startOneDragon", "主程序/自启动参数由插件固化");
  assert(got.configPath.includes("默认配置.json") && got.logPath.includes("{YYYYMMDD}"), "配置/日志路径由插件固化");
  assert(got.successMarkers === "一条龙和配置组任务结束", "完成标志由插件固化");
  const cfg = JSON.parse(fs.readFileSync(path.join(runtimeDir, "config", "scripts.json"), "utf8").replace(/^\uFEFF/, ""));
  const cfgGot = cfg.find(s => s.Id === sid);
  assert(cfgGot && cfgGot.PluginType === "bettergi", "scripts.json 落盘 PluginType（PascalCase）");
  assert(fs.readFileSync(path.join(runtimeDir, "config", "scripts.json"), "utf8").includes("专项脚本A"), "scripts.json 中文以原字符落盘（无 \\u 转义）");

  const bad = await api("POST", "/api/scripts", { name: "专项脚本B", rootPath: path.join(runtimeDir, "no-bgi"), pluginType: "bettergi", maxAttempts: 1, logStallTimeoutMinutes: 5, totalTimeoutMinutes: 120 });
  assert(bad.status === 400, "根目录无法推导时创建被拒（400）");

  const iconOk = await createScript({ name: "图标脚本", rootPath: runtimeDir, mainExe: "C:\\Windows\\explorer.exe", configPath: runtimeDir, logPath: runtimeDir });
  assert(iconOk.ok, "创建图标测试脚本（mainExe 为带高分辨率图标的系统 exe）");
  const iconRes = await fetch(baseUrl + "api/scripts/" + iconOk.id + "/icon");
  assert(iconRes.status === 200 && (iconRes.headers.get("content-type") || "").includes("image/png"), "图标 API 返回 PNG");
  const iconBytes = Buffer.from(await iconRes.arrayBuffer());
  assert(iconBytes.length > 24 && iconBytes.readUInt32BE(16) >= 48, "图标 API 返回最高分辨率图标（PNG 宽度 ≥ 48）");
  await api("DELETE", "/api/scripts/" + iconOk.id);
  const noIconBat = path.join(runtimeDir, "no-icon.bat");
  fs.writeFileSync(noIconBat, "@echo off\r\nexit /b 0\r\n", "ascii");
  const noIcon = await createScript({ name: "无图标脚本", rootPath: runtimeDir, mainExe: noIconBat.replace(/\\/g, "\\\\"), configPath: runtimeDir, logPath: runtimeDir });
  const icon404 = await fetch(baseUrl + "api/scripts/" + noIcon.id + "/icon");
  assert(icon404.status === 404, "主程序无图标资源时图标 API 返回 404");
  await api("DELETE", "/api/scripts/" + noIcon.id);

  await page.goto(baseUrl + "#/dashboard", { waitUntil: "domcontentloaded" });
  await page.goto(baseUrl + "#/scripts", { waitUntil: "domcontentloaded" });
  await page.waitForFunction(() => document.body.textContent.includes("专项脚本A"), null, { timeout: 5000 });
  const cardText = await page.textContent('[data-testid="script-card"]');
  assert(cardText.includes("原神专项"), "脚本卡片显示专项标识（原神专项，游戏名由插件提供）");
  assert(await page.$('[data-testid="script-card"] img.script-ico'), "脚本卡片含主程序图标（img）");

  await page.click('[data-testid="new-script"]');
  await page.waitForSelector(".new-script-chooser", { timeout: 5000 });
  const chooserText = await page.textContent(".new-script-chooser");
  assert(chooserText.includes("新建通用脚本实例") && chooserText.includes("新建BetterGI专项脚本实例"), "选择卡片层含「新建通用脚本实例」与「新建BetterGI专项脚本实例」两张卡片");
  await page.click('[data-action="open-script-type"][data-plugin="bettergi"]');
  await page.waitForSelector("#sm-name");
  assert(!(await page.$("#sm-exe")) && !(await page.$("#sm-args")) && !(await page.$("#sm-config")) && !(await page.$("#sm-log")), "简化弹窗移除主程序/参数/配置/日志字段");
  await page.fill("#sm-name", "专项UI脚本");
  await page.fill("#sm-root", bgiRoot.replace(/\\/g, "\\\\"));
  await page.click(".modal button:has-text('保存')");
  await page.waitForFunction(() => document.body.textContent.includes("专项UI脚本"), null, { timeout: 5000 });
  assert(true, "简化弹窗保存成功（根目录 change 触发 probe 校验）");
  await page.click('[data-action="edit-script"]');
  await page.waitForSelector("#sm-name");
  assert(!(await page.$("#sm-exe")), "编辑专用实例仍为简化弹窗（无主程序字段）");
  await page.click(".modal button:has-text('取消')");

  page.once("dialog", d => d.accept());
  await page.click('[data-action="delete-script"][data-name="专项UI脚本"]');
  await waitAbsent(page, "专项UI脚本");
  assert(true, "删除专项 UI 脚本成功");
  await api("DELETE", "/api/scripts/" + sid);
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

async function testLaunchTargetArgs(page) {
  console.log("[用例] 自启动参数显式路径：运行时启动目标（管理端/执行端分离）/ 普通参数不受影响 / 解析失败回退 / 编辑配置门禁");
  const ltDir = path.join(runtimeDir, "lt-scripts");
  fs.rmSync(ltDir, { recursive: true, force: true });
  fs.mkdirSync(ltDir, { recursive: true });
  const managerFlag = path.join(ltDir, "launcher-ran.flag");
  const execFlag = path.join(ltDir, "exec-ran.flag");
  const execArgsFlag = path.join(ltDir, "exec-args.flag");
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
    configPath: ltDir.replace(/\\/g, "\\\\"), logPath: logPattern, successMarkers: "done",
  });
  assert(s1.ok, "创建启动目标脚本（Args 显式相对路径，含空格无引号 + ? 前带空格）");
  await api("POST", "/api/dispatch/script", { scriptId: s1.id, mode: "manual" });
  assert(await waitNoRunning(30000), "相对路径场景运行结束");
  assert(fs.existsSync(execFlag), "运行时启动目标为 Args 路径（exec target.bat 执行端被启动，含空格无需引号）");
  assert(!fs.existsSync(managerFlag), "主程序（launcher.bat 管理端）未被启动");
  assert(fs.existsSync(execArgsFlag) && fs.readFileSync(execArgsFlag, "utf8").includes("-x marker"), "? 之后的启动目标参数原样传入（-x marker）");
  const rec1 = findHistoryRecord(s1.id);
  assert(rec1 && rec1.Status === "success", "相对路径场景历史记录成功（日志完成标志判定）");
  await api("DELETE", "/api/scripts/" + s1.id);

  fs.rmSync(execFlag, { force: true });
  const s2 = await createScript({
    name: "启动目标绝对", rootPath: ltDir.replace(/\\/g, "\\\\"),
    mainExe: launcherBat.replace(/\\/g, "\\\\"), args: execBat.replace(/\\/g, "\\\\"),
    configPath: ltDir.replace(/\\/g, "\\\\"), logPath: logPattern,
  });
  await api("POST", "/api/dispatch/script", { scriptId: s2.id, mode: "manual" });
  assert(await waitNoRunning(30000), "绝对路径场景运行结束");
  assert(fs.existsSync(execFlag), "绝对路径（含空格无引号）同样作为运行时启动目标");
  await api("DELETE", "/api/scripts/" + s2.id);

  const s3 = await createScript({
    name: "普通参数脚本", rootPath: ltDir.replace(/\\/g, "\\\\"),
    mainExe: launcherBat.replace(/\\/g, "\\\\"), args: "-x marker",
    configPath: ltDir.replace(/\\/g, "\\\\"), logPath: logPattern,
  });
  await api("POST", "/api/dispatch/script", { scriptId: s3.id, mode: "manual" });
  assert(await waitNoRunning(30000), "普通参数场景运行结束");
  const managerOut = fs.existsSync(managerFlag) ? fs.readFileSync(managerFlag, "utf8") : "";
  assert(managerOut.includes("LAUNCHER-RAN") && managerOut.includes("-x"), "普通参数首项不视为路径，主程序被启动且参数原样传入");
  await api("DELETE", "/api/scripts/" + s3.id);

  fs.rmSync(execFlag, { force: true });
  const sQ = await createScript({
    name: "引号参数脚本", rootPath: ltDir.replace(/\\/g, "\\\\"),
    mainExe: launcherBat.replace(/\\/g, "\\\\"), args: '".\\exec target.bat"',
    configPath: ltDir.replace(/\\/g, "\\\\"), logPath: logPattern,
  });
  assert(sQ.ok, "创建引号包裹路径脚本");
  await api("POST", "/api/dispatch/script", { scriptId: sQ.id, mode: "manual" });
  assert(await waitNoRunning(30000), "引号场景运行结束");
  assert(!fs.existsSync(execFlag), "Args 禁止引号：引号包裹路径不视为启动目标（exec 未被启动）");
  const managerOut5 = fs.existsSync(managerFlag) ? fs.readFileSync(managerFlag, "utf8") : "";
  assert(managerOut5.includes("exec target.bat"), "引号内容按普通参数原样传给主程序");
  await api("DELETE", "/api/scripts/" + sQ.id);

  const logFile = path.join(runtimeDir, "logs", "nexus-pipeline-" + localDate().replace(/-/g, "") + ".log");
  const readLog = () => fs.existsSync(logFile) ? fs.readFileSync(logFile, "utf8").replace(/^\uFEFF/, "") : "";
  const logBefore = readLog();
  const s4 = await createScript({
    name: "启动目标缺失", rootPath: ltDir.replace(/\\/g, "\\\\"),
    mainExe: launcherBat.replace(/\\/g, "\\\\"), args: "..\\no-such-exec.bat",
    configPath: ltDir.replace(/\\/g, "\\\\"), logPath: logPattern,
  });
  await api("POST", "/api/dispatch/script", { scriptId: s4.id, mode: "manual" });
  assert(await waitNoRunning(30000), "解析失败场景运行结束");
  assert(fs.existsSync(managerFlag) && fs.readFileSync(managerFlag, "utf8").split("LAUNCHER-RAN").length - 1 >= 2, "显式路径无法解析为可执行文件时回退主程序（launcher.bat 被启动）");
  assert(await waitFor(() => readLog().includes("[警告] 脚本自启动参数含显式路径"), 5000), "回退时输出警告日志");
  await api("DELETE", "/api/scripts/" + s4.id);

  const cmdCopy = path.join(ltDir, "lt-exec.exe");
  fs.copyFileSync("C:\\Windows\\System32\\cmd.exe", cmdCopy);
  const s5 = await api("POST", "/api/scripts", {
    name: "启动目标门禁", rootPath: ltDir.replace(/\\/g, "\\\\"),
    mainExe: launcherBat.replace(/\\/g, "\\\\"), args: ".\\lt-exec.exe",
    configPath: ltDir.replace(/\\/g, "\\\\"), logPath: logPattern,
    maxAttempts: 1, logStallTimeoutMinutes: 5, totalTimeoutMinutes: 120,
  });
  const sid5 = (await s5.json()).id;
  await api("POST", "/api/scripts/" + sid5 + "/users", { name: "甲", enabled: true });
  const execProc = spawn(cmdCopy, ["/c", "ping -n 60 127.0.0.1"], { stdio: "ignore" });
  await waitFor(async () => {
    try { const p = await fetch(baseUrl + "api/status"); await p.json(); } catch { return false; }
    return true;
  }, 3000);
  await sleep(800);
  const during = await api("POST", `api/scripts/${sid5}/users/甲/edit-config`, { action: "start" });
  assert(during.status === 409, "运行时启动目标在运行 → 编辑配置被拒绝（409）");
  spawn("taskkill.exe", ["/PID", String(execProc.pid), "/T", "/F"], { stdio: "ignore" });
  await sleep(800);
  const after = await api("POST", `api/scripts/${sid5}/users/甲/edit-config`, { action: "start" });
  assert(after.ok, "启动目标退出后可正常开始编辑配置");
  const cancel = await api("POST", `api/scripts/${sid5}/users/甲/edit-config`, { action: "cancel" });
  assert(cancel.ok, "取消编辑配置正常（会话关闭）");
  await api("DELETE", "/api/scripts/" + sid5);
}

async function testMarch7thPlugin(page) {
  console.log("[用例] 专用插件：March7thAssistant 适配 / probe / 启动目标推导 / 上级目录执行端");
  const mRoot = path.join(runtimeDir, "sim-march7th");
  fs.rmSync(mRoot, { recursive: true, force: true });
  fs.mkdirSync(mRoot, { recursive: true });
  fs.writeFileSync(path.join(mRoot, "March7th Launcher.exe"), "");
  fs.writeFileSync(path.join(mRoot, "March7th Assistant.exe"), "");

  const st = await (await fetch(baseUrl + "api/status")).json();
  const m7 = (st.plugins || []).find(p => p.name === "march7th");
  assert(m7 && m7.kind === "specialized" && m7.enabled, "March7thAssistant 专用插件已加载且启用（kind=specialized）");
  assert(m7 && m7.gameName === "崩坏：星穹铁道", "March7thAssistant 插件提供游戏名（gameName=崩坏：星穹铁道）");

  const probeOk = await api("POST", "/api/scripts/probe", { rootPath: mRoot.replace(/\\/g, "\\\\"), pluginType: "march7th" });
  assert(probeOk.ok, "march7th probe 成功");
  const profile = (await probeOk.json()).profile;
  assert(profile.mainExe.endsWith("March7th Launcher.exe"), "probe 主程序为 Launcher（编辑配置用）");
  assert(profile.args === ".\\March7th Assistant.exe", "probe 启动目标为显式相对路径（.\\ 前缀，无引号）");
  assert(profile.configPath.endsWith("config.yaml"), "probe 推导配置文件 config.yaml");
  assert(profile.logPath.includes("{YYYY-MM-DD}.log"), "probe 推导日志路径 logs/{YYYY-MM-DD}.log");
  assert(profile.successMarkers === "游戏终止：StarRail", "probe 推导完成标志（游戏终止：StarRail）");

  const probeBad = await api("POST", "/api/scripts/probe", { rootPath: path.join(runtimeDir, "no-m7"), pluginType: "march7th" });
  assert(probeBad.status === 400, "march7th probe 对无法推导的根目录返回 400");

  const mUp = path.join(runtimeDir, "sim-m7-up");
  fs.rmSync(mUp, { recursive: true, force: true });
  fs.mkdirSync(mUp, { recursive: true });
  fs.writeFileSync(path.join(mUp, "March7th Launcher.exe"), "");
  fs.writeFileSync(path.join(runtimeDir, "March7th Assistant.exe"), "");
  const probeUp = await api("POST", "/api/scripts/probe", { rootPath: mUp.replace(/\\/g, "\\\\"), pluginType: "march7th" });
  assert(probeUp.ok, "执行端在上级目录时 probe 成功");
  const upProfile = (await probeUp.json()).profile;
  assert(upProfile.mainExe.endsWith("March7th Launcher.exe") && upProfile.args.startsWith("..\\"), "上级目录场景：主程序 Launcher + Args 为 ..\\ 显式相对路径");
  fs.rmSync(path.join(runtimeDir, "March7th Assistant.exe"), { force: true });

  const created = await api("POST", "/api/scripts", {
    name: "专项M7脚本", rootPath: mRoot.replace(/\\/g, "\\\\"), pluginType: "march7th",
    maxAttempts: 1, logStallTimeoutMinutes: 5, totalTimeoutMinutes: 120,
  });
  assert(created.ok, "API 创建 march7th 专项脚本实例成功");
  const sid = (await created.json()).id;
  const list = await (await fetch(baseUrl + "api/scripts")).json();
  const got = list.find(s => s.id === sid);
  assert(got && got.pluginType === "march7th", "专项实例保存 pluginType=march7th");
  assert(got.mainExe.endsWith("March7th Launcher.exe"), "主程序由插件固化（Launcher）");
  assert(got.args === ".\\March7th Assistant.exe", "Args 启动目标由插件固化（.\\ 显式相对路径，无引号）");
  assert(got.configPath.endsWith("config.yaml") && got.logPath.includes("{YYYY-MM-DD}.log"), "配置/日志路径由插件固化");
  assert(got.successMarkers === "游戏终止：StarRail", "完成标志由插件固化（游戏终止：StarRail）");
  await api("DELETE", "/api/scripts/" + sid);

  await page.goto(baseUrl + "#/scripts", { waitUntil: "domcontentloaded" });
  await page.waitForSelector('[data-testid="new-script"]', { timeout: 5000 });
  await page.click('[data-testid="new-script"]');
  await page.waitForSelector(".new-script-chooser", { timeout: 5000 });
  const chooserText = await page.textContent(".new-script-chooser");
  assert(chooserText.includes("新建March7thAssistant专项脚本实例"), "选择卡片层含「新建March7thAssistant专项脚本实例」卡片");
  await page.click(".modal button:has-text('取消')");
}

async function testZenlessPlugin(page) {
  console.log("[用例] 专用插件：ZenlessZoneZeroOneDragon 适配 / probe / 固化 / 新建卡片");
  const zRoot = path.join(runtimeDir, "sim-zenless");
  fs.rmSync(zRoot, { recursive: true, force: true });
  fs.mkdirSync(path.join(zRoot, ".log"), { recursive: true });
  fs.mkdirSync(path.join(zRoot, "config"), { recursive: true });
  fs.writeFileSync(path.join(zRoot, "OneDragon-Launcher.exe"), "");

  const st = await (await fetch(baseUrl + "api/status")).json();
  const z = (st.plugins || []).find(p => p.name === "zzzonedragon");
  assert(z && z.kind === "specialized" && z.enabled, "ZenlessZoneZeroOneDragon 专用插件已加载且启用（kind=specialized）");
  assert(z && z.gameName === "绝区零", "ZenlessZoneZeroOneDragon 插件提供游戏名（gameName=绝区零）");

  const probeOk = await api("POST", "/api/scripts/probe", { rootPath: zRoot.replace(/\\/g, "\\\\"), pluginType: "zzzonedragon" });
  assert(probeOk.ok, "zzzonedragon probe 成功");
  const profile = (await probeOk.json()).profile;
  assert(profile.mainExe.endsWith("OneDragon-Launcher.exe"), "probe 主程序为 OneDragon-Launcher.exe");
  assert(profile.args === "-o -c", "probe 推导启动参数 -o -c");
  assert(profile.configPath.endsWith("config"), "probe 推导配置文件目录 config");
  assert(profile.logPath.includes(".log") && profile.logPath.endsWith("log.txt"), "probe 推导日志路径 .log/log.txt（固定文件）");
  assert(profile.successMarkers === "关闭游戏成功", "probe 推导完成标志（关闭游戏成功）");

  const probeBad = await api("POST", "/api/scripts/probe", { rootPath: path.join(runtimeDir, "no-zenless"), pluginType: "zzzonedragon" });
  assert(probeBad.status === 400, "zzzonedragon probe 对无法推导的根目录返回 400");

  const created = await api("POST", "/api/scripts", {
    name: "专项ZEN脚本", rootPath: zRoot.replace(/\\/g, "\\\\"), pluginType: "zzzonedragon",
    maxAttempts: 1, logStallTimeoutMinutes: 5, totalTimeoutMinutes: 120,
  });
  assert(created.ok, "API 创建 zzzonedragon 专项脚本实例成功");
  const sid = (await created.json()).id;
  const list = await (await fetch(baseUrl + "api/scripts")).json();
  const got = list.find(s => s.id === sid);
  assert(got && got.pluginType === "zzzonedragon", "专项实例保存 pluginType=zzzonedragon");
  assert(got.mainExe.endsWith("OneDragon-Launcher.exe") && got.args === "-o -c", "主程序/启动参数由插件固化");
  assert(got.configPath.endsWith("config") && got.logPath.endsWith("log.txt"), "配置/日志路径由插件固化");
  assert(got.successMarkers === "关闭游戏成功", "完成标志由插件固化（关闭游戏成功）");
  await api("DELETE", "/api/scripts/" + sid);

  await page.goto(baseUrl + "#/scripts", { waitUntil: "domcontentloaded" });
  await page.waitForSelector('[data-testid="new-script"]', { timeout: 5000 });
  await page.click('[data-testid="new-script"]');
  await page.waitForSelector(".new-script-chooser", { timeout: 5000 });
  const chooserText = await page.textContent(".new-script-chooser");
  assert(chooserText.includes("新建ZenlessZoneZeroOneDragon专项脚本实例"), "选择卡片层含「新建ZenlessZoneZeroOneDragon专项脚本实例」卡片");
  assert(await page.$$eval(".new-script-chooser .scroll-text", els => els.some(el => el.classList.contains("scrolling"))), "长卡片名溢出时启用文字滚动（.scrolling）");
  await page.click(".modal button:has-text('取消')");
}

async function testLogPattern(page) {
  console.log("[用例] 日志路径格式：严格匹配 / 无条目超时失败 / 已有日志忽略 / 通配轮换");
  const logRoot = path.join(runtimeDir, "lp-logs");
  fs.rmSync(logRoot, { recursive: true, force: true });
  fs.mkdirSync(path.join(logRoot, "b"), { recursive: true });
  fs.mkdirSync(path.join(logRoot, "c"), { recursive: true });

  const ping = "C:\\Windows\\System32\\PING.EXE";

  const a = await api("POST", "/api/scripts", {
    name: "无条目脚本", rootPath: runtimeDir, mainExe: ping, args: "-n 90 127.0.0.1",
    configPath: runtimeDir, logPath: path.join(logRoot, "a", "run-{YYYY-MM-DD}.log").replace(/\\/g, "\\\\"),
    maxAttempts: 1, logStallTimeoutMinutes: 1, totalTimeoutMinutes: 10,
  });
  const aid = (await a.json()).id;
  await api("POST", "/api/dispatch/script", { scriptId: aid, mode: "manual" });
  assert(await waitNoRunning(120000), "无条目脚本运行结束（约 1 分钟后无日志条目超时）");
  const hist = await (await fetch(baseUrl + "api/history?days=7")).json();
  const rec = hist.filter(h => h.scriptInstanceId === aid).at(-1);
  assert(rec && rec.finalStatus === "failed" && (rec.resultDetail || "").includes("未产生日志条目"), "启动后无日志条目等待无更新超时后失败（FinalStatus=failed）");
  await api("DELETE", "/api/scripts/" + aid);

  const bLog = path.join(logRoot, "b", "run-" + localDate() + ".log");
  fs.writeFileSync(bLog, "OLD-CONTENT-PREEXISTING\r\n");
  const bBat = path.join(runtimeDir, "lp-b.bat");
  fs.writeFileSync(bBat, "@echo off\r\necho [SCRIPT] NEW-ENTRY-BRANDNEW >> \"" + bLog + "\"\r\necho [SCRIPT] 任务完成 >> \"" + bLog + "\"\r\nexit /b 0\r\n", "ascii");
  const b = await api("POST", "/api/scripts", {
    name: "忽略旧日志脚本", rootPath: runtimeDir, mainExe: bBat.replace(/\\/g, "\\\\"),
    configPath: runtimeDir, logPath: path.join(logRoot, "b", "run-{YYYY-MM-DD}.log").replace(/\\/g, "\\\\"),
    maxAttempts: 1, logStallTimeoutMinutes: 5, totalTimeoutMinutes: 10, successMarkers: "任务完成",
  });
  const bid = (await b.json()).id;
  await api("POST", "/api/dispatch/script", { scriptId: bid, mode: "manual" });
  assert(await waitNoRunning(60000), "忽略旧日志脚本运行结束");
  const sl = scriptHistoryLog(bid);
  assert(sl.includes("NEW-ENTRY-BRANDNEW"), "历史日志含运行期间新条目");
  assert(!sl.includes("OLD-CONTENT-PREEXISTING"), "历史日志忽略运行前已有内容");
  await api("DELETE", "/api/scripts/" + bid);

  const cDir = path.join(logRoot, "c");
  const cOld = path.join(cDir, "run-" + localDate() + "-1.log");
  fs.writeFileSync(cOld, "OLD-ROUND\r\n");
  const cBat = path.join(runtimeDir, "lp-c.bat");
  fs.writeFileSync(cBat, [
    "@echo off",
    "ping 127.0.0.1 -n 3 >nul",
    "del /q \"" + cOld + "\" 2>nul",
    "echo [SCRIPT] ROUND2-NEW >> \"" + path.join(cDir, "run-" + localDate() + "-2.log") + "\"",
    "echo [SCRIPT] 任务完成 >> \"" + path.join(cDir, "run-" + localDate() + "-2.log") + "\"",
    "ping 127.0.0.1 -n 2 >nul",
    "exit /b 0",
  ].join("\r\n"), "ascii");
  const c = await api("POST", "/api/scripts", {
    name: "通配轮换脚本", rootPath: runtimeDir, mainExe: cBat.replace(/\\/g, "\\\\"),
    configPath: runtimeDir, logPath: path.join(cDir, "run-{YYYY-MM-DD-*}.log").replace(/\\/g, "\\\\"),
    maxAttempts: 1, logStallTimeoutMinutes: 5, totalTimeoutMinutes: 10, successMarkers: "任务完成",
  });
  const cid = (await c.json()).id;
  await api("POST", "/api/dispatch/script", { scriptId: cid, mode: "manual" });
  assert(await waitNoRunning(60000), "通配轮换脚本运行结束");
  const cl = scriptHistoryLog(cid);
  assert(cl.includes("ROUND2-NEW") && !cl.includes("OLD-ROUND"), "通配格式跟踪到轮换后的新文件（忽略旧轮次内容）");
  await api("DELETE", "/api/scripts/" + cid);
  fs.rmSync(logRoot, { recursive: true, force: true });
}

async function testPluginConfig(page) {
  console.log("[用例] 插件配置二级页：布局 + 类型选择器 + generic 模板联动与样式");
  await page.click('nav a[href="#/plugins"]');
  await page.waitForSelector("h2");
  await page.waitForFunction(() => document.body.textContent.includes("通知推送"), null, { timeout: 5000 });
  const cfgBtn = await page.$('[data-action="plugin-config"]');
  assert(!!cfgBtn, "通知推送插件有「配置」按钮");
  await page.click('[data-action="plugin-config"]');
  await page.waitForFunction(() => document.body.textContent.includes("· 配置"), null, { timeout: 5000 });
  const body = await page.textContent("body");
  assert(body.includes("返回插件"), "插件配置页有返回箭头");
  assert(body.includes("Webhook 通知") && body.includes("SMTP 邮件通知"), "插件配置页含 Webhook/SMTP 折叠面板");
  assert(body.includes("配置信息") && body.includes("启用通知的脚本实例"), "插件配置页含配置信息（统计）");
  assert(body.includes("0 个"), "启用通知统计显示（脚本 0 / 队列 0）");

  const typeOptions = await page.$$eval("#st-whtype option", els => els.map(e => e.textContent));
  assert(typeOptions.length === 6 && typeOptions[0] === "Feishu" && typeOptions[5] === "Generic", "Webhook 类型选项首字母大写（Feishu…Generic）");
  const defaultType = await page.$eval("#st-whtype", el => el.value);
  assert(defaultType === "feishu", "Webhook 类型默认 feishu（value 小写）");
  assert(await page.$eval("#st-whtpl-box", el => el.hidden), "默认（feishu）时 generic 模板框隐藏");
  await page.selectOption("#st-whtype", "generic");
  assert(await page.$eval("#st-whtype", el => el.value) === "generic", "切换后 value 保持小写（generic）");
  assert(!(await page.$eval("#st-whtpl-box", el => el.hidden)), "切换 generic 后模板框显示");
  await page.selectOption("#st-whtype", "dingtalk");
  assert(await page.$eval("#st-whtpl-box", el => el.hidden), "切换 dingtalk 后模板框再次隐藏");

  await page.click('nav a[href="#/plugins"]');
  await page.waitForFunction(() => document.querySelector("h2") && document.querySelector("h2").textContent.includes("插件"), null, { timeout: 5000 });
  assert(true, "返回插件列表正常");
}

async function testNotifyPluginGating(page) {
  console.log("[用例] 通知复选框与插件状态绑定（禁用隐藏 / 启用恢复）");
  const gDir = makeScriptDir("gating");
  const created = await createScript({ name: "门禁样式脚本", rootPath: gDir.root, mainExe: gDir.main, configPath: gDir.cfg, logPath: gDir.log });
  assert(created.ok, "预创建样式断言用脚本");

  const disable = await api("POST", "/api/plugins/notify/disable");
  assert(disable.ok, "API 禁用通知推送插件");

  await page.click('nav a[href="#/scripts"]');
  await page.waitForFunction(() => document.querySelector("h2") && document.querySelector("h2").textContent.includes("脚本实例"), null, { timeout: 5000 });
  await page.waitForSelector(".script-card", { timeout: 5000 });
  assert(!(await page.$('[data-testid="script-card"] [data-testid="script-notify"]')), "插件禁用时脚本卡片隐藏通知徽章");
  await page.click('[data-testid="new-script"]');
  await page.waitForSelector(".new-script-chooser", { timeout: 5000 });
  await page.click('[data-action="open-script-type"][data-plugin=""]');
  await page.waitForSelector("#sm-name");
  assert(!(await page.isVisible("#sm-notify")), "插件禁用时脚本弹窗隐藏「发送运行状态通知」");
  await page.click('[data-action="close-modal"]');

  await page.click('nav a[href="#/queues"]');
  await page.waitForFunction(() => document.querySelector("h2") && document.querySelector("h2").textContent.includes("调度队列"), null, { timeout: 5000 });
  await page.click('[data-action="open-queue-modal"]');
  await page.waitForSelector("#qm-name");
  assert(!(await page.isVisible("#qm-notify")), "插件禁用时队列弹窗隐藏「队列级通知」");
  await page.click('[data-action="close-modal"]');

  const enable = await api("POST", "/api/plugins/notify/enable");
  assert(enable.ok, "API 重新启用通知推送插件");

  await page.click('nav a[href="#/scripts"]');
  await page.waitForFunction(() => document.querySelector("h2") && document.querySelector("h2").textContent.includes("脚本实例"), null, { timeout: 5000 });
  await page.waitForSelector(".script-card", { timeout: 5000 });
  const notifyCell2 = await page.$eval('[data-testid="script-card"] [data-testid="script-notify"]', el => el.textContent.trim());
  assert(notifyCell2 === "通知：关", "插件启用后脚本卡片通知徽章显示「通知：关」");
  await page.click('[data-testid="new-script"]');
  await page.waitForSelector(".new-script-chooser", { timeout: 5000 });
  await page.click('[data-action="open-script-type"][data-plugin=""]');
  await page.waitForSelector("#sm-name");
  assert(await page.isVisible("#sm-notify"), "插件启用后脚本弹窗恢复显示通知复选框");
  await page.click('[data-action="close-modal"]');

  await page.click('nav a[href="#/queues"]');
  await page.waitForFunction(() => document.querySelector("h2") && document.querySelector("h2").textContent.includes("调度队列"), null, { timeout: 5000 });
  await page.click('[data-action="open-queue-modal"]');
  await page.waitForSelector("#qm-name");
  assert(await page.isVisible("#qm-notify"), "插件启用后队列弹窗恢复显示通知复选框");
  await page.click('[data-action="close-modal"]');

  await api("DELETE", "/api/scripts/" + created.id);
}

async function testNextScheduleAndStats(page) {
  console.log("[用例] 下一调度队列显示/倒计时 + 通知统计");
  const exitBat = path.join(runtimeDir, "exit-ok.bat");
  fs.writeFileSync(exitBat, "@echo off\r\nexit /b 0\r\n");
  const created = await createScript({
    name: "统计脚本", rootPath: runtimeDir.replace(/\\/g, "\\\\"), mainExe: exitBat.replace(/\\/g, "\\\\"),
    configPath: runtimeDir.replace(/\\/g, "\\\\"),
    logPath: runtimeDir.replace(/\\/g, "\\\\"),
    notifyEnabled: true,
  });
  const sid = created.id;
  const qr = await api("POST", "/api/queues", {
    name: "统计队列", autoRunMode: "scheduled", completionAction: "none",
    timeSets: [{ id: "", enabled: true, days: [0, 1, 2, 3, 4, 5, 6], time: "23:59" }],
    tasks: [{ id: "", index: 0, scriptInstanceId: sid }], notifyEnabled: true,
  });
  const qid = (await qr.json()).id;

  await page.click('nav a[href="#/dashboard"]');
  await page.waitForFunction(() => { const el = document.querySelector("#next-q"); return el && /^\d{2}:\d{2}:\d{2}$/.test(el.textContent.trim()); }, null, { timeout: 10000 });
  const cd = await page.textContent("#next-q");
  assert(/^\d{2}:\d{2}:\d{2}$/.test(cd.trim()), "下一调度队列卡片上方显示倒计时（" + cd + "）");
  assert(await page.evaluate(() => { const el = document.querySelector('[data-testid="stat-next"] .lbl'); return el && el.textContent === "下一调度队列"; }), "倒计时下方标签仍为「下一调度队列」");
  const body = await page.textContent("body");
  assert(body.includes("1 个脚本实例") && body.includes("1 个调度队列"), "通知统计显示 1 个脚本实例 / 1 个调度队列");

  await api("DELETE", "/api/queues/" + qid);
  await api("DELETE", "/api/scripts/" + sid);
}

async function testLimitsApi() {
  console.log("[用例] 约束体系：API 默认值 + 数量上限");
  const limits = await (await fetch(baseUrl + "api/limits")).json();
  assert(limits.limits.maxScripts === 25 && limits.limits.maxUsersPerScript === 10, "limits API 默认值（脚本 25 / 用户 10）");
  assert(limits.limits.maxQueues === 10 && limits.limits.maxTimeSetsPerQueue === 10, "limits API 默认值（队列 10 / 定时 10）");
  assert(limits.limits.maxAttempts === 10 && limits.limits.maxTotalMinutes === 720, "limits API 默认值（尝试 10 / 总时长 720）");
  assert(limits.warnings.length === 0, "默认配置无警告");

  const ids = [];
  const xDir = makeScriptDir("limits");
  const limitBase = { rootPath: xDir.root, mainExe: xDir.main, configPath: xDir.cfg, logPath: xDir.log };
  for (let i = 0; i < 25; i++) {
    const r = await api("POST", "/api/scripts", { name: "约束脚本" + String(i).padStart(2, "0"), ...limitBase, maxAttempts: 1, logStallTimeoutMinutes: 5, totalTimeoutMinutes: 120 });
    ids.push((await r.json()).id);
  }
  const r26 = await api("POST", "/api/scripts", { name: "超限脚本", ...limitBase, maxAttempts: 1, logStallTimeoutMinutes: 5, totalTimeoutMinutes: 120 });
  assert(r26.status === 400, "第 26 个脚本被拒（400）");

  for (let i = 0; i < 10; i++) {
    await api("POST", "/api/scripts/" + ids[0] + "/users", { name: "用户" + i, enabled: true });
  }
  const r11 = await api("POST", "/api/scripts/" + ids[0] + "/users", { name: "用户11", enabled: true });
  assert(r11.status === 400, "第 11 个用户被拒（400）");

  const qids = [];
  for (let i = 0; i < 10; i++) {
    const r = await api("POST", "/api/queues", { name: "约束队列" + i, autoRunMode: "scheduled", completionAction: "none", timeSets: [{ id: "", enabled: true, days: [1], time: "23:59" }], tasks: [{ id: "", index: 0, scriptInstanceId: ids[1] }] });
    qids.push((await r.json()).id);
  }
  const q11 = await api("POST", "/api/queues", { name: "超限队列", autoRunMode: "scheduled", completionAction: "none", timeSets: [], tasks: [{ id: "", index: 0, scriptInstanceId: ids[1] }] });
  assert(q11.status === 400, "第 11 个队列被拒（400）");

  const t11 = await api("PUT", "/api/queues/" + qids[0], { name: "约束队列0", autoRunMode: "scheduled", completionAction: "none", timeSets: Array.from({ length: 11 }, (_, i) => ({ id: "", enabled: true, days: [1], time: "23:" + String(40 + i) })), tasks: [{ id: "", index: 0, scriptInstanceId: ids[1] }] });
  assert(t11.status === 400, "第 11 个定时被拒（400）");

  for (const qid of qids) await api("DELETE", "/api/queues/" + qid);
  for (const id of ids) await api("DELETE", "/api/scripts/" + id);
}

async function testLimitsFields() {
  console.log("[用例] 约束体系：名称字节 / 数值区间 / 任务总用户");
  const fDir = makeScriptDir("fields");
  const base = { rootPath: fDir.root, mainExe: fDir.main, configPath: fDir.cfg, logPath: fDir.log, maxAttempts: 1, logStallTimeoutMinutes: 5, totalTimeoutMinutes: 120 };
  const postScript = body => api("POST", "/api/scripts", body);

  const longName = await postScript({ ...base, name: "长".repeat(43) });
  assert(longName.status === 400, "脚本名 129 字节被拒（400）");
  const okName = await postScript({ ...base, name: "长".repeat(42) });
  assert(okName.ok, "脚本名 126 字节通过");
  const sid = (await okName.json()).id;

  assert((await postScript({ ...base, name: "attempts11", maxAttempts: 11 })).status === 400, "attempts=11 被拒");
  assert((await postScript({ ...base, name: "attempts0", maxAttempts: 0 })).status === 400, "attempts=0 被拒");
  assert((await postScript({ ...base, name: "stall61", logStallTimeoutMinutes: 61 })).status === 400, "无更新超时 61 分钟被拒");
  assert((await postScript({ ...base, name: "total4", totalTimeoutMinutes: 4 })).status === 400, "总时长 4 分钟被拒");
  assert((await postScript({ ...base, name: "total721", totalTimeoutMinutes: 721 })).status === 400, "总时长 721 分钟被拒");

  const qLong = await api("POST", "/api/queues", { name: "队".repeat(43), autoRunMode: "scheduled", completionAction: "none", timeSets: [], tasks: [{ id: "", index: 0, scriptInstanceId: sid }] });
  assert(qLong.status === 400, "队列名 129 字节被拒（400）");

  for (let i = 0; i < 10; i++) {
    await api("POST", "/api/scripts/" + sid + "/users", { name: "任务用户" + i, enabled: true });
  }
  const tasks5 = Array.from({ length: 5 }, (_, i) => ({ id: "", index: i, scriptInstanceId: sid }));
  const q5 = await api("POST", "/api/queues", { name: "任务50队列", autoRunMode: "scheduled", completionAction: "none", timeSets: [], tasks: tasks5 });
  assert(q5.ok, "任务启用用户总和 50 通过");
  const qid = (await q5.json()).id;
  const q6 = await api("POST", "/api/queues", { name: "任务60队列", autoRunMode: "scheduled", completionAction: "none", timeSets: [], tasks: [...tasks5, { id: "", index: 5, scriptInstanceId: sid }] });
  assert(q6.status === 400, "任务启用用户总和 60 被拒（400）");

  await api("DELETE", "/api/queues/" + qid);
  await api("DELETE", "/api/scripts/" + sid);
}

async function testPagination(page) {
  console.log("[用例] 分页：脚本列表前端分页 + 达上限禁用 + 历史 API 分页");
  const ids = [];
  const pgDir = makeScriptDir("pager");
  for (let i = 0; i < 25; i++) {
    const r = await api("POST", "/api/scripts", { name: "分页脚本" + String(i).padStart(2, "0"), rootPath: pgDir.root, mainExe: pgDir.main, configPath: pgDir.cfg, logPath: pgDir.log, maxAttempts: 1, logStallTimeoutMinutes: 5, totalTimeoutMinutes: 120 });
    ids.push((await r.json()).id);
  }
  await page.click('nav a[href="#/scripts"]');
  await page.waitForSelector("[data-testid='pager-scripts']", { timeout: 10000 });
  const rows1 = await page.$$eval("#view .script-card", els => els.length);
  assert(rows1 === 20, "脚本分页第一页 20 张卡片（实际 " + rows1 + "）");
  const info1 = await page.textContent("[data-testid='pager-scripts'] .pager-info");
  assert(info1.includes("共 25 条"), "分页条显示共 25 条");
  await page.click("[data-testid='pager-scripts'] [data-action='pager-next']");
  await page.waitForFunction(() => document.querySelectorAll("#view .script-card").length === 5, null, { timeout: 5000 });
  assert(true, "翻页后第二页 5 张卡片");
  const newBtn = await page.$eval("[data-testid='new-script']", el => el.disabled);
  assert(newBtn === true, "脚本达上限新建按钮禁用");

  const hp = await (await fetch(baseUrl + "api/history?days=7&offset=0&limit=5")).json();
  assert(typeof hp.total === "number" && Array.isArray(hp.records) && hp.records.length <= 5, "历史 API 服务端分页返回 {total, records}");

  for (const id of ids) await api("DELETE", "/api/scripts/" + id);
}

async function testLimitsWarnings(page) {
  console.log("[用例] 约束警告：非法配置告警 + 前端卡片（知道了/不再提醒）");
  const logFile = path.join(runtimeDir, "logs", "nexus-pipeline-" + localDate().replace(/-/g, "") + ".log");
  const readLog = () => fs.existsSync(logFile) ? fs.readFileSync(logFile, "utf8").replace(/^\uFEFF/, "") : "";
  const limitsFile = path.join(runtimeDir, "config", "limits.json");

  fs.mkdirSync(path.dirname(limitsFile), { recursive: true });
  fs.writeFileSync(limitsFile, '{"MaxScripts": 30}');
  await restartService();
  assert(readLog().includes("[警告] 约束配置 [MaxScripts"), "启动日志含约束警告");
  const l = await (await fetch(baseUrl + "api/limits")).json();
  assert(l.limits.maxScripts === 30, "警告级配置已生效（maxScripts=30）");
  assert(l.warnings.length === 1, "limits API 返回 1 条警告");

  await page.goto(baseUrl, { waitUntil: "domcontentloaded" });
  await page.waitForSelector("#limits-warning", { timeout: 10000 });
  const cardText = await page.textContent("#limits-warning");
  assert(cardText.includes("知道了") && cardText.includes("不再提醒"), "警告卡片含「知道了」「不再提醒」按钮");

  await page.click('[data-action="limits-dismiss-once"]');
  await page.waitForSelector("#limits-warning", { state: "detached", timeout: 5000 });
  assert(true, "点击「知道了」卡片关闭");
  await page.goto(baseUrl, { waitUntil: "domcontentloaded" });
  await page.waitForSelector("#limits-warning", { timeout: 10000 });
  assert(true, "重载后警告卡片再次出现");

  await page.click('[data-action="limits-dismiss-forever"]');
  await page.waitForSelector("#limits-warning", { state: "detached", timeout: 5000 });
  await page.goto(baseUrl, { waitUntil: "domcontentloaded" });
  await page.waitForTimeout(800);
  assert(!(await page.$("#limits-warning")), "点击「不再提醒」后重载不再出现");
  assert(readLog().includes("[警告] 约束配置 [MaxScripts"), "日志仍含约束警告（不受不再提醒影响）");

  fs.writeFileSync(limitsFile, '{"MaxScripts": 40}');
  await restartService();
  await page.goto(baseUrl, { waitUntil: "domcontentloaded" });
  await page.waitForSelector("#limits-warning", { timeout: 10000 });
  assert(true, "警告内容变化后重新提醒");
  await page.click('[data-action="limits-dismiss-forever"]');

  fs.rmSync(limitsFile, { force: true });
  await restartService();
  const l2 = await (await fetch(baseUrl + "api/limits")).json();
  assert(l2.warnings.length === 0 && l2.limits.maxScripts === 25, "恢复默认配置后无警告");
}

async function testLimitsFatal() {
  console.log("[用例] 约束 FATAL：致命配置拒绝启动");
  const logFile = path.join(runtimeDir, "logs", "nexus-pipeline-" + localDate().replace(/-/g, "") + ".log");
  const readLog = () => fs.existsSync(logFile) ? fs.readFileSync(logFile, "utf8").replace(/^\uFEFF/, "") : "";
  const limitsFile = path.join(runtimeDir, "config", "limits.json");

  fs.mkdirSync(path.dirname(limitsFile), { recursive: true });
  fs.writeFileSync(limitsFile, '{"MaxScripts": 60}');
  await stopService();
  await sleep(400);
  startService();
  let started = true;
  try { await waitForService(8000); } catch { started = false; }
  assert(!started, "超警告区间（MaxScripts=60）服务拒绝启动");
  await sleep(500);
  assert(readLog().includes("[FATAL] 约束配置 [MaxScripts"), "启动日志含 FATAL 约束记录");

  fs.writeFileSync(limitsFile, '{"MinAttempts": 8, "MaxAttempts": 3}');
  await stopService();
  await sleep(400);
  startService();
  started = true;
  try { await waitForService(8000); } catch { started = false; }
  assert(!started, "Min>Max 区间矛盾配置服务拒绝启动");
  await sleep(500);
  assert(readLog().includes("[FATAL]") && readLog().includes("区间矛盾"), "日志含区间矛盾 FATAL");

  fs.rmSync(limitsFile, { force: true });
  await restartService();
  assert(true, "恢复默认配置后服务正常启动");
}

/* ---------------- 主流程 ---------------- */

if (!isElevated()) {
  console.error("[错误] 当前终端无管理员权限：测试实例为提权版（requireAdministrator），必须以管理员身份运行。");
  console.error("       请运行 uitest\\run-uitest.cmd（非管理员时自动提权重启），或以管理员身份打开终端后运行 node uitest\\test.mjs。");
  process.exit(1);
}

async function main() {
  await setupRuntime();
  startService();
  try {
    await waitForService();
    const browser = await chromium.launch({ channel: "msedge", headless: true });
    try {
      const page = await browser.newPage();
      page.on("pageerror", error => console.error("[页面错误] " + error.message));
      const tests = [
        testDashboard, testResponsiveSmoke, testResponsiveShell, testNavigation, testScriptCrud,
        testUserManagement, testQueueMultiUser, testGateRelease, testBatchGameLaunch, testGameProcessConfirm, testForceCloseGating,
        testScriptEditPreservesUsers, testExeOpenGuard, testPathQuoteNormalize, testPathValidation,
        testV020Features, testSpecializedScript, testLaunchTargetArgs, testMarch7thPlugin, testZenlessPlugin, testPluginConfig, testNotifyPluginGating, testNextScheduleAndStats,
        testLogPattern,
        testQueueCrud, testDispatchAndHistory, testLogScroll, testHistoryFiles,
        testAudit, testLogLevel,
        testLimitsApi, testLimitsFields, testPagination, testLimitsWarnings, testLimitsFatal,
      ];
      for (const test of tests) {
        if (CI && CI_SKIP.has(test.name)) {
          console.log("[跳过] " + test.name + "（--ci 模式）");
          continue;
        }
        await test(page);
      }
    } finally {
      await browser.close();
    }
  } finally {
    await stopService();
  }
  console.log("");
  const expected = CI ? CI_EXPECTED : EXPECTED;
  console.log(`结果：通过 ${passed} 项，失败 ${failed} 项${CI ? "（--ci 核心回归集）" : ""}`);
  if (passed + failed !== expected) {
    failed += 1;
    console.log(`[FAIL] 断言总数与预期不一致：实际 ${passed + failed - 1}，预期 ${expected}（请同步 AGENTS.md 数字）`);
  }
  if (failed > 0) process.exit(1);
}

main().catch(err => {
  console.error("[错误] " + err.message);
  stopService();
  process.exit(1);
});
