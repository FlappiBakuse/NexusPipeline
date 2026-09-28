import test, { before, after } from "node:test";
import assert from "node:assert/strict";
import fs from "node:fs";
import path from "node:path";
import http from "node:http";
import { spawn } from "node:child_process";
import { once } from "node:events";
import { createHash } from "node:crypto";
import { createRequire } from "node:module";
import { readProcessIdentity } from "../support/windows-process.mjs";
import {
  api, prepareRuntime, runtimeDir, startRuntime, stopRuntime, waitForService,
  waitNoRunning, waitForHistory, createUserBinding, runtimeDiagnostic,
  makeFixture, writeBatch, waitFor, projectRoot, serviceUrl,
} from "./runtime-helper.mjs";

const inputsRoot = process.env.NEXUS_SYSTEM_MAA_INPUTS;
if (!inputsRoot) throw new Error("Use the existing system runner: dev system maa (required prepared native inputs)");
const inputs = JSON.parse(fs.readFileSync(path.join(inputsRoot, "inputs.json"), "utf8"));
assert.equal(inputs.schemaVersion, 1);
assert.equal(inputs.remoteWrites, false);
assert.equal(createHash("sha256").update(fs.readFileSync(inputs.package)).digest("hex"), inputs.packageSha256);
let server;
let fixtureWindow;
let hostLaunchedWindow;
let journalLocker;
let caseEnvironment = {};
const savedCatalog = process.env.NEXUS_PLUGIN_CATALOG_URL;
const savedPackages = process.env.NEXUS_PLUGIN_PACKAGE_BASE_URL;
const evidence = [];
async function configureFromBrowser(script, root) {
  const require = createRequire(path.join(projectRoot, "tests", "e2e", "package.json"));
  const { chromium, expect } = require("@playwright/test");
  const browser = await chromium.launch({ channel: "msedge", headless: true });
  const errors = [];
  try {
    const page = await browser.newPage({ locale: "zh-CN" });
    page.on("pageerror", error => errors.push(error.message));
    page.on("dialog", dialog => dialog.accept());
    await page.goto(serviceUrl() + "#/scripts", { waitUntil: "domcontentloaded" });
    await page.getByTestId("script-card").filter({ hasText: script.name })
      .getByRole("button", { name: "编辑脚本", exact: true }).click();
    await page.getByRole("button", { name: "读取项目", exact: true }).click();
    await expect(page.getByRole("button", { name: "任务预设", exact: true })).toBeVisible();
    await page.getByRole("button", { name: "任务预设", exact: true }).click();
    await page.getByRole("option", { name: "Daily", exact: true }).click();
    await page.getByRole("button", { name: "应用所选预设到草稿", exact: true }).click();
    await expect(page.getByRole("button", { name: "任务（按选中顺序执行）", exact: true })).toContainText("Daily");
    await page.getByRole("button", { name: "只读执行预览", exact: true }).click();
    await expect(page.getByRole("button", { name: "确认授权并保存独立配置", exact: true })).toBeVisible();
    await expect(page.locator('[data-plugin-slot="scripts.editor.sections"]').first()).toContainText("executionFingerprint");
    await page.getByRole("button", { name: "确认授权并保存独立配置", exact: true }).click();
    await expect(page.getByRole("status").filter({ hasText: "配置已保存" })).toBeVisible();
    await page.getByRole("button", { name: "保存", exact: true }).click();
    await expect(page.locator("#sm-name")).toBeHidden();
    assert.deepEqual(errors, []);
    const configured = await json("POST", "/api/plugin-api/maa-framework/profile",
      { scriptId: script.id, profileId: script.executionProviderConfigId, userId: "" });
    fs.writeFileSync(path.join(root, "browser-configuration-evidence.json"), JSON.stringify({
      source: "actual hosted plugin slot and public controls", errors, configured,
    }, null, 2));
    return configured;
  } finally { await browser.close(); }
}
async function json(method, route, body) {
  const response = await api(method, route, body);
  const value = await response.json();
  assert.equal(response.status, 200, route + ": " + JSON.stringify(value) + "\n" + runtimeDiagnostic());
  return value;
}
async function restart() {
  await stopRuntime();
  startRuntime(["web"], caseEnvironment);
  await waitForService(null, 60000);
}
async function closeWindow() {
  if (hostLaunchedWindow) {
    const owned = hostLaunchedWindow;
    if (!owned.pid && fs.existsSync(path.join(owned.root, "window-identity.json")))
      Object.assign(owned, JSON.parse(fs.readFileSync(path.join(owned.root, "window-identity.json"), "utf8")));
    if (owned.pid && readProcessIdentity(owned.pid))
      assert.equal(readProcessIdentity(owned.pid).startTime, owned.startedAtUtc, "fixture PID reused");
    fs.writeFileSync(path.join(owned.root, "stop-window"), "owned lifecycle stop");
    const deadline = Date.now() + 10000;
    while (readProcessIdentity(owned.pid) && Date.now() < deadline) {
      assert.equal(readProcessIdentity(owned.pid)?.startTime, owned.startedAtUtc, "fixture PID reused");
      await new Promise(resolve => setTimeout(resolve, 50));
    }
    assert.equal(readProcessIdentity(owned.pid), null, "Host-launched owned window did not close");
    hostLaunchedWindow = null;
  }
  if (!fixtureWindow || fixtureWindow.exitCode !== null) return;
  const current = fixtureWindow;
  const exited = once(current, "exit");
  current.stdin.end("\n");
  await Promise.race([exited, new Promise((_, reject) => setTimeout(() => reject(new Error("Owned fixture did not close")), 10000).unref())]);
  fixtureWindow = null;
}
async function closeLocker() {
  if (!journalLocker || journalLocker.exitCode !== null) return;
  const current = journalLocker;
  const exited = once(current, "exit");
  current.stdin.end("\n");
  await Promise.race([exited, new Promise((_, reject) => setTimeout(() => reject(new Error("Owned journal locker did not close")), 10000).unref())]);
  assert.equal(current.exitCode, 0);
  journalLocker = null;
}
before(async () => {
  server = http.createServer((request, response) => {
    let bytes;
    if (request.url === "/catalog.json") {
      const catalog = JSON.parse(fs.readFileSync(path.join(inputsRoot, "catalog.json"), "utf8"));
      catalog.plugins[0].packageUrl = "http://127.0.0.1:" + server.address().port + "/packages/MaaFrameworkDriver/MaaFrameworkDriver-0.1.0.zip";
      bytes = Buffer.from(JSON.stringify(catalog));
    } else if (request.url === "/packages/MaaFrameworkDriver/MaaFrameworkDriver-0.1.0.zip") bytes = fs.readFileSync(inputs.package);
    else { response.writeHead(404); response.end(); return; }
    response.writeHead(200, { "Content-Length": bytes.length }); response.end(bytes);
  });
  server.listen(0, "127.0.0.1");
  await once(server, "listening");
  const base = "http://127.0.0.1:" + server.address().port;
  process.env.NEXUS_PLUGIN_CATALOG_URL = base + "/catalog.json";
  process.env.NEXUS_PLUGIN_PACKAGE_BASE_URL = base + "/packages/";
  await prepareRuntime();
  startRuntime();
  await waitForService();
  const store = await json("GET", "/api/plugins/store");
  assert.equal(store.available, true, JSON.stringify(store));
  assert.equal((await json("POST", "/api/plugins/store/maa-framework/install")).pending, true);
  await restart();
  const installed = (await json("GET", "/api/plugins")).find(item => item.name === "maa-framework");
  assert.equal(installed.version, "0.1.0");
  assert.equal(installed.managedByStore, true);
  await json("POST", "/api/plugins/maa-framework/enable");
  await restart();
  const frontend = await json("GET", "/api/plugin-runtime/frontend");
  assert.ok(JSON.stringify(frontend).includes("maa-framework"));
});
after(async () => {
  await closeLocker();
  await stopRuntime();
  await closeWindow();
  if (server) { server.closeAllConnections(); await new Promise(resolve => server.close(resolve)); }
  if (savedCatalog === undefined) delete process.env.NEXUS_PLUGIN_CATALOG_URL;
  else process.env.NEXUS_PLUGIN_CATALOG_URL = savedCatalog;
  if (savedPackages === undefined) delete process.env.NEXUS_PLUGIN_PACKAGE_BASE_URL;
  else process.env.NEXUS_PLUGIN_PACKAGE_BASE_URL = savedPackages;
  fs.writeFileSync(path.join(runtimeDir, "maa-vertical-evidence.json"), JSON.stringify({ inputs, evidence }, null, 2));
});

for (const controller of ["Win32", "Adb", "Win32HostLaunch", "Win32Recovery", "Win32Cancel"]) {
  test("Maa optional store package → independent " + controller + " binding → existing queue → actual native Agent/pretask → persistent history", async () => {
    await closeWindow();
    await closeLocker();
    const recoveryCase = controller === "Win32Recovery", cancelCase = controller === "Win32Cancel";
    const root = path.join(runtimeDir, "fixtures", "maa-" + controller.toLowerCase());
    fs.cpSync(path.join(inputsRoot, "standard-pi"), root, { recursive: true });
    fs.writeFileSync(path.join(root, ".nxp-native-fixture"), "owned-native-contract");
    fs.writeFileSync(path.join(root, ".nxp-maa-project-owned"), "owned-native-contract");
    fs.cpSync(inputs.nativeRoot, path.join(root, "native"), { recursive: true });
    fs.cpSync(path.join(inputsRoot, "NativeAgent"), path.join(root, "fixture-agent"), { recursive: true });
    fs.cpSync(path.join(inputsRoot, "NativeAdb"), path.join(root, "fixture-adb"), { recursive: true });
    const pi = JSON.parse(fs.readFileSync(path.join(root, "interface.json"), "utf8"));
    pi.agent = { child_exec: "fixture-agent/NexusPipeline.MaaTestAgent.exe", child_args: [] };
    pi.pretask = { exec: "fixture-agent/NexusPipeline.MaaTestAgent.exe", args: ["--pretask"], option: ["Mode"] };
    pi.option = { Mode: { cases: [{ name: "fixture" }] } };
    if (controller === "Win32") {
      pi.group = [{ name: "Daily" }]; pi.task[0].group = ["Daily"];
      pi.preset = [{ name: "Daily", task: [{ name: pi.task[0].name, option: { Mode: "fixture" } }] }];
      pi.task[0].option = ["Mode"];
    }
    if (controller === "Adb") { pi.controller[0].type = "Adb"; delete pi.controller[0].win32; }
    fs.writeFileSync(path.join(root, "interface.json"), JSON.stringify(pi));
    fs.writeFileSync(path.join(root, "resource", "pipeline", "sanity.json"),
      JSON.stringify({ Sanity: { recognition: "DirectHit", action: "Custom", custom_action: "FixtureAction", post_delay: 0 } }));
    const hostLaunch = controller === "Win32HostLaunch";
    fs.cpSync(path.join(inputsRoot, "NativeWindow"), path.join(root, "fixture-window"), { recursive: true });
    const windowExe = path.join(root, "fixture-window", "NexusPipeline.MaaTestWindow.exe");
    let handleBytes = "0", identity;
    if (!hostLaunch) {
      fixtureWindow = spawn(windowExe, [], { cwd: root, windowsHide: true, stdio: ["pipe", "pipe", "pipe"] });
      [handleBytes] = await once(fixtureWindow.stdout, "data");
      identity = readProcessIdentity(fixtureWindow.pid);
      assert.ok(identity?.startTime);
    }
    caseEnvironment = { NXP_MAA_FIXTURE: "owned-native-contract", NXP_MAA_FIXTURE_ROOT: root,
      NXP_MAA_FIXTURE_SCENARIO: cancelCase ? "cancel" : "success" };
    await restart();
    let profile = {
      schemaVersion: 1, profileId: "vertical-" + controller.toLowerCase(), revision: "", packageRoot: root,
      interfacePath: "interface.json", controller: "PC", resource: "Fixture", selectedTasks: ["Sanity"],
      options: {}, resourceOptions: {}, controllerOptions: {}, taskOptions: {}, language: "zh_cn",
      nativeDirectory: "native", nativeVersion: "v5.14.0", windowHandle: Number(String(handleBytes).trim()),
      windowProcessId: fixtureWindow?.pid || 0, windowStartedAtUtc: identity?.startTime || null, windowExecutable: windowExe,
      adbPath: path.join(root, "fixture-adb", "NexusPipeline.MaaTestAdb.exe"), adbSerial: "nxp-owned-fixture",
    };
    const gameArgs = '--owned-lifetime "' + root + '" --companion-window';
    if (hostLaunch) {
      fs.writeFileSync(path.join(root, "mxu.json"), JSON.stringify({ version: "1.0", instances: [{
        id: "imported-pc", controllerName: "PC", resourceName: "Fixture",
        preActions: [{ enabled: true, program: windowExe, args: gameArgs, waitForExit: false, skipIfRunning: true, useCmd: false }],
        tasks: [{ taskName: "Sanity", enabled: true, optionValues: {} }],
      }] }));
      const before = fs.readFileSync(path.join(root, "mxu.json"));
      const imported = await json("POST", "/api/plugin-api/maa-framework/import", {
        scriptId: "", userId: "", profileId: profile.profileId, profile,
        sourceKind: "mxu", sourcePath: "mxu.json", instanceId: "imported-pc",
      });
      profile = imported.profile;
      assert.equal(profile.hostLaunchRequired, true);
      assert.deepEqual(fs.readFileSync(path.join(root, "mxu.json")), before);
    }
    async function authorize(draft, scriptId = "", userId = "") {
      const body = { scriptId, userId, profileId: profile.profileId, profile: draft, expectedRevision: draft.revision || "" };
      const preview = await json("POST", "/api/plugin-api/maa-framework/preview", body);
      assert.equal(preview.project.tasks.length, 1);
      return json("POST", "/api/plugin-api/maa-framework/authorize",
        { ...body, confirmedFingerprint: preview.project.executionFingerprint });
    }
    await authorize(profile);
    const script = await json("POST", "/api/scripts", {
      name: "Maa native " + controller, rootPath: root, executionProviderId: "maa-framework",
      executionProviderConfigId: profile.profileId, launchGame: hostLaunch, forceCloseGame: false,
      ...(hostLaunch ? { gameMode: "pc", gameExe: windowExe, gameArgs, gameWaitSeconds: 1 } : {}),
      maxAttempts: 1, totalTimeoutMinutes: 5, logStallTimeoutMinutes: 1,
    });
    if (controller === "Win32") profile = await configureFromBrowser(script, root);
    const user = await createUserBinding(script.id, "Maa owned " + controller);
    const draft = await json("POST", "/api/plugin-api/maa-framework/profile",
      { scriptId: script.id, userId: user.id, profileId: profile.profileId });
    await authorize(draft, script.id, user.id);
    let independent, independentFixture;
    const tasks = [{ index: 0, scriptInstanceId: script.id }];
    if (recoveryCase || cancelCase) {
      independentFixture = makeFixture("maa-independent-" + controller.toLowerCase());
      writeBatch(independentFixture, [`echo independent>"${path.join(independentFixture.dir, "ran.marker")}"`,
        `echo MAA-INDEPENDENT-SUCCESS>>"${independentFixture.log}"`]);
      independent = await json("POST", "/api/scripts", { name: "Maa independent " + controller,
        rootPath: independentFixture.dir, mainExe: independentFixture.exe, configPath: independentFixture.cfg,
        logPath: independentFixture.log, successKeywords: "MAA-INDEPENDENT-SUCCESS", launchGame: false,
        gameExe: "C:\\Windows\\System32\\PING.EXE", maxAttempts: 1, totalTimeoutMinutes: 5, logStallTimeoutMinutes: 1 });
      await createUserBinding(independent.id, "Maa independent owned " + controller);
      if (recoveryCase) {
        const related = await json("POST", "/api/scripts", {
          name: "Maa related shared project", rootPath: root, executionProviderId: "maa-framework",
          executionProviderConfigId: profile.profileId, launchGame: false, forceCloseGame: false,
          maxAttempts: 1, totalTimeoutMinutes: 5, logStallTimeoutMinutes: 1,
        });
        const relatedUser = await createUserBinding(related.id, "Maa related owned recovery");
        const relatedDraft = await json("POST", "/api/plugin-api/maa-framework/profile",
          { scriptId: related.id, userId: relatedUser.id, profileId: profile.profileId });
        await authorize(relatedDraft, related.id, relatedUser.id);
        tasks.push({ index: tasks.length, scriptInstanceId: related.id });
      }
      tasks.push({ index: tasks.length, scriptInstanceId: independent.id });
    }
    const queue = await json("POST", "/api/queues", {
      name: "Maa owned queue " + controller, autoRunMode: "none", completionAction: "none",
      timeSets: [], notifyEnabled: false, tasks,
    });
    const journal = path.join(runtimeDir, "data", script.id, user.id, ".session");
    if (recoveryCase) {
      journalLocker = spawn(windowExe, ["--lock-journal", journal, root],
        { cwd: root, windowsHide: true, stdio: ["pipe", "pipe", "pipe"] });
    }
    if (hostLaunch) hostLaunchedWindow = { root };
    const dispatch = await json("POST", "/api/dispatch/queue", { queueId: queue.id, mode: "manual" });
    if (cancelCase) {
      assert.equal(await waitFor(() => fs.existsSync(path.join(root, "agent-action-ran.marker")), 20000, 25), true);
      assert.equal((await json("POST", "/api/cancel", { runId: dispatch.runId })).cancellation, "accepted");
    }
    assert.equal(await waitNoRunning(60000), true);
    const run = await json("GET", "/api/dispatch/" + dispatch.runId);
    if (recoveryCase) {
      assert.equal(fs.existsSync(path.join(root, "lock-ready.marker")), true);
      assert.equal(run.records.length, 3, JSON.stringify(run));
      assert.equal(run.records[1].resultCode, "run.not_started_quarantined");
      assert.equal(run.records[1].outcomes.executionOutcome, "not_started");
      assert.equal(run.records[2].scriptInstanceId, independent.id);
      assert.equal(run.records[2].status, "success");
      assert.equal(fs.existsSync(path.join(independentFixture.dir, "ran.marker")), true);
    } else if (cancelCase) {
      assert.equal(run.status, "cancelled");
      assert.equal(run.records.length, 1, JSON.stringify(run));
      assert.equal(run.records[0].status, "cancelled");
      assert.equal(fs.existsSync(path.join(independentFixture.dir, "ran.marker")), false);
      evidence.push({ controller, runId: dispatch.runId, cancelled: run });
      return;
    }
    const row = recoveryCase ? run.records[0] : await waitForHistory(script.id, 10000);
    assert.ok(row);
    const { record } = await json("GET", "/api/history/detail?id=" + row.id);
    if (hostLaunch && fs.existsSync(path.join(root, "window-identity.json")))
      hostLaunchedWindow = { root, ...JSON.parse(fs.readFileSync(path.join(root, "window-identity.json"), "utf8")) };
    assert.equal(record.status, "unverified", JSON.stringify(record));
    assert.equal(record.outcomes.engineStatus, "succeeded");
    assert.equal(record.outcomes.businessVerification, "unverified");
    assert.equal(record.outcomes.recoveryOutcome, recoveryCase ? "quarantined" : "not_required");
    assert.equal(record.taskReport.structuredEvidenceVersion, 1);
    assert.ok(record.taskReport.structuredEvidence.some(item => item.kind === "ready" && item.nativeVersion === "v5.14.0"));
    assert.equal(record.taskReport.finalTaskResults[0].engineStatus, "succeeded");
    assert.equal(record.taskReport.finalTaskResults[0].status, "unknown");
    assert.ok(fs.existsSync(path.join(root, "agent-action-ran.marker")));
    assert.equal(JSON.parse(fs.readFileSync(path.join(root, "pretask-evidence.json"), "utf8")).cwdMatches, true);
    assert.equal(fs.readFileSync(path.join(root, "agent-exited.marker"), "utf8"), "0");
    await restart();
    if (recoveryCase) {
      // The exact locked journal must survive startup; an independent run still works.
      assert.equal(fs.existsSync(journal) || fs.existsSync(journal + ".bak"), true);
      const blocked = await api("POST", "/api/dispatch/script", { scriptId: script.id, mode: "manual" });
      assert.equal(blocked.status, 409);
      const conflict = await blocked.json();
      assert.equal(conflict.code, "execution_resource_in_use");
      assert.ok(conflict.args.resource, JSON.stringify(conflict));
      const resumed = await json("POST", "/api/dispatch/script", { scriptId: independent.id, mode: "manual" });
      assert.equal(await waitNoRunning(30000), true);
      const resumedRun = await json("GET", "/api/dispatch/" + resumed.runId);
      fs.writeFileSync(path.join(root, "locked-restart-independent-evidence.json"), JSON.stringify(resumedRun, null, 2));
      assert.equal(resumedRun.records[0].status, "success", JSON.stringify(resumedRun));
      await closeLocker();
      await restart();
      assert.equal(fs.existsSync(journal) || fs.existsSync(journal + ".bak"), false);
    }
    const { record: persisted } = await json("GET", "/api/history/detail?id=" + row.id);
    assert.deepEqual(persisted.taskReport, record.taskReport);
    evidence.push({ controller, runId: dispatch.runId, scriptId: script.id, userId: user.id, queueId: queue.id,
      history: record, nativeAgent: JSON.parse(fs.readFileSync(path.join(root, "agent-evidence.json"), "utf8")) });
  });
}
