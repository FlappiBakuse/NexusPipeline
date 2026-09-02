import { spawnSync } from "node:child_process";
import fs from "node:fs";
import path from "node:path";
import { test, expect } from "@playwright/test";
import { baseUrl, PING_GAME, runtimeDir, api, waitFor, waitNoRunning, ensureService } from "./helpers.mjs";

await ensureService();

/** 构造判断脚本目录：bat 写日志到 logs\log.txt（ASCII 关键字规避 bat 中文编码问题）。 */
function makeJudgeDir(label, logLines, delaySecs = 2) {
  const dir = path.join(runtimeDir, "judge-" + label);
  fs.rmSync(dir, { recursive: true, force: true });
  fs.mkdirSync(path.join(dir, "logs"), { recursive: true });
  const lines = Array.isArray(logLines) ? logLines : [logLines];
  const batLines = ["@echo off", "echo START >> logs\\log.txt", `ping -n ${delaySecs} 127.0.0.1 >nul`];
  for (const line of lines) batLines.push(`echo ${line} >> logs\\log.txt`);
  batLines.push("exit /b 0");
  fs.writeFileSync(path.join(dir, `nexusjudge-${label}.bat`), batLines.join("\r\n") + "\r\n", "ascii");
  return dir;
}

async function judgeCreate(name, dir, label, extra = {}) {
  const res = await api("POST", "/api/scripts", {
    name, rootPath: dir, mainExe: path.join(dir, `nexusjudge-${label}.bat`),
    configPath: dir, logPath: path.join(dir, "logs\\log.txt"),
    maxAttempts: 1, logStallTimeoutMinutes: 2, totalTimeoutMinutes: 10,
    gameExe: PING_GAME, ...extra,
  });
  if (!res.ok) {
    return { ok: false, id: "" };
  }
  const script = await res.json();
  await api("POST", `/api/scripts/${script.id}/users`, { name: "默认", enabled: true });
  return { ok: true, id: script.id };
}

async function judgeRunAndHistory(id) {
  const dispatch = await api("POST", "/api/dispatch/script", { scriptId: id, mode: "manual" });
  const ended = await waitNoRunning(90000);
  const hist = await (await fetch(baseUrl + "api/history?days=7")).json();
  const rec = hist.filter(h => h.scriptInstanceId === id).at(-1);
  return { dispatchOk: dispatch.ok, ended, rec };
}

test("自定义完成标志：成功/失败关键字判定（顺序、AND/OR、失败立即终止）", async () => {
  const d1 = makeJudgeDir("succ", ["TASK DONE"]);
  const s1 = await judgeCreate("关键字成功脚本", d1, "succ", { successKeywords: "DONE", failureKeywords: "" });
  expect(s1.ok, "创建成功关键字脚本").toBeTruthy();
  let r = await judgeRunAndHistory(s1.id);
  expect(r.dispatchOk && r.ended, "成功关键字脚本运行结束").toBeTruthy();
  expect(r.rec && r.rec.finalStatus === "success", "命中成功关键字判定成功（FinalStatus=success）").toBeTruthy();
  await api("DELETE", "/api/scripts/" + s1.id);

  const d2 = makeJudgeDir("and", ["TASK DONE COMPLETED"]);
  const s2 = await judgeCreate("关键字AND脚本", d2, "and", { successKeywords: "DONE, COMPLETED", failureKeywords: "" });
  expect(s2.ok, "创建 AND 关键字脚本（同行双词）").toBeTruthy();
  r = await judgeRunAndHistory(s2.id);
  expect(r.dispatchOk && r.ended, "AND 关键字脚本运行结束").toBeTruthy();
  expect(r.rec && r.rec.finalStatus === "success", "AND 组同行全部出现命中成功（FinalStatus=success）").toBeTruthy();
  await api("DELETE", "/api/scripts/" + s2.id);

  const d3 = makeJudgeDir("and2", ["TASK DONE", "TASK COMPLETED"]);
  const s3 = await judgeCreate("关键字AND跨行脚本", d3, "and2", { successKeywords: "DONE, COMPLETED", failureKeywords: "" });
  expect(s3.ok, "创建 AND 跨行关键字脚本").toBeTruthy();
  r = await judgeRunAndHistory(s3.id);
  expect(r.dispatchOk && r.ended, "AND 跨行关键字脚本运行结束").toBeTruthy();
  expect(r.rec && r.rec.finalStatus === "success", "AND 词跨行分别出现命中成功（v0.7.1 跨日志 AND，FinalStatus=success）").toBeTruthy();
  await api("DELETE", "/api/scripts/" + s3.id);

  const d3b = makeJudgeDir("and3", ["TASK DONE", "TASK DONE AGAIN"]);
  const s3b = await judgeCreate("关键字AND单词脚本", d3b, "and3", { successKeywords: "DONE, COMPLETED", failureKeywords: "" });
  expect(s3b.ok, "创建 AND 单词脚本（整个日志只出现一个词）").toBeTruthy();
  r = await judgeRunAndHistory(s3b.id);
  expect(r.dispatchOk && r.ended, "AND 单词脚本运行结束").toBeTruthy();
  expect(r.rec && r.rec.finalStatus === "failed", "AND 词只出现其一不命中，进程退出判定失败（FinalStatus=failed）").toBeTruthy();
  await api("DELETE", "/api/scripts/" + s3b.id);

  const d4 = makeJudgeDir("fail", ["TASK FAIL"]);
  const s4 = await judgeCreate("失败关键字脚本", d4, "fail", { successKeywords: "", failureKeywords: "FAIL", maxAttempts: 2 });
  expect(s4.ok, "创建失败关键字脚本").toBeTruthy();
  r = await judgeRunAndHistory(s4.id);
  expect(r.dispatchOk && r.ended, "失败关键字脚本运行结束").toBeTruthy();
  expect(r.rec && r.rec.finalStatus === "failed", "命中失败关键字判定失败（FinalStatus=failed）").toBeTruthy();
  expect(r.rec && (r.rec.attemptDetails || []).some(a => a.status === "failed" && /失败关键字/.test(a.reason)), "尝试详情含失败关键字判定原因").toBeTruthy();
  await api("DELETE", "/api/scripts/" + s4.id);

  const d5 = makeJudgeDir("succfirst", ["TASK DONE", "TASK FAIL"]);
  const s5 = await judgeCreate("成功先于失败脚本", d5, "succfirst", { successKeywords: "DONE", failureKeywords: "FAIL" });
  expect(s5.ok, "创建成功先于失败脚本").toBeTruthy();
  r = await judgeRunAndHistory(s5.id);
  expect(r.dispatchOk && r.ended, "成功先于失败脚本运行结束").toBeTruthy();
  expect(r.rec && r.rec.finalStatus === "success", "成功关键字先于失败关键字出现判定成功（FinalStatus=success）").toBeTruthy();
  await api("DELETE", "/api/scripts/" + s5.id);

  const d6 = makeJudgeDir("failfirst", ["TASK FAIL", "TASK DONE"]);
  const s6 = await judgeCreate("失败先于成功脚本", d6, "failfirst", { successKeywords: "DONE", failureKeywords: "FAIL" });
  expect(s6.ok, "创建失败先于成功脚本").toBeTruthy();
  r = await judgeRunAndHistory(s6.id);
  expect(r.dispatchOk && r.ended, "失败先于成功脚本运行结束").toBeTruthy();
  expect(r.rec && r.rec.finalStatus === "failed", "失败关键字先出现判定失败（FinalStatus=failed）").toBeTruthy();
  await api("DELETE", "/api/scripts/" + s6.id);
});

test("自定义完成标志：判断脚本（JS 内置引擎 / Python 系统 / 文件读取 / 无返回）", async () => {
  const d1 = makeJudgeDir("jssucc", ["ANY OUTPUT"]);
  const jsOk = 'console.log(JSON.stringify({ status: "success", reason: "judge-ok", notifyText: "自定义通知" }));';
  const s1 = await judgeCreate("JS成功脚本", d1, "jssucc", { judgeScriptEnabled: true, judgeScriptLanguage: "javascript", judgeScript: jsOk });
  expect(s1.ok, "创建 JS 判断脚本（返回 success）").toBeTruthy();
  let r = await judgeRunAndHistory(s1.id);
  expect(r.dispatchOk && r.ended, "JS 成功脚本运行结束").toBeTruthy();
  expect(r.rec && r.rec.finalStatus === "success", "JS 判定成功（FinalStatus=success）").toBeTruthy();
  await api("DELETE", "/api/scripts/" + s1.id);

  const d2 = makeJudgeDir("jsfail", ["ANY OUTPUT"]);
  const jsFail = 'console.log(JSON.stringify({ status: "failed", reason: "judge-fail" }));';
  const s2 = await judgeCreate("JS失败脚本", d2, "jsfail", { judgeScriptEnabled: true, judgeScriptLanguage: "javascript", judgeScript: jsFail });
  expect(s2.ok, "创建 JS 判断脚本（返回 failed）").toBeTruthy();
  r = await judgeRunAndHistory(s2.id);
  expect(r.dispatchOk && r.ended, "JS 失败脚本运行结束").toBeTruthy();
  expect(r.rec && r.rec.finalStatus === "failed", "JS 判定失败（FinalStatus=failed）").toBeTruthy();
  await api("DELETE", "/api/scripts/" + s2.id);

  const d3 = makeJudgeDir("jsnone", ["ANY OUTPUT"]);
  const s3 = await judgeCreate("JS无返回脚本", d3, "jsnone", { judgeScriptEnabled: true, judgeScriptLanguage: "javascript", judgeScript: 'console.log("hello judge");' });
  expect(s3.ok, "创建 JS 判断脚本（无返回）").toBeTruthy();
  r = await judgeRunAndHistory(s3.id);
  expect(r.dispatchOk && r.ended, "JS 无返回脚本运行结束").toBeTruthy();
  expect(r.rec && r.rec.finalStatus === "failed", "JS 未返回判定结果，进程退出判定失败（FinalStatus=failed）").toBeTruthy();
  await api("DELETE", "/api/scripts/" + s3.id);

  const d4 = makeJudgeDir("jsfile", ["ANY OUTPUT"]);
  fs.writeFileSync(path.join(d4, "data.txt"), "MARK-42", "utf8");
  fs.mkdirSync(path.join(d4, "cfg", "nested"), { recursive: true });
  fs.writeFileSync(path.join(d4, "cfg", "nested", "conf.ini"), "KEY=YES", "utf8");
  const jsFile = `
const input = JSON.parse(__NEXUS_INPUT__);
const files = nexus.listFiles();
const data = nexus.readFile(files.find(f => f.endsWith("data.txt")));
const conf = nexus.readFile(files.find(f => f.endsWith("conf.ini")));
const wrote = nexus.writeFile("written.txt", "HELLO-SCRIPT");
const readBack = nexus.readFile(input.scriptDir + "/written.txt");
if (wrote && readBack && readBack.includes("HELLO-SCRIPT") && data && data.includes("MARK-42") && conf && conf.includes("KEY=YES")) {
  console.log(JSON.stringify({ status: "success", reason: "files-read" }));
} else {
  console.log(JSON.stringify({ status: "failed", reason: "files-missing" }));
}`;
  const s4 = await judgeCreate("JS读文件脚本", d4, "jsfile", { judgeScriptEnabled: true, judgeScriptLanguage: "javascript", judgeScript: jsFile });
  expect(s4.ok, "创建 JS 读文件脚本（config 递归 + script 目录读写）").toBeTruthy();
  r = await judgeRunAndHistory(s4.id);
  expect(r.dispatchOk && r.ended, "JS 读文件脚本运行结束").toBeTruthy();
  expect(r.rec && r.rec.finalStatus === "success", "JS 读取 config 文件并写入/读取 script 目录成功（FinalStatus=success）").toBeTruthy();
  expect(!fs.existsSync(path.join(runtimeDir, "data", s4.id, "script")), "运行结束后 script 目录已清空").toBeTruthy();
  await api("DELETE", "/api/scripts/" + s4.id);

  const pyOk = spawnSync("python", ["--version"], { stdio: "ignore", windowsHide: true }).status === 0;
  if (!pyOk) {
    for (let i = 0; i < 6; i++) expect(true, "python 不可用（跳过 Python 判定脚本断言 " + (i + 1) + "/6）").toBeTruthy();
  } else {
    const d5 = makeJudgeDir("pysucc", ["ANY OUTPUT"]);
    const pyCode = 'import json, sys\ninput_data = json.load(open(sys.argv[1], encoding="utf-8"))\nprint(json.dumps({"status": "success", "reason": "py-ok"}))';
    const s5 = await judgeCreate("Python成功脚本", d5, "pysucc", { judgeScriptEnabled: true, judgeScriptLanguage: "python", judgeScript: pyCode });
    expect(s5.ok, "创建 Python 判断脚本（返回 success）").toBeTruthy();
    r = await judgeRunAndHistory(s5.id);
    expect(r.dispatchOk && r.ended, "Python 成功脚本运行结束").toBeTruthy();
    expect(r.rec && r.rec.finalStatus === "success", "Python 判定成功（FinalStatus=success）").toBeTruthy();
    await api("DELETE", "/api/scripts/" + s5.id);

    const d6 = makeJudgeDir("pyfail", ["ANY OUTPUT"]);
    const pyFail = 'import json, sys\ninput_data = json.load(open(sys.argv[1], encoding="utf-8"))\nprint(json.dumps({"status": "failed", "reason": "py-fail"}))';
    const s6 = await judgeCreate("Python失败脚本", d6, "pyfail", { judgeScriptEnabled: true, judgeScriptLanguage: "python", judgeScript: pyFail });
    expect(s6.ok, "创建 Python 判断脚本（返回 failed）").toBeTruthy();
    r = await judgeRunAndHistory(s6.id);
    expect(r.dispatchOk && r.ended, "Python 失败脚本运行结束").toBeTruthy();
    expect(r.rec && r.rec.finalStatus === "failed", "Python 判定失败（FinalStatus=failed）").toBeTruthy();
    await api("DELETE", "/api/scripts/" + s6.id);
  }
});

test("自定义完成标志：插队替换配置后重试（script 目录中转 + config 还原）", async () => {
  const dir = path.join(runtimeDir, "judge-replace");
  fs.rmSync(dir, { recursive: true, force: true });
  fs.mkdirSync(path.join(dir, "logs"), { recursive: true });
  fs.writeFileSync(path.join(dir, "mode.txt"), "FAIL", "utf8");
  fs.writeFileSync(path.join(dir, "nexusjudge-replace.bat"), [
    "@echo off",
    "set /p MODE=<mode.txt",
    'if "%MODE%"=="DONE" (echo TASK DONE >> logs\\log.txt) else (echo TASK FAIL >> logs\\log.txt)',
    "exit /b 0",
  ].join("\r\n") + "\r\n", "ascii");
  const jsReplace = `
const input = JSON.parse(__NEXUS_INPUT__);
if (input.log.includes("TASK DONE")) {
  console.log(JSON.stringify({ status: "success", reason: "done" }));
} else {
  nexus.writeFile("mode.txt", "DONE");
  console.log(JSON.stringify({ status: "failed", reason: "task-failed", replaceConfigs: ["mode.txt"] }));
}`;
  const created = await api("POST", "/api/scripts", {
    name: "插队替换脚本", rootPath: dir, mainExe: path.join(dir, "nexusjudge-replace.bat"),
    configPath: dir, logPath: path.join(dir, "logs\\log.txt"),
    maxAttempts: 2, logStallTimeoutMinutes: 2, totalTimeoutMinutes: 10, gameExe: PING_GAME,
    judgeScriptEnabled: true, judgeScriptLanguage: "javascript", judgeScript: jsReplace,
  });
  expect(created.ok, "创建插队替换脚本（首次失败+replaceConfigs，重试后成功）").toBeTruthy();
  const id = (await created.json()).id;
  await api("POST", `/api/scripts/${id}/users`, { name: "默认", enabled: true });
  const r = await judgeRunAndHistory(id);
  expect(r.dispatchOk && r.ended, "插队替换脚本运行结束").toBeTruthy();
  expect(r.rec && r.rec.finalStatus === "partial", "替换配置后重试成功（重试>1，FinalStatus=partial）").toBeTruthy();
  expect(r.rec && r.rec.attempts === 2, "替换后发生重试（attempts=2）").toBeTruthy();
  const modeAfter = fs.readFileSync(path.join(dir, "mode.txt"), "utf8").trim();
  expect(modeAfter === "FAIL", "运行结束后 config 已还原至启动前状态（mode.txt=FAIL，实际 " + modeAfter + "）").toBeTruthy();
  expect(!fs.existsSync(path.join(runtimeDir, "data", id, "默认", "retry-store")), "运行结束后 retry-store 已清理").toBeTruthy();
  await api("DELETE", "/api/scripts/" + id);
});

test("自定义完成标志：进程退出时最终触发一次判断脚本", async () => {
  const d1 = makeJudgeDir("fin", ["ANY OUTPUT"]);
  const jsFin = `
const input = JSON.parse(__NEXUS_INPUT__);
const counter = input.scriptDir + "/counter.txt";
const n = Number(nexus.readFile(counter) || "0") + 1;
nexus.writeFile("counter.txt", String(n));
if (n >= 2) {
  console.log(JSON.stringify({ status: "success", reason: "final-ok" }));
}`;
  const s1 = await judgeCreate("最终触发脚本", d1, "fin", { judgeScriptEnabled: true, judgeScriptLanguage: "javascript", judgeScript: jsFin });
  expect(s1.ok, "创建最终触发脚本（首次批次触发无返回，进程退出最终触发判定成功）").toBeTruthy();
  const r = await judgeRunAndHistory(s1.id);
  expect(r.dispatchOk && r.ended, "最终触发脚本运行结束").toBeTruthy();
  expect(r.rec && r.rec.finalStatus === "success", "进程退出时最终触发判定成功（FinalStatus=success）").toBeTruthy();
  await api("DELETE", "/api/scripts/" + s1.id);
});

test("自定义完成标志：日志阻塞时周期触发判断脚本", async () => {
  const dir = path.join(runtimeDir, "judge-periodic");
  fs.rmSync(dir, { recursive: true, force: true });
  fs.mkdirSync(path.join(dir, "logs"), { recursive: true });
  // NEXUS_TIME_SCALE 加速档下宿主周期触发间隔 30 秒 → 1 秒（scale=10 → 3 秒），脚本阻塞时长同步缩放（真实 64 秒 / 加速 5 秒）
  const FAST = (Number(process.env.NEXUS_TIME_SCALE || "1") || 1) > 1;
  fs.writeFileSync(path.join(dir, "nexusjudge-periodic.bat"), [
    "@echo off",
    "echo ONLY-ONCE >> logs\\log.txt",
    "ping -n " + (FAST ? 6 : 65) + " 127.0.0.1 >nul",
    "exit /b 0",
  ].join("\r\n") + "\r\n", "ascii");
  const jsPeriodic = `
const n = Number(nexus.readFile("counter.txt") || "0") + 1;
nexus.writeFile("counter.txt", String(n));
if (n >= 2) {
  console.log(JSON.stringify({ status: "failed", reason: "blocked" }));
}`;
  const created = await api("POST", "/api/scripts", {
    name: "周期触发脚本", rootPath: dir, mainExe: path.join(dir, "nexusjudge-periodic.bat"),
    configPath: dir, logPath: path.join(dir, "logs\\log.txt"),
    maxAttempts: 1, logStallTimeoutMinutes: 5, totalTimeoutMinutes: 10, gameExe: PING_GAME,
    judgeScriptEnabled: true, judgeScriptLanguage: "javascript", judgeScript: jsPeriodic,
  });
  expect(created.ok, "创建周期触发脚本（日志阻塞后周期触发判定失败）").toBeTruthy();
  const id = (await created.json()).id;
  await api("POST", `/api/scripts/${id}/users`, { name: "默认", enabled: true });
  const r = await judgeRunAndHistory(id);
  expect(r.dispatchOk && r.ended, "周期触发脚本运行结束").toBeTruthy();
  expect(r.rec && r.rec.finalStatus === "failed", "阻塞期间周期触发判定失败（FinalStatus=failed）").toBeTruthy();
  await api("DELETE", "/api/scripts/" + id);
});

test("自定义完成标志前端：关键字区/脚本区切换、上传识别语言、专用脚本不显示（当前插件判断脚本）", async ({ page }) => {
  await page.goto(baseUrl + "#/scripts", { waitUntil: "domcontentloaded" });
  await page.waitForFunction(() => document.querySelector("h2") && document.querySelector("h2").textContent.includes("脚本实例"), null, { timeout: 5000 });
  await page.click('[data-testid="new-script"]');
  await page.waitForSelector(".new-script-chooser", { timeout: 5000 });
  await page.click('[data-action="open-script-type"][data-plugin=""]');
  await page.waitForSelector("#sm-mode-btn", { timeout: 5000 });

  expect(await page.$("#sm-succ-kw"), "成功关键字填写框显示").toBeTruthy();
  expect(await page.$("#sm-fail-kw"), "失败关键字填写框显示").toBeTruthy();
  expect(!(await page.$("#sm-script-box:not([hidden])")), "默认关闭时脚本区隐藏").toBeTruthy();
  expect(await page.$("#sm-kw-box:not([hidden])"), "默认关键字区可见").toBeTruthy();
  expect((await page.$eval("#sm-mode-btn", el => el.getAttribute("aria-pressed"))) === "false", "默认按钮未激活").toBeTruthy();

  await page.click('[data-action="toggle-judge-mode"]');
  expect((await page.$eval("#sm-mode-btn", el => el.getAttribute("aria-pressed"))) === "true", "点击按钮后激活").toBeTruthy();
  expect(await page.$("#sm-kw-box[hidden]"), "开启后关键字区隐藏").toBeTruthy();
  expect(await page.$("#sm-script-box:not([hidden])"), "开启后脚本区显示").toBeTruthy();
  expect(await page.$("#sm-upload-btn:not([hidden])"), "上传脚本按钮显示").toBeTruthy();
  const judgeLayoutOn = await page.evaluate(() => {
    const upload = document.querySelector("#sm-upload-btn")?.getBoundingClientRect();
    const mode = document.querySelector("#sm-mode-btn")?.getBoundingClientRect();
    const auto = document.querySelector('[data-switch-row="sm-autoupdate"]')?.getBoundingClientRect();
    const row = document.querySelector(".judge-actions")?.getBoundingClientRect();
    const style = document.querySelector(".judge-actions") ? getComputedStyle(document.querySelector(".judge-actions")) : null;
    const gapPx = parseFloat(style?.gap) || 12;
    const third = (row?.width || 0) > 0 ? (row.width - 2 * gapPx) / 3 : 0;
    const game = document.querySelector('[data-switch-row="sm-launch"]')?.getBoundingClientRect();
    return {
      hasAll: !!(upload && mode && auto && row),
      order: !!upload && !!mode && !!auto && upload.left < mode.left && mode.left < auto.left,
      rightAligned: !!auto && !!row && Math.abs(auto.right - row.right) <= 1,
      thirdWidth: [upload, mode, auto].every(box => !!box && Math.abs(box.width - third) <= 2),
      sameAsGameGroup: !!auto && !!game && Math.abs(auto.width - game.width) <= 2,
    };
  });
  expect(judgeLayoutOn.hasAll && judgeLayoutOn.order, "开启后按钮顺序为上传脚本文件、使用判断脚本、自动更新配置").toBeTruthy();
  expect(judgeLayoutOn.rightAligned, "开启后按钮组靠右对齐").toBeTruthy();
  expect(judgeLayoutOn.thirdWidth && judgeLayoutOn.sameAsGameGroup, "开启后三个按钮均占行宽 1/3，与游戏联动组按钮同宽").toBeTruthy();
  const langVal = await page.$eval("#sm-judge-lang", el => el.value);
  expect(langVal === "javascript", "语言默认 JavaScript").toBeTruthy();

  await page.click('[data-action="toggle-judge-mode"]');
  expect((await page.$eval("#sm-mode-btn", el => el.getAttribute("aria-pressed"))) === "false", "再次点击按钮还原为未激活").toBeTruthy();
  expect(await page.$("#sm-kw-box:not([hidden])"), "还原后关键字区重新显示").toBeTruthy();
  expect(await page.$("#sm-upload-btn[hidden]"), "还原后上传按钮隐藏").toBeTruthy();
  const judgeLayoutOff = await page.evaluate(() => {
    const mode = document.querySelector("#sm-mode-btn")?.getBoundingClientRect();
    const auto = document.querySelector('[data-switch-row="sm-autoupdate"]')?.getBoundingClientRect();
    const row = document.querySelector(".judge-actions")?.getBoundingClientRect();
    const style = document.querySelector(".judge-actions") ? getComputedStyle(document.querySelector(".judge-actions")) : null;
    const gapPx = parseFloat(style?.gap) || 12;
    const third = (row?.width || 0) > 0 ? (row.width - 2 * gapPx) / 3 : 0;
    const game = document.querySelector('[data-switch-row="sm-launch"]')?.getBoundingClientRect();
    return {
      hasAll: !!(mode && auto && row),
      order: !!mode && !!auto && mode.left < auto.left,
      rightAligned: !!auto && !!row && Math.abs(auto.right - row.right) <= 1,
      thirdWidth: [mode, auto].every(box => !!box && Math.abs(box.width - third) <= 2),
      sameAsGameGroup: !!auto && !!game && Math.abs(auto.width - game.width) <= 2,
    };
  });
  expect(judgeLayoutOff.hasAll && judgeLayoutOff.order, "关闭后顺序为使用判断脚本、自动更新配置").toBeTruthy();
  expect(judgeLayoutOff.rightAligned, "关闭后按钮组靠右对齐").toBeTruthy();
  expect(judgeLayoutOff.thirdWidth && judgeLayoutOff.sameAsGameGroup, "关闭后两个按钮均占行宽 1/3，与游戏联动组按钮同宽").toBeTruthy();
  await page.click('[data-action="toggle-judge-mode"]');

  const pyFile = path.join(runtimeDir, "judge-upload.py");
  fs.writeFileSync(pyFile, "import json, sys\nprint('x')\n", "utf8");
  const [chooser] = await Promise.all([
    page.waitForEvent("filechooser"),
    page.click('[data-action="upload-judge-script"]'),
  ]);
  await chooser.setFiles(pyFile);
  await page.waitForFunction(() => (document.querySelector("#sm-judge-code")?.value || "").includes("import json"), null, { timeout: 5000 });
  const codeVal = await page.$eval("#sm-judge-code", el => el.value);
  const langVal2 = await page.$eval("#sm-judge-lang", el => el.value);
  expect(codeVal.includes("import json"), "上传后代码填入代码框").toBeTruthy();
  expect(langVal2 === "python", "上传 .py 自动识别为 Python").toBeTruthy();

  const jsFile = path.join(runtimeDir, "judge-upload.js");
  fs.writeFileSync(jsFile, "console.log('x');\n", "utf8");
  const [chooser2] = await Promise.all([
    page.waitForEvent("filechooser"),
    page.click('[data-action="upload-judge-script"]'),
  ]);
  await chooser2.setFiles(jsFile);
  await page.waitForFunction(() => document.querySelector("#sm-judge-lang")?.value === "javascript", null, { timeout: 5000 });
  const langVal3 = await page.$eval("#sm-judge-lang", el => el.value);
  expect(langVal3 === "javascript", "上传 .js 自动识别为 JavaScript").toBeTruthy();
  await page.click('[data-action="close-modal"]');
  await page.waitForSelector(".modal-mask", { state: "detached", timeout: 5000 });

  const bgiRoot = path.join(runtimeDir, "sim-bettergi-ui");
  fs.rmSync(bgiRoot, { recursive: true, force: true });
  fs.mkdirSync(bgiRoot, { recursive: true });
  fs.writeFileSync(path.join(bgiRoot, "BetterGI.exe"), "");
  await page.click('[data-testid="new-script"]');
  await page.waitForSelector(".new-script-chooser", { timeout: 5000 });
  await page.click('[data-action="open-script-type"][data-plugin="bettergi"]');
  await page.waitForSelector("#sm-root", { timeout: 5000 });
  expect(!(await page.$("#sm-mode-btn")), "专项脚本弹窗不显示自定义完成标志区（判断脚本由当前插件 profile 提供，用户不可编辑）").toBeTruthy();
  await page.click('[data-action="close-modal"]');

  const created = await api("POST", "/api/scripts", {
    name: "专项判断脚本动态解析", pluginType: "bettergi",
    rootPath: bgiRoot.replace(/\\/g, "\\\\"),
    maxAttempts: 3, logStallTimeoutMinutes: 5, totalTimeoutMinutes: 30, gameExe: PING_GAME,
  });
  expect(created.ok, "API 创建 BetterGI 专项脚本（模拟目录）").toBeTruthy();
  const sp = await created.json();
  expect(sp.judgeScriptEnabled === true, "专项脚本自动解析当前判断脚本（JudgeScriptEnabled=true）").toBeTruthy();
  expect(sp.judgeScript.includes("一条龙和配置组任务结束"), "当前插件判断脚本含运行结束关键字").toBeTruthy();
  expect(sp.judgeScript.includes("config-restore.json") && sp.judgeScript.includes("TaskEnabledList"), "当前插件判断脚本含选择性重试与还原描述逻辑").toBeTruthy();
  expect(sp.configPath.endsWith("OneDragon"), "专项 ConfigPath 指向一条龙配置目录（User/OneDragon）").toBeTruthy();
  const forcedOff = await api("PUT", "/api/scripts/" + sp.id, { ...sp, autoUpdateConfig: false });
  expect(forcedOff.ok, "API 修改专项脚本成功").toBeTruthy();
  const forcedOffScript = await forcedOff.json();
  expect(forcedOffScript.autoUpdateConfig === true, "后端强制专项自动更新配置保持开启").toBeTruthy();
  await api("DELETE", "/api/scripts/" + sp.id);
  await page.evaluate(() => { location.hash = "#/dashboard"; });
  await page.waitForFunction(() => document.querySelector("h2") && document.querySelector("h2").textContent.includes("仪表盘"), null, { timeout: 5000 });
});

test("自动更新配置开关：通用弹窗默认开/切换/保存/回显；专项不渲染（v0.7.6）", async ({ page }) => {
  await page.goto(baseUrl + "#/scripts", { waitUntil: "domcontentloaded" });
  await page.waitForFunction(() => document.querySelector("h2") && document.querySelector("h2").textContent.includes("脚本实例"), null, { timeout: 5000 });
  await page.click('[data-testid="new-script"]');
  await page.waitForSelector(".new-script-chooser", { timeout: 5000 });
  await page.click('[data-action="open-script-type"][data-plugin=""]');
  await page.waitForSelector("#sm-autoupdate", { timeout: 5000 });

  expect((await page.$eval("#sm-autoupdate", el => el.getAttribute("aria-pressed"))) === "true", "自动更新配置按钮默认开").toBeTruthy();
  expect((await page.textContent('[data-switch-row="sm-autoupdate"]')).includes("自动更新配置"), "开关行含主文案").toBeTruthy();

  await page.click('[data-action="toggle-sm-flag"][data-flag="autoupdate"]');
  expect((await page.$eval("#sm-autoupdate", el => el.getAttribute("aria-pressed"))) === "false", "点击后切换为关").toBeTruthy();

  const dir = path.join(runtimeDir, "au-ui");
  fs.rmSync(dir, { recursive: true, force: true });
  fs.mkdirSync(path.join(dir, "cfg"), { recursive: true });
  fs.mkdirSync(path.join(dir, "logs"), { recursive: true });
  fs.writeFileSync(path.join(dir, "nexusjudge-au.bat"), "@echo off\r\nexit /b 0\r\n", "ascii");
  await page.fill("#sm-name", "自动更新配置脚本");
  await page.fill("#sm-root", dir.replace(/\\/g, "\\\\"));
  await page.fill("#sm-exe", path.join(dir, "nexusjudge-au.bat").replace(/\\/g, "\\\\"));
  await page.fill("#sm-config", path.join(dir, "cfg").replace(/\\/g, "\\\\"));
  await page.fill("#sm-log", path.join(dir, "logs\\log.txt").replace(/\\/g, "\\\\"));
  await page.fill("#sm-game-exe", PING_GAME);
  await page.click(".modal button:has-text('保存')");
  await page.waitForSelector(".modal-mask", { state: "detached", timeout: 5000 });

  const list = await (await api("GET", "api/scripts")).json();
  const s = list.find(x => x.name === "自动更新配置脚本");
  expect(s && s.autoUpdateConfig === false, "保存后字段落盘（autoUpdateConfig=false）").toBeTruthy();

  await page.click(`[data-action="edit-script"][data-id="${s.id}"]`);
  await page.waitForSelector("#sm-autoupdate", { timeout: 5000 });
  await page.waitForFunction(() => document.querySelector("#sm-autoupdate")?.getAttribute("aria-pressed") === "false", null, { timeout: 5000 });
  expect((await page.$eval("#sm-autoupdate", el => el.getAttribute("aria-pressed"))) === "false", "编辑弹窗回显为关").toBeTruthy();
  await page.click('[data-action="close-modal"]');
  await page.waitForSelector(".modal-mask", { state: "detached", timeout: 5000 });
  await api("DELETE", "/api/scripts/" + s.id);

  const bgiRoot = path.join(runtimeDir, "sim-bettergi-au");
  fs.rmSync(bgiRoot, { recursive: true, force: true });
  fs.mkdirSync(bgiRoot, { recursive: true });
  fs.writeFileSync(path.join(bgiRoot, "BetterGI.exe"), "");
  await page.click('[data-testid="new-script"]');
  await page.waitForSelector(".new-script-chooser", { timeout: 5000 });
  await page.click('[data-action="open-script-type"][data-plugin="bettergi"]');
  await page.waitForSelector("#sm-root", { timeout: 5000 });
  expect(!(await page.$("#sm-autoupdate")), "专项弹窗不渲染自动更新配置按钮").toBeTruthy();
  await page.click('[data-action="close-modal"]');

  await page.evaluate(() => { location.hash = "#/dashboard"; });
  await page.waitForFunction(() => document.querySelector("h2") && document.querySelector("h2").textContent.includes("仪表盘"), null, { timeout: 5000 });
});

test("Missing 形态还原：运行前配置位置不存在，运行结束（自然结束/运行中取消）后不残留 store 快照", async () => {
  // ---- 变体 A：专项脚本 + 快速退出进程（ping 冒充 BetterGI）→ 自然结束 ----
  const rootA = path.join(runtimeDir, "sim-bgi-missing-a");
  fs.rmSync(rootA, { recursive: true, force: true });
  fs.mkdirSync(path.join(rootA, "User", "OneDragon"), { recursive: true });
  fs.copyFileSync("C:\\Windows\\System32\\ping.exe", path.join(rootA, "BetterGI.exe"));
  const cfgDirA = path.join(rootA, "User", "OneDragon");
  fs.writeFileSync(path.join(cfgDirA, "默认配置.json"), JSON.stringify({ Name: "初始配置", TaskEnabledList: {} }), "utf8");
  const createdA = await api("POST", "/api/scripts", { name: "Missing自然结束", pluginType: "bettergi", rootPath: rootA.replace(/\\/g, "\\\\"), gameExe: PING_GAME, maxAttempts: 1, logStallTimeoutMinutes: 5, totalTimeoutMinutes: 30 });
  expect(createdA.ok, "创建专项脚本（ping 冒充 BetterGI，快速退出）").toBeTruthy();
  const spA = await createdA.json();
  await api("POST", `/api/scripts/${spA.id}/users`, { name: "默认", enabled: true });
  const dataA = path.join(runtimeDir, "data", spA.id, "默认");
  expect(!fs.existsSync(path.join(dataA, "store")) || fs.readdirSync(path.join(dataA, "store")).length === 0, "v0.12.8 绑定不建立配置快照").toBeTruthy();
  fs.rmSync(cfgDirA, { recursive: true, force: true });
  expect(!fs.existsSync(cfgDirA), "运行前配置位置不存在（Missing 形态）").toBeTruthy();

  await api("POST", "/api/dispatch/script", { scriptId: spA.id, mode: "manual" });
  expect(await waitNoRunning(90000), "自然结束运行结束").toBeTruthy();
  await new Promise(r => setTimeout(r, 300));
  expect(!fs.existsSync(cfgDirA), "运行结束后配置位置还原为不存在（不残留快照）").toBeTruthy();
  expect(!fs.existsSync(path.join(dataA, "store")) || fs.readdirSync(path.join(dataA, "store")).length === 0, "store 保持为空（Missing 形态不入库）").toBeTruthy();
  expect(!fs.existsSync(path.join(dataA, ".session")), "运行结束后 .session 已清除").toBeTruthy();
  const originalA = path.join(dataA, "original");
  expect(!fs.existsSync(originalA) || fs.readdirSync(originalA).length === 0, "original 已清空").toBeTruthy();
  const bgiA = spawnSync("tasklist", ["/FI", "IMAGENAME eq BetterGI.exe"], { stdio: "pipe", encoding: "utf8" }).stdout;
  expect(!bgiA.toLowerCase().includes("bettergi.exe"), "运行结束后 BetterGI 进程无残留").toBeTruthy();
  await api("DELETE", "/api/scripts/" + spA.id);

  // ---- 变体 B：通用脚本 + 单文件配置（先建后删）+ 长运行脚本 → 运行中取消 ----
  const dirB = path.join(runtimeDir, "sim-missing-cancel");
  fs.rmSync(dirB, { recursive: true, force: true });
  fs.mkdirSync(path.join(dirB, "logs"), { recursive: true });
  fs.writeFileSync(path.join(dirB, "nexusmissing.bat"), "@echo off\r\nping -n 30 127.0.0.1 >nul\r\nexit /b 0\r\n", "ascii");
  const cfgB = path.join(dirB, "setup.txt");
  fs.writeFileSync(cfgB, "INITIAL", "utf8");
  const createdB = await api("POST", "/api/scripts", {
    name: "Missing运行取消", rootPath: dirB.replace(/\\/g, "\\\\"),
    mainExe: path.join(dirB, "nexusmissing.bat").replace(/\\/g, "\\\\"),
    configPath: cfgB.replace(/\\/g, "\\\\"), logPath: path.join(dirB, "logs\\log.txt"),
    gameExe: PING_GAME, maxAttempts: 1, logStallTimeoutMinutes: 5, totalTimeoutMinutes: 30,
  });
  expect(createdB.ok, "创建通用脚本（单文件配置）").toBeTruthy();
  const spB = await createdB.json();
  await api("POST", `/api/scripts/${spB.id}/users`, { name: "默认", enabled: true });
  const dataB = path.join(runtimeDir, "data", spB.id, "默认");
  expect(fs.existsSync(path.join(dataB, "store", "setup.txt")), "添加用户生成 store 快照").toBeTruthy();
  fs.rmSync(cfgB, { force: true });
  expect(!fs.existsSync(cfgB), "运行前配置位置不存在（Missing 形态）").toBeTruthy();

  await api("POST", "/api/dispatch/script", { scriptId: spB.id, mode: "manual" });
  await waitFor(async () => (await (await fetch(baseUrl + "api/status")).json()).running?.length > 0, 10000);
  const statusB = await (await fetch(baseUrl + "api/status")).json();
  const runIdB = (statusB.running || []).find(item => item.targetId === spB.id)?.id;
  expect(!!runIdB, "取消前已获取运行任务 id").toBeTruthy();
  await api("POST", "/api/cancel", { runId: runIdB });
  expect(await waitNoRunning(30000), "取消后运行结束").toBeTruthy();
  await new Promise(r => setTimeout(r, 500));
  expect(!fs.existsSync(cfgB), "取消后配置位置还原为不存在（不残留 store 快照）").toBeTruthy();
  expect(fs.existsSync(path.join(dataB, "store", "setup.txt")), "store 快照保留").toBeTruthy();
  expect(!fs.existsSync(path.join(dataB, ".session")), "取消后 .session 已清除").toBeTruthy();
  const originalB = path.join(dataB, "original");
  expect(!fs.existsSync(originalB) || fs.readdirSync(originalB).length === 0, "original 已清空").toBeTruthy();
  await api("DELETE", "/api/scripts/" + spB.id);
});
