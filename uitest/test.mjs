import { spawn } from "node:child_process";
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

async function waitForService(timeoutMs = 30000) {
  const deadline = Date.now() + timeoutMs;
  while (Date.now() < deadline) {
    try {
      const res = await fetch(baseUrl + "api/status");
      if (res.ok) return;
    } catch { /* retry */ }
    await new Promise(r => setTimeout(r, 500));
  }
  throw new Error("服务未在 " + timeoutMs + "ms 内启动");
}

async function setupRuntime() {
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
    await new Promise(r => setTimeout(r, 500));
    child = null;
  }
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
  assert(body.includes("0.2.1"), "版本显示 0.2.1（x.x.x 不带 v）");
  assert(body.includes("下一调度队列"), "首行含下一调度队列卡片");
  const nums = await page.$$eval(".stat .num", els => els.map(e => e.textContent.trim()));
  assert(nums.includes("无"), "无定时队列时下一调度显示「无」");
  const pcards = await page.$$eval(".plugin-card", els => els.map(e => e.textContent.trim()));
  assert(pcards.length >= 1, "仪表盘插件区为 1/4 小卡片布局（≥1 张卡片）");
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

  await page.setViewportSize({ width: 1280, height: 900 });
  await page.goto(baseUrl + "#/dispatch", { waitUntil: "domcontentloaded" });
  await page.waitForSelector("#dc-script");
  const dispatchButtons = await page.evaluate(() => Array.from(document.querySelectorAll(".control-action button")).map(button => ({ width: button.getBoundingClientRect().width, card: button.closest(".card").getBoundingClientRect().width })));
  assert(dispatchButtons.length === 2 && dispatchButtons.every(item => item.width / item.card <= 0.2), "调度中心执行按钮保持约 1/8 宽度");
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
  console.log("[用例] 脚本实例：空状态 / 按钮位置 / 必填校验 / 新建 / 编辑 / 删除");
  await page.click('nav a[href="#/scripts"]');
  await page.waitForSelector("h2");
  assert((await page.textContent("body")).includes("暂无脚本实例"), "无脚本时显示空状态提示而非空卡片");
  const newBtn = await page.$(".page-head button:has-text('新建脚本实例')");
  assert(!!newBtn, "新建按钮位于右上角（page-head 内）");
  if (newBtn) {
    const box = await newBtn.boundingBox();
    const vw = await page.evaluate(() => window.innerWidth);
    assert(box.x > vw / 2, "新建按钮位于视口右半侧（与卡片右侧对齐）");
  }
  await page.click("text=新建脚本实例");
  await page.waitForSelector(".modal-mask");
  assert((await page.$$(".req")).length >= 7, "必填项红色 * 标记存在（≥7 个）");

  await page.click(".modal button:has-text('保存')");
  await page.waitForTimeout(400);
  assert(await page.$(".modal-mask"), "必填未填时无法保存（弹窗保留）");

  await page.fill("#sm-name", "测试脚本A");
  await page.fill("#sm-root", "C:\\scripts\\a");
  await page.fill("#sm-exe", "C:\\scripts\\a\\run.bat");
  await page.fill("#sm-config", "C:\\scripts\\a\\config");
  await page.fill("#sm-log", "C:\\scripts\\a\\logs");
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
  await page.waitForTimeout(800);
  const body = await page.textContent("body");
  assert(!body.includes("测试脚本A-改"), "删除后列表不再显示该脚本");
}

async function testQueueCrud(page) {
  console.log("[用例] 调度队列：新建（定时+任务）/ 编辑 / 删除");
  const created = await fetch(baseUrl + "api/scripts", {
    method: "POST",
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify({
      name: "队列用脚本", rootPath: "C:\\scripts\\q", mainExe: "C:\\scripts\\q\\run.bat",
      configPath: "C:\\scripts\\q\\config", logPath: "C:\\scripts\\q\\logs",
      maxAttempts: 3, logStallTimeoutMinutes: 5, totalTimeoutMinutes: 120,
    }),
  });
  assert(created.ok, "通过 API 预创建队列用脚本");
  await page.click('nav a[href="#/queues"]');
  await page.waitForSelector("h2");
  await page.click("text=新建调度队列");
  await page.waitForSelector(".modal-mask");
  await page.fill("#qm-name", "测试队列A");

  const dayState = await page.$eval(".timeset-days", el => {
    const frame = el.querySelector(".days-frame");
    const inputs = Array.from(el.querySelectorAll("[data-ts-days]"));
    return { hasFrame: !!frame, count: inputs.length, checked: inputs.filter(input => input.checked).length, bordered: frame ? getComputedStyle(frame).borderTopWidth !== "0px" : false };
  });
  assert(dayState.hasFrame && dayState.count === 7, "执行周期为整体带框复选框组（7 项）");
  assert(dayState.checked === 5, "执行周期默认选中工作日");
  assert(dayState.bordered, "执行周期复选框组带整体边框");
  const heightMatch = await page.$eval(".timeset-layout", el => {
    const frame = el.querySelector(".days-frame");
    const input = el.querySelector(".timeset-time input");
    if (!frame || !input) return false;
    return Math.abs(frame.getBoundingClientRect().height - input.getBoundingClientRect().height) <= 4;
  });
  assert(heightMatch, "执行周期容器与执行时间元件高度一致");
  const timeLayout = await page.$eval(".timeset-layout", el => {
    const time = el.querySelector(".timeset-time").getBoundingClientRect();
    return { ratio: time.width / el.getBoundingClientRect().width };
  });
  assert(timeLayout.ratio <= 0.35, "执行时间控件约占定时卡片四分之一宽度");
  const actionLayout = await page.$eval(".timeset-actions", el => {
    const children = Array.from(el.children).map(child => child.getBoundingClientRect());
    const box = el.getBoundingClientRect();
    const tops = children.map(child => child.top);
    return { sameRow: Math.max(...tops) - Math.min(...tops) <= 8, rightAligned: children.at(-1).right >= box.right - 2 };
  });
  assert(actionLayout.sameRow && actionLayout.rightAligned, "启用与删除定时按钮在右下并排");

  await page.click("text=+ 添加任务");
  await page.selectOption('[data-task-idx="0"]', { label: "队列用脚本" });
  await page.click(".modal button:has-text('保存')");
  await page.waitForSelector(".modal-mask", { state: "detached", timeout: 5000 });
  await page.waitForFunction(() => document.body.textContent.includes("测试队列A"), null, { timeout: 5000 });
  assert(true, "新建后卡片显示队列名称");

  await page.click('[data-action="edit-queue"]');
  await page.waitForSelector("#qm-name");
  await page.fill("#qm-name", "测试队列A-改");
  await page.click(".modal button:has-text('保存')");
  await page.waitForSelector(".modal-mask", { state: "detached", timeout: 5000 });
  await page.waitForFunction(() => document.body.textContent.includes("测试队列A-改"), null, { timeout: 5000 });
  assert(true, "编辑后队列名称已更新");

  page.once("dialog", d => d.accept());
  await page.click('[data-action="delete-queue"]');
  await page.waitForTimeout(800);
  const body = await page.textContent("body");
  assert(!body.includes("测试队列A-改"), "删除后卡片消失");
}

async function testDispatchAndHistory(page) {
  console.log("[用例] 调度中心执行 + 历史记录详情");
  await page.click('nav a[href="#/scripts"]');
  await page.waitForSelector("h2");
  await page.click("text=新建脚本实例");
  await page.waitForSelector("#sm-name");
  await page.fill("#sm-name", "跑批脚本");
  await page.fill("#sm-root", "C:\\scripts\\b");
  await page.fill("#sm-exe", "C:\\scripts\\b\\nonexist.exe");
  await page.fill("#sm-config", "C:\\scripts\\b\\config");
  await page.fill("#sm-log", "C:\\scripts\\b\\logs");
  await page.click(".modal button:has-text('保存')");
  await page.waitForSelector(".modal-mask", { state: "detached", timeout: 5000 });

  await page.click('nav a[href="#/dispatch"]');
  await page.waitForSelector("#dc-script");
  await page.selectOption("#dc-script", { label: "跑批脚本" });
  await page.click("button:has-text('执行')");
  await page.waitForTimeout(1500);

  await page.click('nav a[href="#/history"]');
  await page.waitForSelector("h2");
  await page.waitForTimeout(500);
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
  const created = await fetch(baseUrl + "api/scripts", {
    method: "POST",
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify({
      name: "日志脚本", rootPath: runtimeDir, mainExe: batPath,
      configPath: runtimeDir, logPath,
      maxAttempts: 2, logStallTimeoutMinutes: 5, totalTimeoutMinutes: 10,
    }),
  });
  assert(created.ok, "创建日志测试脚本");

  await page.click('nav a[href="#/dispatch"]');
  await page.waitForSelector("#dc-script");
  await page.selectOption("#dc-script", { label: "日志脚本" });
  await page.click("button:has-text('执行')");
  await page.waitForSelector(".run-log", { timeout: 10000 });
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
  const dayDir = path.join(runtimeDir, "history", localDate());
  await new Promise(r => setTimeout(r, 800));
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

  const consoleFile = path.join(runtimeDir, "logs", localDate() + ".log");
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
  const sleep = ms => new Promise(r => setTimeout(r, ms));

  const created = await fetch(baseUrl + "api/scripts", {
    method: "POST",
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify({
      name: "审计脚本", rootPath: "C:\\audit", mainExe: "C:\\audit\\run.bat",
      configPath: "C:\\audit\\config", logPath: "C:\\audit\\logs",
      maxAttempts: 3, logStallTimeoutMinutes: 5, totalTimeoutMinutes: 120,
    }),
  });
  assert(created.ok, "API 创建脚本");
  await sleep(400);
  assert(readLog().includes("[审计] web | 添加脚本实例（审计脚本"), "创建脚本产生审计行");

  const list = await (await fetch(baseUrl + "api/scripts")).json();
  const target = list.find(x => x.name === "审计脚本");
  assert(!!target, "列表可查询到审计脚本");
  const updated = await fetch(baseUrl + "api/scripts/" + target.id, {
    method: "PUT",
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify({
      id: target.id, name: "审计脚本改", rootPath: "C:\\audit", mainExe: "C:\\audit\\run.bat",
      configPath: "C:\\audit\\config", logPath: "C:\\audit\\logs",
      maxAttempts: 3, logStallTimeoutMinutes: 5, totalTimeoutMinutes: 120,
    }),
  });
  assert(updated.ok, "API 修改脚本");
  await sleep(400);
  assert(readLog().includes("[审计] web | 修改脚本实例（审计脚本改"), "修改脚本产生审计行");

  await page.click('nav a[href="#/history"]');
  await page.waitForFunction(() => document.querySelector("h2") && document.querySelector("h2").textContent.includes("历史记录"));
  await sleep(600);
  assert(readLog().includes("[审计] web | 查询历史记录"), "打开历史页产生查询审计行");

  const count1 = (readLog().match(/\[审计\]/g) || []).length;
  await page.waitForTimeout(5500);
  const count2 = (readLog().match(/\[审计\]/g) || []).length;
  assert(count1 === count2, "历史页停留 5.5 秒无新增审计行（status 轮询已豁免）");

  const del = await fetch(baseUrl + "api/scripts/" + target.id, { method: "DELETE" });
  assert(del.ok, "API 删除脚本");
  await sleep(400);
  assert(readLog().includes("[审计] web | 删除脚本实例（审计脚本改"), "删除脚本产生审计行");
}

async function testLogLevel(page) {
  console.log("[用例] 日志级别：设置 UI / 落盘 / 阈值过滤 / DEBUG 请求记录");
  const logFile = path.join(runtimeDir, "logs", "nexus-pipeline-" + localDate().replace(/-/g, "") + ".log");
  const readLog = () => fs.existsSync(logFile) ? fs.readFileSync(logFile, "utf8").replace(/^\uFEFF/, "") : "";
  const sleep = ms => new Promise(r => setTimeout(r, ms));
  const hdr = { "Content-Type": "application/json" };

  await page.click('nav a[href="#/settings"]');
  await page.waitForSelector("#st-loglevel");
  const defaultLevel = await page.$eval("#st-loglevel", el => el.value);
  assert(defaultLevel === "info", "设置页含「日志级别」下拉且默认 info");

  let put = await fetch(baseUrl + "api/settings", { method: "PUT", headers: hdr, body: JSON.stringify({ logLevel: "warn" }) });
  assert(put.ok, "PUT logLevel=warn 成功");
  const got = await (await fetch(baseUrl + "api/settings")).json();
  assert(got.settings.logLevel === "warn", "GET 返回 logLevel=warn");
  const cfg = JSON.parse(fs.readFileSync(path.join(runtimeDir, "config", "settings.json"), "utf8").replace(/^\uFEFF/, ""));
  assert(cfg.LogLevel === "warn", "settings.json 已落盘 LogLevel=warn");

  const created = await fetch(baseUrl + "api/scripts", {
    method: "POST", headers: hdr,
    body: JSON.stringify({
      name: "日志级别脚本", rootPath: "C:\\lg", mainExe: "C:\\lg\\run.bat",
      configPath: "C:\\lg\\cfg", logPath: "C:\\lg\\log",
      maxAttempts: 1, logStallTimeoutMinutes: 5, totalTimeoutMinutes: 120,
    }),
  });
  assert(created.ok, "创建日志级别测试脚本（触发 INFO 审计）");
  const sid = (await created.json()).id;
  await sleep(400);
  assert(!readLog().includes("[审计] web | 添加脚本实例（日志级别脚本"), "warn 阈值下 INFO 审计行被过滤");

  put = await fetch(baseUrl + "api/settings", { method: "PUT", headers: hdr, body: JSON.stringify({ logLevel: "debug" }) });
  assert(put.ok, "PUT logLevel=debug 成功");
  await fetch(baseUrl + "api/scripts");
  await sleep(400);
  assert(readLog().includes("[DEBUG] [Web] GET /api/scripts"), "debug 级别记录 Web API 请求");
  await fetch(baseUrl + "api/status");
  await sleep(400);
  assert(!readLog().includes("[Web] GET /api/status"), "GET /api/status 轮询豁免（不记录）");

  put = await fetch(baseUrl + "api/settings", { method: "PUT", headers: hdr, body: JSON.stringify({ logLevel: "info" }) });
  assert(put.ok, "恢复 logLevel=info 成功");
  const del = await fetch(baseUrl + "api/scripts/" + sid, { method: "DELETE" });
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

  const create = await fetch(baseUrl + "api/scripts", {
    method: "POST",
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify({
      name: "用户测试脚本", rootPath: runtimeDir.replace(/\\/g, "\\\\"), mainExe: exitBat.replace(/\\/g, "\\\\"),
      configPath: cfgDir.replace(/\\/g, "\\\\"), logPath: logDir.replace(/\\/g, "\\\\"),
      maxAttempts: 1, logStallTimeoutMinutes: 5, totalTimeoutMinutes: 120,
    }),
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
  await page.waitForTimeout(400);
  body = await page.textContent("body");
  assert(body.includes("用户名重复"), "重复用户名被拒绝（弹窗保留）");
  await page.click(".modal button:has-text('取消')");

  await page.click('[data-action="edit-user"][data-name="甲"]');
  await page.waitForSelector("#um-name");
  await page.fill("#um-name", "甲改");
  await page.click(".modal button:has-text('保存')");
  await page.waitForFunction(() => document.body.textContent.includes("甲改"), null, { timeout: 5000 });
  assert(fs.existsSync(path.join(dataDir, "甲改", "config", "configA.txt")), "改名后用户数据目录已迁移");
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
  await page.waitForTimeout(300);
  assert(fs.readFileSync(path.join(userDir, "config", "configA.txt"), "utf8") === "NEWSETUP", "完成后新配置已保存（store）");
  assert(fs.readFileSync(cfgFile, "utf8") === "ORIGINAL", "完成后原配置已还原到配置路径");
  assert(!fs.existsSync(path.join(userDir, "cache", "configA.txt")), "完成后缓存区已清空");

  await page.click(`[data-action="edit-user-config"][data-name="${user}"]`);
  await page.waitForSelector(".modal", { timeout: 5000 });
  fs.writeFileSync(cfgFile, "HALF");
  await page.click('[data-action="edit-config-cancel"]');
  await page.waitForFunction(() => !document.querySelector(".modal"), null, { timeout: 5000 });
  await page.waitForTimeout(300);
  assert(fs.readFileSync(cfgFile, "utf8") === "ORIGINAL", "取消后原配置已还原");
  assert(fs.readFileSync(path.join(userDir, "config", "configA.txt"), "utf8") === "NEWSETUP", "取消不改变已保存的用户配置");

  await page.click('nav a[href="#/dispatch"]');
  await page.waitForSelector("#dc-script");
  await page.selectOption("#dc-script", { label: "用户测试脚本" });
  await page.waitForTimeout(300);
  const userOpts = await page.$$eval("#dc-user option", opts => opts.map(o => o.value));
  assert(userOpts.includes(user) && !userOpts.includes("甲"), "调度中心用户下拉仅含启用用户");
  await page.selectOption("#dc-user", { label: user });
  await page.click("button:has-text('执行')");
  for (let i = 0; i < 30; i++) {
    const st = await (await fetch(baseUrl + "api/status")).json();
    if ((st.running || []).length > 0) break;
    await new Promise(r => setTimeout(r, 200));
  }
  let runFinished = false;
  for (let i = 0; i < 200; i++) {
    const st = await (await fetch(baseUrl + "api/status")).json();
    if ((st.running || []).length === 0) { runFinished = true; break; }
    await new Promise(r => setTimeout(r, 300));
  }
  assert(runFinished, "运行任务已结束（含配置还原）");
  await page.waitForTimeout(400);
  await page.waitForTimeout(400);
  const afterRun = fs.readFileSync(cfgFile, "utf8");
  assert(afterRun === "ORIGINAL", "运行结束后原配置已还原（实际：" + afterRun + "）");
  assert(fs.readFileSync(path.join(userDir, "config", "configA.txt"), "utf8") === "NEWSETUP", "运行结束后用户配置保留");

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
  await page.waitForFunction(() => !document.body.textContent.includes("用户测试脚本"), null, { timeout: 5000 });
  assert(!fs.existsSync(dataDir), "删除脚本后 data 目录已清理");
  const queues = await (await fetch(baseUrl + "api/queues")).json();
  for (const q of queues) {
    if (q.name === "用户队列测试") await fetch(baseUrl + "api/queues/" + q.id, { method: "DELETE" });
  }
}

async function testQueueMultiUser(page) {
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

  const create = await fetch(baseUrl + "api/scripts", {
    method: "POST",
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify({
      name: "多用户脚本", rootPath: runtimeDir.replace(/\\/g, "\\\\"), mainExe: exitBat.replace(/\\/g, "\\\\"),
      configPath: cfgDir.replace(/\\/g, "\\\\"), logPath: muLog.replace(/\\/g, "\\\\"),
      maxAttempts: 1, logStallTimeoutMinutes: 5, totalTimeoutMinutes: 120,
    }),
  });
  const sid = (await create.json()).id;
  const hdr = { "Content-Type": "application/json" };

  await fetch(baseUrl + "api/scripts/" + sid + "/users", { method: "POST", headers: hdr, body: JSON.stringify({ name: "甲", enabled: true }) });
  await fetch(baseUrl + "api/scripts/" + sid + "/users", { method: "POST", headers: hdr, body: JSON.stringify({ name: "乙", enabled: true }) });

  const editCfg = (user, action) => fetch(baseUrl + `api/scripts/${sid}/users/${encodeURIComponent(user)}/edit-config`, { method: "POST", headers: hdr, body: JSON.stringify({ action }) });

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

  const qr = await fetch(baseUrl + "api/queues", {
    method: "POST", headers: hdr,
    body: JSON.stringify({
      name: "多用户队列", autoRunMode: "scheduled", completionAction: "none",
      timeSets: [{ id: "", enabled: true, days: [1], time: "08:00" }],
      tasks: [{ id: "", index: 0, scriptInstanceId: sid }], notifyEnabled: false,
    }),
  });
  const qid = (await qr.json()).id;

  const dr = await fetch(baseUrl + "api/dispatch/queue", { method: "POST", headers: hdr, body: JSON.stringify({ queueId: qid, mode: "manual" }) });
  assert(dr.ok, "队列已开始执行");
  let queueDone = false;
  for (let i = 0; i < 200; i++) {
    const st = await (await fetch(baseUrl + "api/status")).json();
    if ((st.running || []).length === 0) { queueDone = true; break; }
    await new Promise(res => setTimeout(res, 300));
  }
  assert(queueDone, "队列运行已结束");

  const hist = await (await fetch(baseUrl + "api/history?days=7")).json();
  const recent = hist.filter(h => h.queueId === qid);
  assert(recent.length === 2, "队列多用户依次运行产生 2 条记录（实际 " + recent.length + "）");
  const names = recent.map(h => h.userName);
  assert(names.includes("甲") && names.includes("乙"), "两条记录分别属于启用用户（甲、乙）");
  assert(fs.readFileSync(cfgFile, "utf8") === "ORIGINAL", "队列运行结束后配置路径已还原");
  assert(fs.readFileSync(path.join(runtimeDir, "data", sid, "甲", "config", "configA.txt"), "utf8") === "NEWA", "甲用户配置保留");
  assert(fs.readFileSync(path.join(runtimeDir, "data", sid, "乙", "config", "configA.txt"), "utf8") === "NEWB", "乙用户配置保留");

  await fetch(baseUrl + "api/queues/" + qid, { method: "DELETE" });
  await fetch(baseUrl + "api/scripts/" + sid, { method: "DELETE" });
  assert(!fs.existsSync(path.join(runtimeDir, "data", sid)), "清理后数据目录已删除");
}

async function testGateRelease(page) {
  console.log("[用例] 门禁释放：运行中禁止编辑配置，结束后可正常进入");
  const gateCfg = path.join(runtimeDir, "gate-cfg");
  const gateLog = path.join(runtimeDir, "gate-log");
  fs.rmSync(gateCfg, { recursive: true, force: true });
  fs.rmSync(gateLog, { recursive: true, force: true });
  fs.mkdirSync(gateCfg, { recursive: true });
  fs.mkdirSync(gateLog, { recursive: true });
  const ps = "C:\\Windows\\System32\\WindowsPowerShell\\v1.0\\powershell.exe";
  const hdr = { "Content-Type": "application/json" };
  const create = await fetch(baseUrl + "api/scripts", {
    method: "POST",
    headers: hdr,
    body: JSON.stringify({
      name: "门禁测试脚本", rootPath: runtimeDir.replace(/\\/g, "\\\\"), mainExe: ps.replace(/\\/g, "\\\\"),
      args: "-Command Start-Sleep 8", configPath: gateCfg.replace(/\\/g, "\\\\"), logPath: gateLog.replace(/\\/g, "\\\\"),
      maxAttempts: 1, logStallTimeoutMinutes: 5, totalTimeoutMinutes: 120,
    }),
  });
  const sid = (await create.json()).id;
  await fetch(baseUrl + "api/scripts/" + sid + "/users", { method: "POST", headers: hdr, body: JSON.stringify({ name: "甲", enabled: true }) });

  await fetch(baseUrl + "api/dispatch/script", { method: "POST", headers: hdr, body: JSON.stringify({ scriptId: sid, mode: "manual", userName: "甲" }) });
  let running = false;
  for (let i = 0; i < 50; i++) {
    const st = await (await fetch(baseUrl + "api/status")).json();
    if ((st.running || []).length > 0) { running = true; break; }
    await new Promise(res => setTimeout(res, 200));
  }
  assert(running, "脚本已开始运行");
  const during = await fetch(baseUrl + `api/scripts/${sid}/users/${encodeURIComponent("甲")}/edit-config`, { method: "POST", headers: hdr, body: JSON.stringify({ action: "start" }) });
  assert(during.status === 409, "运行中编辑配置被拒绝（409，门禁占用）");
  let ended = false;
  for (let i = 0; i < 200; i++) {
    const st = await (await fetch(baseUrl + "api/status")).json();
    if ((st.running || []).length === 0) { ended = true; break; }
    await new Promise(res => setTimeout(res, 300));
  }
  assert(ended, "运行已结束");
  const after = await fetch(baseUrl + `api/scripts/${sid}/users/${encodeURIComponent("甲")}/edit-config`, { method: "POST", headers: hdr, body: JSON.stringify({ action: "start" }) });
  assert(after.ok, "运行结束后可正常开始编辑配置（门禁已释放，可继续编辑）");
  const cancel = await fetch(baseUrl + `api/scripts/${sid}/users/${encodeURIComponent("甲")}/edit-config`, { method: "POST", headers: hdr, body: JSON.stringify({ action: "cancel" }) });
  assert(cancel.ok, "取消编辑配置正常（会话关闭）");
  await fetch(baseUrl + "api/scripts/" + sid, { method: "DELETE" });
  assert(!fs.existsSync(path.join(runtimeDir, "data", sid)), "门禁测试脚本数据已清理");
}

async function testBatchGameLaunch(page) {
  console.log("[用例] 批处理游戏启动：有效 stdio + 正常结束");
  const hdr = { "Content-Type": "application/json" };
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

  const create = await fetch(baseUrl + "api/scripts", {
    method: "POST", headers: hdr,
    body: JSON.stringify({
      name: "批处理游戏脚本", rootPath: runtimeDir, mainExe: mainBat,
      configPath: "", logPath: "", launchGame: true, gameExe: gameBat,
      gameArgs: "", gameWaitSeconds: 0, forceCloseGame: false,
      maxAttempts: 1, logStallTimeoutMinutes: 5, totalTimeoutMinutes: 10,
    }),
  });
  const script = await create.json();
  assert(create.ok, "创建批处理游戏测试脚本");

  const dispatch = await fetch(baseUrl + "api/dispatch/script", {
    method: "POST", headers: hdr,
    body: JSON.stringify({ scriptId: script.id, mode: "manual" }),
  });
  assert(dispatch.ok, "批处理游戏脚本已开始运行");

  let ended = false;
  let gameStarted = false;
  for (let i = 0; i < 100; i++) {
    const status = await (await fetch(baseUrl + "api/status")).json();
    gameStarted = fs.existsSync(marker);
    if ((status.running || []).length === 0 && gameStarted) {
      ended = true;
      break;
    }
    await new Promise(res => setTimeout(res, 200));
  }
  assert(ended && gameStarted, "批处理游戏已启动且主脚本正常结束");
  await fetch(baseUrl + "api/scripts/" + script.id, { method: "DELETE" });
}

async function testForceCloseIndependent(page) {
  console.log("[用例] 强制关闭游戏独立于启动游戏（不启动游戏也执行关闭，任务正常结束）");
  const hdr = { "Content-Type": "application/json" };
  const exitBat = path.join(runtimeDir, "exit-ok.bat");
  fs.writeFileSync(exitBat, "@echo off\r\nexit /b 0\r\n");
  const cfg = path.join(runtimeDir, "fc-cfg");
  const log = path.join(runtimeDir, "fc-log");
  const logFile = path.join(log, "run.log");
  fs.rmSync(cfg, { recursive: true, force: true });
  fs.rmSync(log, { recursive: true, force: true });
  fs.mkdirSync(cfg, { recursive: true });
  fs.mkdirSync(log, { recursive: true });
  const runBat = path.join(runtimeDir, "fc-run.bat");
  fs.writeFileSync(runBat, "@echo off\r\necho done >> " + logFile + "\r\nexit /b 0\r\n");
  const create = await fetch(baseUrl + "api/scripts", {
    method: "POST", headers: hdr,
    body: JSON.stringify({
      name: "独立关闭脚本", rootPath: runtimeDir.replace(/\\/g, "\\\\"), mainExe: runBat.replace(/\\/g, "\\\\"),
      configPath: cfg.replace(/\\/g, "\\\\"), logPath: log.replace(/\\/g, "\\\\"),
      launchGame: false, gameExe: "C:\\nonexist\\game.exe", forceCloseGame: true,
      maxAttempts: 1, logStallTimeoutMinutes: 5, totalTimeoutMinutes: 120,
    }),
  });
  const sid = (await create.json()).id;
  const list = await (await fetch(baseUrl + "api/scripts")).json();
  const got = list.find(s => s.id === sid);
  assert(got && got.launchGame === false && got.forceCloseGame === true, "保存后字段独立（launchGame=false / forceCloseGame=true）");
  await fetch(baseUrl + "api/dispatch/script", { method: "POST", headers: hdr, body: JSON.stringify({ scriptId: sid, mode: "manual" }) });
  let ended = false;
  for (let i = 0; i < 200; i++) {
    const st = await (await fetch(baseUrl + "api/status")).json();
    if ((st.running || []).length === 0) { ended = true; break; }
    await new Promise(res => setTimeout(res, 300));
  }
  assert(ended, "运行已正常结束（未启动游戏仍执行强制关闭，无游戏进程则跳过）");
  const dayDir = path.join(runtimeDir, "history", localDate());
  const files = fs.readdirSync(dayDir).filter(f => f.endsWith(".json")).sort();
  const rec = JSON.parse(fs.readFileSync(path.join(dayDir, files[files.length - 1]), "utf8").replace(/^\uFEFF/, ""));
  assert(rec.FinalStatus === "success", "任务 FinalStatus=success（实际 " + rec.FinalStatus + "）");
  await fetch(baseUrl + "api/scripts/" + sid, { method: "DELETE" });
}

async function testV020Features(page) {
  console.log("[用例] v0.2.0：幽灵联动 / 字段改名 / fs 浏览 / 通知开关 / 时间选择器");
  await page.click('nav a[href="#/scripts"]');
  await page.waitForSelector("h2");
  await page.click("text=新建脚本实例");
  await page.waitForSelector("#sm-root");
  const exeDisabled = await page.$eval("#sm-exe", el => el.disabled);
  const argsDisabled = await page.$eval("#sm-args", el => el.disabled);
  const logDisabled = await page.$eval("#sm-log", el => el.disabled);
  assert(exeDisabled && argsDisabled && logDisabled, "根目录未填时主程序/参数/日志输入禁用（幽灵状态）");
  await page.fill("#sm-root", "C:\\scripts\\v");
  await page.waitForFunction(() => document.querySelector("#sm-exe") && !document.querySelector("#sm-exe").disabled, null, { timeout: 5000 });
  const exeEnabled = await page.$eval("#sm-exe", el => !el.disabled);
  assert(exeEnabled, "填写根目录后输入启用");
  assert((await page.textContent("body")).includes("日志文件夹路径"), "日志字段已改名「日志文件夹路径」");
  await page.click(".modal button:has-text('取消')");

  const fs = await (await fetch(baseUrl + "api/fs/browse")).json();
  assert((fs.dirs || []).some(d => /^C:\\$/.test(d)), "fs browse 返回盘符列表（含 C:\\）");
  const fsSub = await (await fetch(baseUrl + "api/fs/browse?path=" + encodeURIComponent("C:\\"))).json();
  assert(Array.isArray(fsSub.dirs) && Array.isArray(fsSub.files), "fs browse 返回目录与文件列表");

  const hdr = { "Content-Type": "application/json" };
  const put = await fetch(baseUrl + "api/settings", { method: "PUT", headers: hdr, body: JSON.stringify({ webhookEnabled: true, smtpEnabled: true }) });
  assert(put.ok, "PUT 设置通知开关成功");
  const got = await (await fetch(baseUrl + "api/settings")).json();
  const gWh = got.settings.webhookEnabled;
  const gSm = got.settings.smtpEnabled;
  assert(gWh === true && gSm === true, "GET 返回通知开关一致（webhook=" + gWh + " smtp=" + gSm + "）");
  await fetch(baseUrl + "api/settings", { method: "PUT", headers: hdr, body: JSON.stringify({ smtpEnabled: false }) });

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

async function testPluginConfig(page) {
  console.log("[用例] 插件配置二级页");
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
  await page.click('nav a[href="#/plugins"]');
  await page.waitForFunction(() => document.querySelector("h2") && document.querySelector("h2").textContent.includes("插件"), null, { timeout: 5000 });
  assert(true, "返回插件列表正常");
}

async function testNextScheduleAndStats(page) {
  console.log("[用例] 下一调度队列显示/倒计时 + 通知统计");
  const hdr = { "Content-Type": "application/json" };
  const exitBat = path.join(runtimeDir, "exit-ok.bat");
  fs.writeFileSync(exitBat, "@echo off\r\nexit /b 0\r\n");
  const create = await fetch(baseUrl + "api/scripts", {
    method: "POST", headers: hdr,
    body: JSON.stringify({
      name: "统计脚本", rootPath: runtimeDir.replace(/\\/g, "\\\\"), mainExe: exitBat.replace(/\\/g, "\\\\"),
      configPath: path.join(runtimeDir, "stat-cfg").replace(/\\/g, "\\\\"),
      logPath: path.join(runtimeDir, "stat-log").replace(/\\/g, "\\\\"),
      maxAttempts: 1, logStallTimeoutMinutes: 5, totalTimeoutMinutes: 120, notifyEnabled: true,
    }),
  });
  const sid = (await create.json()).id;
  const qr = await fetch(baseUrl + "api/queues", {
    method: "POST", headers: hdr,
    body: JSON.stringify({
      name: "统计队列", autoRunMode: "scheduled", completionAction: "none",
      timeSets: [{ id: "", enabled: true, days: [0, 1, 2, 3, 4, 5, 6], time: "23:59" }],
      tasks: [{ id: "", index: 0, scriptInstanceId: sid }], notifyEnabled: true,
    }),
  });
  const qid = (await qr.json()).id;

  await page.click('nav a[href="#/dashboard"]');
  await page.waitForFunction(() => document.body.textContent.includes("统计队列"), null, { timeout: 10000 });
  const cd = await page.textContent("#next-cd");
  assert(/剩余 \d{2}:\d{2}:\d{2}/.test(cd), "下一调度队列显示倒计时（" + cd + "）");
  const body = await page.textContent("body");
  assert(body.includes("1 个脚本实例") && body.includes("1 个调度队列"), "通知统计显示 1 个脚本实例 / 1 个调度队列");

  await fetch(baseUrl + "api/queues/" + qid, { method: "DELETE" });
  await fetch(baseUrl + "api/scripts/" + sid, { method: "DELETE" });
}

async function testLimitsApi(page) {
  console.log("[用例] 约束体系：API 默认值 + 数量上限");
  const hdr = { "Content-Type": "application/json" };
  const limits = await (await fetch(baseUrl + "api/limits")).json();
  assert(limits.limits.maxScripts === 25 && limits.limits.maxUsersPerScript === 10, "limits API 默认值（脚本 25 / 用户 10）");
  assert(limits.limits.maxQueues === 10 && limits.limits.maxTimeSetsPerQueue === 10, "limits API 默认值（队列 10 / 定时 10）");
  assert(limits.limits.maxAttempts === 10 && limits.limits.maxTotalMinutes === 720, "limits API 默认值（尝试 10 / 总时长 720）");
  assert(limits.warnings.length === 0, "默认配置无警告");

  const ids = [];
  for (let i = 0; i < 25; i++) {
    const r = await fetch(baseUrl + "api/scripts", {
      method: "POST", headers: hdr,
      body: JSON.stringify({ name: "约束脚本" + String(i).padStart(2, "0"), rootPath: "C:\\x", mainExe: "C:\\x\\run.bat", configPath: "C:\\x\\cfg", logPath: "C:\\x\\log", maxAttempts: 1, logStallTimeoutMinutes: 5, totalTimeoutMinutes: 120 }),
    });
    ids.push((await r.json()).id);
  }
  const r26 = await fetch(baseUrl + "api/scripts", {
    method: "POST", headers: hdr,
    body: JSON.stringify({ name: "超限脚本", rootPath: "C:\\x", mainExe: "C:\\x\\run.bat", configPath: "C:\\x\\cfg", logPath: "C:\\x\\log", maxAttempts: 1, logStallTimeoutMinutes: 5, totalTimeoutMinutes: 120 }),
  });
  assert(r26.status === 400, "第 26 个脚本被拒（400）");

  for (let i = 0; i < 10; i++) {
    await fetch(baseUrl + "api/scripts/" + ids[0] + "/users", { method: "POST", headers: hdr, body: JSON.stringify({ name: "用户" + i, enabled: true }) });
  }
  const r11 = await fetch(baseUrl + "api/scripts/" + ids[0] + "/users", { method: "POST", headers: hdr, body: JSON.stringify({ name: "用户11", enabled: true }) });
  assert(r11.status === 400, "第 11 个用户被拒（400）");

  const qids = [];
  for (let i = 0; i < 10; i++) {
    const r = await fetch(baseUrl + "api/queues", {
      method: "POST", headers: hdr,
      body: JSON.stringify({ name: "约束队列" + i, autoRunMode: "scheduled", completionAction: "none", timeSets: [{ id: "", enabled: true, days: [1], time: "23:59" }], tasks: [{ id: "", index: 0, scriptInstanceId: ids[1] }] }),
    });
    qids.push((await r.json()).id);
  }
  const q11 = await fetch(baseUrl + "api/queues", {
    method: "POST", headers: hdr,
    body: JSON.stringify({ name: "超限队列", autoRunMode: "scheduled", completionAction: "none", timeSets: [], tasks: [{ id: "", index: 0, scriptInstanceId: ids[1] }] }),
  });
  assert(q11.status === 400, "第 11 个队列被拒（400）");

  const t11 = await fetch(baseUrl + "api/queues/" + qids[0], {
    method: "PUT", headers: hdr,
    body: JSON.stringify({ name: "约束队列0", autoRunMode: "scheduled", completionAction: "none", timeSets: Array.from({ length: 11 }, (_, i) => ({ id: "", enabled: true, days: [1], time: "23:" + String(40 + i) })), tasks: [{ id: "", index: 0, scriptInstanceId: ids[1] }] }),
  });
  assert(t11.status === 400, "第 11 个定时被拒（400）");

  for (const qid of qids) await fetch(baseUrl + "api/queues/" + qid, { method: "DELETE" });
  for (const id of ids) await fetch(baseUrl + "api/scripts/" + id, { method: "DELETE" });
}

async function testLimitsFields(page) {
  console.log("[用例] 约束体系：名称字节 / 数值区间 / 任务总用户");
  const hdr = { "Content-Type": "application/json" };
  const base = { rootPath: "C:\\x", mainExe: "C:\\x\\run.bat", configPath: "C:\\x\\cfg", logPath: "C:\\x\\log", maxAttempts: 1, logStallTimeoutMinutes: 5, totalTimeoutMinutes: 120 };
  const postScript = body => fetch(baseUrl + "api/scripts", { method: "POST", headers: hdr, body: JSON.stringify(body) });

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

  const qLong = await fetch(baseUrl + "api/queues", {
    method: "POST", headers: hdr,
    body: JSON.stringify({ name: "队".repeat(43), autoRunMode: "scheduled", completionAction: "none", timeSets: [], tasks: [{ id: "", index: 0, scriptInstanceId: sid }] }),
  });
  assert(qLong.status === 400, "队列名 129 字节被拒（400）");

  for (let i = 0; i < 10; i++) {
    await fetch(baseUrl + "api/scripts/" + sid + "/users", { method: "POST", headers: hdr, body: JSON.stringify({ name: "任务用户" + i, enabled: true }) });
  }
  const tasks5 = Array.from({ length: 5 }, (_, i) => ({ id: "", index: i, scriptInstanceId: sid }));
  const q5 = await fetch(baseUrl + "api/queues", {
    method: "POST", headers: hdr,
    body: JSON.stringify({ name: "任务50队列", autoRunMode: "scheduled", completionAction: "none", timeSets: [], tasks: tasks5 }),
  });
  assert(q5.ok, "任务启用用户总和 50 通过");
  const qid = (await q5.json()).id;
  const q6 = await fetch(baseUrl + "api/queues", {
    method: "POST", headers: hdr,
    body: JSON.stringify({ name: "任务60队列", autoRunMode: "scheduled", completionAction: "none", timeSets: [], tasks: [...tasks5, { id: "", index: 5, scriptInstanceId: sid }] }),
  });
  assert(q6.status === 400, "任务启用用户总和 60 被拒（400）");

  await fetch(baseUrl + "api/queues/" + qid, { method: "DELETE" });
  await fetch(baseUrl + "api/scripts/" + sid, { method: "DELETE" });
}

async function testPagination(page) {
  console.log("[用例] 分页：脚本列表前端分页 + 达上限禁用 + 历史 API 分页");
  const hdr = { "Content-Type": "application/json" };
  const ids = [];
  for (let i = 0; i < 25; i++) {
    const r = await fetch(baseUrl + "api/scripts", {
      method: "POST", headers: hdr,
      body: JSON.stringify({ name: "分页脚本" + String(i).padStart(2, "0"), rootPath: "C:\\x", mainExe: "C:\\x\\run.bat", configPath: "C:\\x\\cfg", logPath: "C:\\x\\log", maxAttempts: 1, logStallTimeoutMinutes: 5, totalTimeoutMinutes: 120 }),
    });
    ids.push((await r.json()).id);
  }
  await page.click('nav a[href="#/scripts"]');
  await page.waitForSelector("[data-testid='pager-scripts']", { timeout: 10000 });
  const rows1 = await page.$$eval("#view table tbody tr", els => els.length);
  assert(rows1 === 20, "脚本分页第一页 20 行（实际 " + rows1 + "）");
  const info1 = await page.textContent("[data-testid='pager-scripts'] .pager-info");
  assert(info1.includes("共 25 条"), "分页条显示共 25 条");
  await page.click("[data-testid='pager-scripts'] [data-action='pager-next']");
  await page.waitForFunction(() => document.querySelectorAll("#view table tbody tr").length === 5, null, { timeout: 5000 });
  assert(true, "翻页后第二页 5 行");
  const newBtn = await page.$eval("[data-testid='new-script']", el => el.disabled);
  assert(newBtn === true, "脚本达上限新建按钮禁用");

  const hp = await (await fetch(baseUrl + "api/history?days=7&offset=0&limit=5")).json();
  assert(typeof hp.total === "number" && Array.isArray(hp.records) && hp.records.length <= 5, "历史 API 服务端分页返回 {total, records}");

  for (const id of ids) await fetch(baseUrl + "api/scripts/" + id, { method: "DELETE" });
}

async function testLimitsWarnings(page) {
  console.log("[用例] 约束警告：非法配置告警 + 前端卡片（知道了/不再提醒）");
  const logFile = path.join(runtimeDir, "logs", "nexus-pipeline-" + localDate().replace(/-/g, "") + ".log");
  const readLog = () => fs.existsSync(logFile) ? fs.readFileSync(logFile, "utf8").replace(/^\uFEFF/, "") : "";
  const sleep = ms => new Promise(r => setTimeout(r, ms));
  const limitsFile = path.join(runtimeDir, "config", "limits.json");

  fs.mkdirSync(path.dirname(limitsFile), { recursive: true });
  fs.writeFileSync(limitsFile, '{"MaxScripts": 30}');
  await stopService();
  await sleep(400);
  startService();
  await waitForService();
  await sleep(500);
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
  await stopService();
  await sleep(400);
  startService();
  await waitForService();
  await sleep(500);
  await page.goto(baseUrl, { waitUntil: "domcontentloaded" });
  await page.waitForSelector("#limits-warning", { timeout: 10000 });
  assert(true, "警告内容变化后重新提醒");
  await page.click('[data-action="limits-dismiss-forever"]');

  fs.rmSync(limitsFile, { force: true });
  await stopService();
  await sleep(400);
  startService();
  await waitForService();
  const l2 = await (await fetch(baseUrl + "api/limits")).json();
  assert(l2.warnings.length === 0 && l2.limits.maxScripts === 25, "恢复默认配置后无警告");
}

async function testLimitsFatal() {
  console.log("[用例] 约束 FATAL：致命配置拒绝启动");
  const logFile = path.join(runtimeDir, "logs", "nexus-pipeline-" + localDate().replace(/-/g, "") + ".log");
  const readLog = () => fs.existsSync(logFile) ? fs.readFileSync(logFile, "utf8").replace(/^\uFEFF/, "") : "";
  const sleep = ms => new Promise(r => setTimeout(r, ms));
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
  await stopService();
  await sleep(400);
  startService();
  await waitForService();
  assert(true, "恢复默认配置后服务正常启动");
}

/* ---------------- 主流程 ---------------- */

async function main() {
  await setupRuntime();
  startService();
  try {
    await waitForService();
    const browser = await chromium.launch({ channel: "msedge", headless: true });
    try {
      const page = await browser.newPage();
      page.on("pageerror", error => console.error("[页面错误] " + error.message));
      await testDashboard(page);
      await testResponsiveShell(page);
      await testNavigation(page);
      await testScriptCrud(page);
      await testUserManagement(page);
      await testQueueMultiUser(page);
      await testGateRelease(page);
      await testBatchGameLaunch(page);
      await testForceCloseIndependent(page);
      await testV020Features(page);
      await testPluginConfig(page);
      await testNextScheduleAndStats(page);
      await testQueueCrud(page);
      await testDispatchAndHistory(page);
      await testLogScroll(page);
      await testHistoryFiles();
      await testAudit(page);
      await testLogLevel(page);
      await testLimitsApi(page);
      await testLimitsFields(page);
      await testPagination(page);
      await testLimitsWarnings(page);
      await testLimitsFatal();
    } finally {
      await browser.close();
    }
  } finally {
    await stopService();
  }
  console.log("");
  console.log(`结果：通过 ${passed} 项，失败 ${failed} 项`);
  if (failed > 0) process.exit(1);
}

main().catch(err => {
  console.error("[错误] " + err.message);
  stopService();
  process.exit(1);
});
