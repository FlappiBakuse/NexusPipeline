import fs from "node:fs";
import path from "node:path";
import { test, expect } from "@playwright/test";
import { baseUrl, PING_GAME, runtimeDir, makeScriptDir, api, localDate } from "./helpers.mjs";

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
