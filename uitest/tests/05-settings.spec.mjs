import fs from "node:fs";
import path from "node:path";
import { test, expect } from "@playwright/test";
import { baseUrl, PING_GAME, runtimeDir, makeScriptDir, api, localDate, startService, stopService, restartService, waitForService, waitFor, waitNoRunning, runningCount, createScript, killRuntimeServices, sleep, ensureService } from "./helpers.mjs";

await ensureService();

test("审计日志：增删改/查询记录 + 轮询豁免", async ({ page }) => {
  const logFile = path.join(runtimeDir, "logs", "nexus-pipeline-" + localDate() + ".log");
  const readLog = () => fs.readFileSync(logFile, "utf8").replace(/^\uFEFF/, "");

  const aDir = makeScriptDir("audit");
  const created = await api("POST", "/api/scripts", {
    name: "审计脚本", rootPath: aDir.root, mainExe: aDir.main,
    configPath: aDir.cfg, logPath: aDir.log, gameExe: PING_GAME,
    maxAttempts: 3, logStallTimeoutMinutes: 5, totalTimeoutMinutes: 120,
  });
  expect(created.ok, "API 创建脚本").toBeTruthy();
  await new Promise(r => setTimeout(r, 400));
  expect(readLog().includes("[审计] web | 添加脚本实例（审计脚本"), "创建脚本产生审计行").toBeTruthy();
  const auditLine = readLog().split("\n").reverse().find(line => line.includes("[审计] web | 添加脚本实例（审计脚本"));
  expect(/^\[\d{2}:\d{2}:\d{2}\.\d{3}\]/.test(auditLine || ""), "审计行时间为毫秒级 [HH:mm:ss.fff]（" + (auditLine || "").slice(0, 24) + "）").toBeTruthy();

  const list = await (await fetch(baseUrl + "api/scripts")).json();
  const target = list.find(x => x.name === "审计脚本");
  expect(!!target, "列表可查询到审计脚本").toBeTruthy();
  const updated = await api("PUT", "/api/scripts/" + target.id, {
    id: target.id, name: "审计脚本改", rootPath: aDir.root, mainExe: aDir.main,
    configPath: aDir.cfg, logPath: aDir.log, gameExe: PING_GAME,
    maxAttempts: 3, logStallTimeoutMinutes: 5, totalTimeoutMinutes: 120,
  });
  expect(updated.ok, "API 修改脚本").toBeTruthy();
  await new Promise(r => setTimeout(r, 400));
  expect(readLog().includes("[审计] web | 修改脚本实例（审计脚本改"), "修改脚本产生审计行").toBeTruthy();

  await page.goto(baseUrl + "#/history", { waitUntil: "domcontentloaded" });
  await page.waitForFunction(() => document.querySelector("h2") && document.querySelector("h2").textContent.includes("历史记录"));
  await new Promise(r => setTimeout(r, 600));
  expect(readLog().includes("[审计] web | 查询历史记录"), "打开历史页产生查询审计行").toBeTruthy();

  const count1 = (readLog().match(/\[审计\]/g) || []).length;
  await page.waitForTimeout(2600);
  const count2 = (readLog().match(/\[审计\]/g) || []).length;
  expect(count1 === count2, "历史页停留无新增审计行（status 轮询已豁免）").toBeTruthy();

  const del = await api("DELETE", "/api/scripts/" + target.id);
  expect(del.ok, "API 删除脚本").toBeTruthy();
  await new Promise(r => setTimeout(r, 400));
  expect(readLog().includes("[审计] web | 删除脚本实例（审计脚本改"), "删除脚本产生审计行").toBeTruthy();
});

test("日志级别：设置 UI / 落盘 / 阈值过滤 / DEBUG 请求记录", async ({ page }) => {
  const logFile = path.join(runtimeDir, "logs", "nexus-pipeline-" + localDate() + ".log");
  const readLog = () => fs.existsSync(logFile) ? fs.readFileSync(logFile, "utf8").replace(/^\uFEFF/, "") : "";

  await page.goto(baseUrl + "#/settings", { waitUntil: "domcontentloaded" });
  await page.waitForSelector("#st-loglevel");
  const defaultLevel = await page.$eval("#st-loglevel", el => el.value);
  expect(defaultLevel === "info", "设置页含「日志级别」下拉且默认 info").toBeTruthy();
  const levelOptions = await page.$$eval("#st-loglevel option", els => els.map(e => e.textContent));
  expect(levelOptions.length === 5 && levelOptions[0] === "Debug" && levelOptions[4] === "Fatal", "日志级别选项首字母大写（Debug…Fatal）").toBeTruthy();

  let put = await api("PUT", "/api/settings", { logLevel: "warn" });
  expect(put.ok, "PUT logLevel=warn 成功").toBeTruthy();
  const got = await (await fetch(baseUrl + "api/settings")).json();
  expect(got.settings.logLevel === "warn", "GET 返回 logLevel=warn").toBeTruthy();
  const cfg = JSON.parse(fs.readFileSync(path.join(runtimeDir, "config", "settings.json"), "utf8").replace(/^\uFEFF/, ""));
  expect(cfg.LogLevel === "warn", "settings.json 已落盘 LogLevel=warn").toBeTruthy();

  const lgDir = makeScriptDir("loglevel");
  const created = await api("POST", "/api/scripts", {
    name: "日志级别脚本", rootPath: lgDir.root, mainExe: lgDir.main,
    configPath: lgDir.cfg, logPath: lgDir.log, gameExe: PING_GAME,
    maxAttempts: 1, logStallTimeoutMinutes: 5, totalTimeoutMinutes: 120,
  });
  expect(created.ok, "创建日志级别测试脚本（触发 INFO 审计）").toBeTruthy();
  const sid = (await created.json()).id;
  await new Promise(r => setTimeout(r, 400));
  expect(!readLog().includes("[审计] web | 添加脚本实例（日志级别脚本"), "warn 阈值下 INFO 审计行被过滤").toBeTruthy();

  put = await api("PUT", "/api/settings", { logLevel: "debug" });
  expect(put.ok, "PUT logLevel=debug 成功").toBeTruthy();
  await fetch(baseUrl + "api/scripts");
  await new Promise(r => setTimeout(r, 400));
  expect(readLog().includes("[DEBUG] [Web] GET /api/scripts"), "debug 级别记录 Web API 请求").toBeTruthy();
  await fetch(baseUrl + "api/status");
  await new Promise(r => setTimeout(r, 400));
  expect(!readLog().includes("[Web] GET /api/status"), "GET /api/status 轮询豁免（不记录）").toBeTruthy();

  put = await api("PUT", "/api/settings", { logLevel: "info" });
  expect(put.ok, "恢复 logLevel=info 成功").toBeTruthy();
  const del = await api("DELETE", "/api/scripts/" + sid);
  expect(del.ok, "清理日志级别测试脚本").toBeTruthy();
});

test("远程访问设置（令牌加密存储 + 本地豁免）与历史保留天数上限校验", async () => {
  const bad = await api("PUT", "/api/settings", { historyRetentionDays: 999 });
  expect(bad.status === 400, "历史保留天数 999 被拒（400）").toBeTruthy();
  const good = await api("PUT", "/api/settings", { historyRetentionDays: 7 });
  expect(good.ok, "历史保留天数 7 保存成功").toBeTruthy();
  const on = await api("PUT", "/api/settings", { allowRemoteAccess: true, secretKey: "accessToken", secretValue: "test-token-123" });
  expect(on.ok, "开启远程访问 + 设置访问令牌成功").toBeTruthy();
  const settings = await (await fetch(baseUrl + "api/settings")).json();
  expect(settings.settings.allowRemoteAccess === true, "设置回读 allowRemoteAccess=true").toBeTruthy();
  expect(settings.settings.accessToken === "enc:***", "令牌已加密存储（回显掩码 enc:***）").toBeTruthy();
  expect(settings.status.remote && settings.status.remote.tokenSet === true, "状态含远程令牌已设置标记").toBeTruthy();
  expect(Array.isArray(settings.status.remote.lanAddresses), "状态含局域网地址列表 lanAddresses（数组）").toBeTruthy();
  expect(settings.status.remote.lanAddresses.every(addr => /^\d{1,3}(\.\d{1,3}){3}$/.test(addr)), "lanAddresses 均为点分 IPv4 格式").toBeTruthy();
  const st = await fetch(baseUrl + "api/status");
  expect(st.ok, "本地请求豁免令牌校验（/api/status 200）").toBeTruthy();
  const off = await api("PUT", "/api/settings", { allowRemoteAccess: false });
  expect(off.ok, "关闭远程访问成功").toBeTruthy();
});

test("访问令牌：生成后默认隐藏、可切换显示并在保存完成后反馈", async ({ page }) => {
  await page.goto(baseUrl + "#/settings", { waitUntil: "domcontentloaded" });
  await page.waitForSelector("#st-token");
  await page.click('[data-action="gen-token"]');
  await page.waitForFunction(() => document.querySelector("#st-token")?.value.length > 0, null, { timeout: 5000 });
  expect(await page.getAttribute("#st-token", "type"), "生成访问令牌后输入框默认保持 password 隐藏").toBe("password");
  await page.waitForFunction(() => document.body.textContent.includes("访问令牌已保存"), null, { timeout: 5000 });
  expect(await page.getAttribute('[data-action="toggle-token-visibility"]', "aria-pressed"), "令牌保存完成后显示切换按钮保持隐藏状态").toBe("false");
  await page.click('[data-action="toggle-token-visibility"]');
  expect(await page.getAttribute("#st-token", "type"), "点击显示后令牌输入框切换为 text").toBe("text");
  await page.click('[data-action="toggle-token-visibility"]');
  expect(await page.getAttribute("#st-token", "type"), "再次点击后令牌输入框恢复 password").toBe("password");
});

test("重启服务：确认卡片 → 自动重启并恢复（service 模式）", async ({ page }) => {
  // v0.6.9+ F3 治理：页面错误探针，失败时输出 pageerror/console.error 现场
  const pageErrors = [];
  page.on("pageerror", e => pageErrors.push("pageerror: " + e.message));
  page.on("console", m => { if (m.type() === "error") pageErrors.push("console.error: " + m.text()); });
  // 自重启仅常驻服务模式支持；测试基建默认以 web 模式启动服务，此用例切换为 service 模式。
  // 注意：本机若同时运行着其他 nexus-pipeline 常驻服务，单实例互斥体冲突会导致启动失败。
  await stopService();
  startService("service");
  await waitForService();
  await page.goto(baseUrl + "#/settings", { waitUntil: "domcontentloaded" });
  await page.waitForSelector('[data-testid="restart-service"]', { timeout: 15000 });
  await page.click('[data-testid="restart-service"]');
  await page.waitForSelector('[data-action="restart-confirm"]', { timeout: 5000 });
  await page.click('[data-action="restart-confirm"]');
  // v0.6.7+：断言前端「服务重启中」锁定弹窗出现（此前 settings.js 误用 state.schedule 导致首次轮询抛错、锁定弹窗卡死无法自动恢复）
  await page.waitForSelector('.modal[data-locked]', { timeout: 5000 });
  await page.waitForFunction(() => document.body.textContent.includes("服务重启中"), null, { timeout: 5000 });
  // 等待新进程完成重启并接管服务（旧进程响应后 ~1 秒退出，新进程等待互斥体后接管）
  const logFile = path.join(runtimeDir, "logs", "nexus-pipeline-" + localDate() + ".log");
  const readLog = () => fs.readFileSync(logFile, "utf8").replace(/^\uFEFF/, "");
  await waitFor(() => readLog().includes("[重启] 正在等待旧进程退出"), 30000, 300);
  const reachable = await waitFor(async () => {
    try {
      const res = await fetch(baseUrl + "api/status");
      if (!res.ok) return false;
      const s = await res.json();
      return !!s.version;
    } catch { return false; }
  }, 30000, 300);
  expect(reachable, "重启后服务可达（新进程接管）").toBeTruthy();
  const logText = readLog();
  expect(logText.includes("[审计] web | 重启服务"), "重启操作产生审计行").toBeTruthy();
  expect(logText.includes("[重启] 正在等待旧进程退出"), "新进程记录重启日志").toBeTruthy();
  // v0.6.7+：服务恢复后前端应自动刷新并关闭锁定弹窗（此前会永久卡死在「服务重启中」）
  await page.waitForFunction(() => !document.querySelector('.modal[data-locked]'), null, { timeout: 30000 });
  // v0.6.9+ F3 治理：重启后页面滞留「正在连接本地服务...」（reload 后模块加载/服务接管首个请求竞态）
  // 时重载页面重试（服务已恢复，重载即可正常加载）；失败输出 pageerror/console.error 现场
  let restored = false;
  for (let attempt = 0; attempt < 3 && !restored; attempt++) {
    try {
      await page.waitForSelector('[data-testid="restart-service"]', { timeout: 15000 });
      restored = true;
    } catch {
      await page.waitForTimeout(500);
      await page.reload({ waitUntil: "domcontentloaded" });
    }
  }
  expect(restored, "重启后前端恢复（restart-service 按钮可见；页面错误现场：" + (pageErrors.join(" | ") || "无") + "）").toBeTruthy();
  // 清理：杀掉自重启拉起的进程（service 模式，未登记 PID 文件），恢复标准 web 模式测试环境
  await killRuntimeServices();
  startService("web");
  await waitForService();
  await sleep(500);
});

test("重启服务：运行任务时 409 拒绝", async () => {
  await stopService();
  startService("service");
  await waitForService();
  const dDir = makeScriptDir("restart409");
  fs.writeFileSync(path.join(dDir.root, "nexustest-restart409.bat"), "@echo off\r\nping -n 8 127.0.0.1 >nul\r\nexit /b 0\r\n", "ascii");
  const created = await createScript({ name: "重启409脚本", rootPath: dDir.root, mainExe: dDir.main, configPath: dDir.cfg, logPath: dDir.log });
  expect(created.ok, "创建运行任务脚本").toBeTruthy();
  const dispatch = await (await api("POST", "/api/dispatch/script", { scriptId: created.id, mode: "manual" })).json();
  expect(!!dispatch.runId, "发起运行任务成功").toBeTruthy();
  await waitFor(async () => (await runningCount()) > 0, 10000);
  const res = await fetch(baseUrl + "api/settings/restart", { method: "POST" });
  expect(res.status === 409, "运行任务时重启返回 409（" + res.status + "）").toBeTruthy();
  const data = await res.json();
  expect(data.error && data.error.includes("运行中"), "409 错误文案提示存在运行任务").toBeTruthy();
  await api("POST", "/api/cancel", { runId: dispatch.runId });
  await waitNoRunning(30000);
  const del = await api("DELETE", "/api/scripts/" + created.id);
  expect(del.ok, "清理运行任务脚本").toBeTruthy();
  await killRuntimeServices();
  startService("web");
  await waitForService();
  await sleep(500);
});

test("切换按钮文字状态：后缀「：开/：关」实时同步 + 豁免按钮", async ({ page }) => {
  await page.goto(baseUrl + "#/settings", { waitUntil: "domcontentloaded" });
  await page.waitForSelector("#st-autostart");
  expect((await page.textContent("#st-autostart")) === "开机自启：关", "设置页切换按钮初始带「：关」后缀").toBeTruthy();
  await page.click("#st-autostart");
  expect((await page.textContent("#st-autostart")) === "开机自启：开", "点击后按钮文字同步「：开」").toBeTruthy();
  await page.click("#st-autostart");
  expect((await page.textContent("#st-autostart")) === "开机自启：关", "再次点击恢复「：关」").toBeTruthy();

  await page.goto(baseUrl + "#/scripts", { waitUntil: "domcontentloaded" });
  await page.waitForSelector("h2");
  await page.click('[data-testid="new-script"]');
  await page.waitForSelector('[data-action="open-script-type"][data-plugin=""]');
  await page.click('[data-action="open-script-type"][data-plugin=""]');
  await page.waitForSelector("#sm-mode-btn");
  expect((await page.textContent("#sm-mode-btn")) === "使用判断脚本（脚本优先）", "判断脚本模式按钮豁免（不加「：开/关」后缀）").toBeTruthy();
  expect((await page.textContent("#sm-launch")) === "启动游戏：关", "脚本弹窗切换按钮带「：关」后缀").toBeTruthy();
  await page.click("#sm-launch");
  expect((await page.textContent("#sm-launch")) === "启动游戏：开", "脚本弹窗点击后同步「：开」").toBeTruthy();
  await page.click('[data-action="close-modal"]');
});
