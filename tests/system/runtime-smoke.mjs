import { controlServiceName } from "../../tools/installation-generation.mjs";
import test, { after, before } from "node:test";
import assert from "node:assert/strict";
import { spawn, spawnSync } from "node:child_process";
import http from "node:http";
import net from "node:net";
import fs from "node:fs";
import path from "node:path";
import { readRunMarker, resolveTestRunRoot } from "../support/test-runtime.mjs";
import {
  api,
  createUserBinding,
  deleteScript,
  executionMode,
  fetchWithTimeout,
  isRuntimeAlive,
  makeFixture,
  prepareRuntime,
  projectRoot,
  runtimeExe,
  runtimeDir,
  runId,
  runMarkerPath,
  runtimeDiagnostic,
  startRuntime,
  stopRuntime,
  serviceUrl,
  waitForRestartedService,
  waitForService,
  writeBatch,
} from "./runtime-helper.mjs";

const enabled = process.env.NEXUS_SYSTEM_SMOKE === "1";
const skipReason = process.env.NEXUS_SYSTEM_SMOKE !== "1"
  ? "设置 NEXUS_SYSTEM_SMOKE=1 后运行"
  : "";
const skip = enabled ? false : skipReason;

function runCli(args, input = "", timeout = 10000) {
  const localNoProxy = [process.env.NO_PROXY, process.env.no_proxy, "127.0.0.1", "localhost"]
    .filter(Boolean)
    .join(",");
  return spawnSync(runtimeExe, args, {
    cwd: runtimeDir,
    input,
    encoding: "utf8",
    env: {
      ...process.env,
      NEXUS_SYSTEM_SMOKE: "1",
      NO_PROXY: localNoProxy,
      no_proxy: localNoProxy,
    },
    timeout,
    windowsHide: true,
  });
}

function runCliAsync(args, input = "", timeout = 20000) {
  const localNoProxy = [process.env.NO_PROXY, process.env.no_proxy, "127.0.0.1", "localhost"]
    .filter(Boolean)
    .join(",");
  return new Promise((resolve, reject) => {
    const child = spawn(runtimeExe, args, {
      cwd: runtimeDir,
      env: {
        ...process.env,
        NEXUS_SYSTEM_SMOKE: "1",
        NO_PROXY: localNoProxy,
        no_proxy: localNoProxy,
      },
      windowsHide: true,
      stdio: ["pipe", "pipe", "pipe"],
    });
    let stdout = "";
    let stderr = "";
    const timer = setTimeout(() => {
      child.kill();
      reject(new Error(`CLI 异步测试超时（${timeout}ms）：${args.join(" ")}`));
    }, timeout);
    child.stdout.on("data", chunk => { stdout += chunk.toString(); });
    child.stderr.on("data", chunk => { stderr += chunk.toString(); });
    child.once("error", error => {
      clearTimeout(timer);
      reject(error);
    });
    child.once("close", (status, signal) => {
      clearTimeout(timer);
      resolve({ status, signal, stdout, stderr });
    });
    if (input) child.stdin.write(input);
    child.stdin.end();
  });
}

before(async () => {
  if (!enabled) return;
  await prepareRuntime();
  startRuntime();
  await waitForService();
});

after(async () => {
  if (enabled) await stopRuntime();
});

test("当前隔离宿主启动并提供 status API", { skip }, async () => {
  const response = await fetchWithTimeout(serviceUrl() + "api/status");
  assert.equal(response.status, 200);
  const status = await response.json();
  assert.equal(status.service, controlServiceName);
  assert.equal(status.controlApiVersion, 1);
  assert.match(status.version, /^(?:0|[1-9]\d*)\.(?:0|[1-9]\d*)\.(?:0|[1-9]\d*)(?:-(?:beta|rc)\.(?:0|[1-9]\d*))?$/);
  assert.ok(status.actualPort >= 1024 && status.actualPort <= 65535);
  // 重启恢复协议依赖实例标识区分重启前后的服务；普通启动的进程没有交接标识。
  assert.match(status.instanceId, /^[0-9a-f]{32}$/);
  assert.equal(status.restartHandoffId, "");
  assert.ok(Array.isArray(status.running));
});

test("SSE 事件端点返回首帧 stream.ready 与稳定包络", { skip }, async () => {
  const response = await fetchWithTimeout(serviceUrl() + "api/events", {}, 5000);
  assert.equal(response.status, 200);
  assert.match(response.headers.get("content-type") || "", /text\/event-stream/i);
  assert.equal(response.headers.get("cache-control"), "no-cache, no-store, no-transform");
  const reader = response.body?.getReader();
  assert.ok(reader);
  const timeout = new Promise((_, reject) => setTimeout(() => reject(new Error("SSE 首帧等待超时")), 5000));
  try {
    const first = await Promise.race([reader.read(), timeout]);
    assert.equal(first.done, false);
    const text = new TextDecoder().decode(first.value);
    assert.match(text, /event: stream\.ready/);
    const dataLine = text.split(/\r?\n/).find(line => line.startsWith("data: "));
    assert.ok(dataLine);
    const envelope = JSON.parse(dataLine.slice("data: ".length));
    assert.equal(envelope.schemaVersion, 1);
    assert.equal(typeof envelope.sequence, "number");
    assert.equal(typeof envelope.timestamp, "string");
    assert.equal(typeof envelope.data, "object");
  } finally {
    await reader.cancel();
  }
});

test("正式 CLI 的 --json 输出保持单 envelope 与稳定退出码", { skip }, () => {
  for (const args of [["status", "--json"], ["script", "list", "--json"], ["run", "list", "--json"]]) {
    const result = runCli(args);
    assert.equal(result.status, 0, `${args.join(" ")} error: ${result.error?.message || ""}; stderr: ${result.stderr}`);
    const lines = result.stdout.trim().split(/\r?\n/).filter(Boolean);
    assert.equal(lines.length, 1, `${args.join(" ")} stdout: ${result.stdout}`);
    const payload = JSON.parse(lines[0]);
    assert.equal(payload.ok, true);
    assert.equal(payload.code, "ok");
    assert.ok(Object.hasOwn(payload, "data"));
  }

  const invalid = runCli(["script", "create", "--json", "--file", "-"], "{invalid json");
  assert.equal(invalid.status, 2, invalid.stdout + invalid.stderr);
  const failure = JSON.parse(invalid.stdout.trim());
  assert.equal(failure.ok, false);
  assert.equal(failure.code, "validation_error");
});

test("status 与 limits API 在同一服务实例可用", { skip }, async () => {
  const response = await fetchWithTimeout(serviceUrl() + "api/limits");
  assert.equal(response.status, 200);
  const payload = await response.json();
  assert.equal(payload.limits.maxScripts, 50);
  assert.equal(payload.limits.maxQueues, 50);
  assert.equal(payload.limits.maxRunDays, 365);
  assert.equal(payload.limits.maxSuccessfulRunsPerDay, 10);
});

test("普通运行状态集中在 .nxp，安装根不再散落运行标记", { skip }, () => {
  const internalDir = path.join(runtimeDir, ".nxp");
  const runtimeStateDir = path.join(internalDir, "runtime");
  const stateDir = path.join(internalDir, "state");
  assert.equal(fs.existsSync(path.join(runtimeDir, "service.pid")), false);
  assert.equal(fs.existsSync(path.join(runtimeDir, "web.port")), false);
  assert.equal(fs.existsSync(path.join(runtimeDir, "scheduler-state.json")), false);
  assert.equal(fs.existsSync(path.join(runtimeStateDir, "service.pid")), true);
  assert.equal(fs.existsSync(path.join(runtimeStateDir, "web.port")), true);
  assert.equal(fs.existsSync(stateDir), true);
});

test("重启接受后立即冻结旧服务的运行与配置写入准入", { skip, concurrency: false }, async () => {
  await stopRuntime();
  startRuntime(["service"]);
  await waitForService();
  const fixture = makeFixture("restart-maintenance");
  writeBatch(fixture, ["echo restart-maintenance>>\"" + fixture.log + "\""]);
  let scriptId = "";
  let restartOptions = null;
  let restartedConfirmed = false;
  let failure = null;
  try {
    const create = await api("POST", "/api/scripts", {
      name: `Restart Maintenance ${Date.now()}`,
      rootPath: fixture.dir,
      mainExe: fixture.exe,
      configPath: fixture.cfg,
      logPath: fixture.log,
      gameExe: "C:\\Windows\\System32\\PING.EXE",
      gameArgs: "127.0.0.1 -n 1",
      launchGame: false,
      maxAttempts: 1,
      logStallTimeoutMinutes: 5,
      totalTimeoutMinutes: 5,
      autoUpdateConfig: false,
    });
    const createBody = await create.text();
    assert.equal(create.status, 200, `创建重启测试脚本失败：HTTP ${create.status} ${createBody}`);
    scriptId = JSON.parse(createBody).id;
    const userName = "Restart Maintenance User";
    await createUserBinding(scriptId, userName);
    const beforeSettingsResponse = await api("GET", "/api/settings");
    assert.equal(beforeSettingsResponse.status, 200);
    const beforeLogLevel = (await beforeSettingsResponse.json()).settings.logLevel;
    const attemptedLogLevel = beforeLogLevel === "debug" ? "warn" : "debug";

    const oldUrl = serviceUrl();
    const restart = await api("POST", "/api/settings/restart");
    const restartBody = await restart.text();
    assert.equal(restart.status, 200, `提交重启失败：HTTP ${restart.status} ${restartBody}`);
    const restartPayload = JSON.parse(restartBody);
    assert.equal(restartPayload.ok, true);
    // 交接标识由旧实例生成并传给新实例，前端据此确认应答来自本次重启。
    assert.match(restartPayload.handoffId, /^[0-9a-f]{32}$/);
    assert.match(restartPayload.instanceId, /^[0-9a-f]{32}$/);
    const previousInstanceId = restartPayload.instanceId;
    const configuredPort = Number(restartPayload.newPort);
    const candidatePorts = Number.isInteger(configuredPort) && configuredPort >= 1024
      ? Array.from({ length: 20 }, (_, offset) => configuredPort + offset)
        .filter(port => port <= 65535)
      : [];
    restartOptions = {
      previousInstanceId,
      expectedHandoffId: restartPayload.handoffId,
      candidatePorts,
      timeoutMs: 30000,
    };

    const assertOldServiceRejects = async (method, route, body) => {
      let response;
      try {
        response = await api(method, route, body, oldUrl);
      } catch (error) {
        // 监听器关闭可拒绝新连接，也可重置已经建立但尚未返回的连接。
        assert.ok(["ECONNREFUSED", "ECONNRESET"].includes(error?.cause?.code),
          `旧服务关闭时出现非预期连接错误：${error?.message}`);
        return;
      }
      const responseBody = await response.text();
      assert.equal(response.status, 409, `维护期间写入未被拒绝：HTTP ${response.status} ${responseBody}`);
      assert.equal(JSON.parse(responseBody).code, "host_maintenance");
    };
    await assertOldServiceRejects("POST", "/api/dispatch/script", { scriptId, mode: "manual", userName });
    await assertOldServiceRejects("PUT", "/api/settings", { logLevel: attemptedLogLevel });

    const restarted = await waitForRestartedService(restartOptions);
    restartedConfirmed = true;
    assert.equal(restarted.service, controlServiceName);
    assert.notEqual(restarted.instanceId, previousInstanceId, "重启后必须由新的进程实例提供服务");
    assert.equal(restarted.restartHandoffId, restartPayload.handoffId, "新实例必须携带本次重启的交接标识");
    const afterSettingsResponse = await api("GET", "/api/settings");
    assert.equal(afterSettingsResponse.status, 200);
    assert.equal((await afterSettingsResponse.json()).settings.logLevel, beforeLogLevel,
      "旧服务关闭期间不得保存设置写入");
    assert.equal(fs.existsSync(fixture.log), false, "旧服务关闭期间不得执行新派发的脚本");
  } catch (error) {
    failure = error;
  }
  let cleanupFailure = null;
  if (restartOptions && !restartedConfirmed) {
    try {
      await waitForRestartedService(restartOptions);
      restartedConfirmed = true;
    } catch (error) {
      cleanupFailure = error;
    }
  }
  if (scriptId && (!restartOptions || restartedConfirmed)) {
    try { await deleteScript(scriptId); } catch (error) { cleanupFailure ??= error; }
  }
  try {
    await stopRuntime();
    startRuntime(["web"]);
    await waitForService();
  } catch (error) {
    cleanupFailure ??= error;
  }
  if (failure) throw failure;
  if (cleanupFailure) throw cleanupFailure;
});

test("System Smoke runtime 位于隔离目录", { skip }, () => {
  const runRoot = resolveTestRunRoot(projectRoot, runId);
  const relative = path.relative(runRoot, runtimeDir);
  assert.ok(relative && !relative.startsWith("..") && !path.isAbsolute(relative));
  const marker = readRunMarker(runMarkerPath);
  assert.equal(marker?.runId, runId);
  assert.equal(path.resolve(marker?.executablePath || ""), path.resolve(runtimeExe));
  assert.ok(fs.existsSync(path.join(runtimeDir, "nexus-pipeline.exe")));
});
