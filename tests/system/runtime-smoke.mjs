import test, { after, before } from "node:test";
import assert from "node:assert/strict";
import { spawn, spawnSync } from "node:child_process";
import http from "node:http";
import net from "node:net";
import fs from "node:fs";
import path from "node:path";
import {
  api,
  createUserBinding,
  deleteScript,
  isAdminMode,
  executionMode,
  fetchWithTimeout,
  isAdministrator,
  isRuntimeAlive,
  makeFixture,
  prepareRuntime,
  projectRoot,
  runtimeExe,
  runtimeDir,
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
  if (isAdminMode) {
    assert.ok(isAdministrator(), "管理员 System Smoke 必须在 Administrator / High Integrity 终端运行");
  }
  prepareRuntime();
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
  assert.equal(status.service, "NexusPipeline");
  assert.equal(status.controlApiVersion, 1);
  assert.match(status.version, /^\d+\.\d+\.\d+$/);
  assert.ok(status.actualPort >= 1024 && status.actualPort <= 65535);
  // 重启恢复协议依赖实例标识区分重启前后的服务；普通启动的进程没有交接标识。
  assert.match(status.instanceId, /^[0-9a-f]{32}$/);
  assert.equal(status.restartHandoffId, "");
  assert.ok(Array.isArray(status.running));
});

test(`${executionMode === "admin" ? "管理员生产 release" : "Codex Test Host"} 可在无 URLACL 的 loopback 随机端口提供 status API`, { skip, concurrency: false }, async () => {
  if (isAdminMode) {
    assert.ok(isAdministrator(), "管理员 HTTP Probe 必须在 Administrator / High Integrity 终端运行");
  }
  const settingsPath = path.join(runtimeDir, "config", "settings.json");
  const originalSettings = fs.existsSync(settingsPath)
    ? fs.readFileSync(settingsPath, "utf8")
    : null;
  const blocker = net.createServer();
  await new Promise((resolve, reject) => {
    blocker.once("error", reject);
    blocker.listen(0, "127.0.0.1", resolve);
  });
  const port = blocker.address().port;
  await new Promise((resolve, reject) => blocker.close(error => error ? reject(error) : resolve()));
  const settings = originalSettings ? JSON.parse(originalSettings.replace(/^\uFEFF/u, "")) : {};
  settings.WebPort = port;
  fs.writeFileSync(settingsPath, JSON.stringify(settings, null, 2), "utf8");
  try {
    const acl = spawnSync("netsh", ["http", "show", "urlacl"], { encoding: "utf8", windowsHide: true });
    const aclOutput = `${acl.stdout || ""}\n${acl.stderr || ""}`;
    if (acl.status === 0) {
      assert.doesNotMatch(
        aclOutput,
        new RegExp(`https?://(?:127\\.0\\.0\\.1|\\+):${port}/`, "i"),
        `随机探针端口已有 URLACL：${aclOutput}`,
      );
    } else {
      // HTTP.sys URLACL 查询本身可能要求管理员句柄；随机 loopback 绑定
      // 仍由当前模式的宿主 transport 直接验证。
      process.stderr.write("ℹ 当前句柄无法读取 HTTP.sys URLACL，继续验证随机 loopback 绑定。\n");
    }
    await stopRuntime();
    startRuntime(["web"]);
    const probeUrl = `http://127.0.0.1:${port}/`;
    await waitForService(probeUrl, 30000);
    const response = await fetchWithTimeout(`${probeUrl}api/status`);
    assert.equal(response.status, 200);
    const status = await response.json();
    assert.equal(status.service, "NexusPipeline");
    assert.equal(status.actualPort, port);
  } finally {
    await stopRuntime();
    if (originalSettings === null) {
      fs.rmSync(settingsPath, { force: true });
    } else {
      fs.writeFileSync(settingsPath, originalSettings, "utf8");
    }
    startRuntime();
    await waitForService();
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

test("doctor 与 dry-run 只读取当前快照并返回可审计结果", { skip, concurrency: false }, async () => {
  const diagnostics = await api("GET", "/api/diagnostics");
  const diagnosticsText = await diagnostics.text();
  assert.equal(diagnostics.status, 200, `诊断快照失败：HTTP ${diagnostics.status} ${diagnosticsText}`);
  const snapshot = JSON.parse(diagnosticsText);
  assert.equal(snapshot.schemaVersion, 2);
  assert.match(snapshot.overallStatus, /^(pass|warn|fail)$/);
  assert.ok(Array.isArray(snapshot.checks));
  for (const id of [
    "host.admin-integrity",
    "host.install-write",
    "web.listener",
    "mcp.listener",
    "update.transaction",
    "plugin.runtime",
    "plugin.pending",
    "config.recovery",
    "scheduler.state",
    "execution.state",
    "dependency.python",
    "dependency.adb",
  ]) {
    assert.ok(snapshot.checks.some(check => check.id === id), `缺少稳定诊断项：${id}`);
  }
  assert.ok(snapshot.checks.every(check => /^(pass|warn|fail|skipped)$/.test(check.status)));

  const doctor = runCli(["doctor", "--json"]);
  assert.equal(doctor.status, 0, `${doctor.stdout}\n${doctor.stderr}`);
  const doctorLines = doctor.stdout.trim().split(/\r?\n/).filter(Boolean);
  assert.equal(doctorLines.length, 1, `doctor stdout: ${doctor.stdout}`);
  const doctorPayload = JSON.parse(doctorLines[0]);
  assert.equal(doctorPayload.ok, true);
  assert.equal(doctorPayload.code, "ok");
  assert.equal(doctorPayload.data.schemaVersion, 2);

  const fixture = makeFixture("explain");
  writeBatch(fixture, [`echo explain-ok>>"${fixture.log}"`]);
  const userName = `Explain User ${Date.now()}`;
  let scriptId = "";
  let queueId = "";
  try {
    const created = await api("POST", "/api/scripts", {
      name: `Explain Script ${Date.now()}`,
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
    const createdText = await created.text();
    assert.equal(created.status, 200, `创建 explain 脚本失败：HTTP ${created.status} ${createdText}`);
    const script = JSON.parse(createdText);
    scriptId = script.id;
    await createUserBinding(scriptId, userName);

    const scriptExplain = await api("POST", "/api/dispatch/explain/script", {
      scriptId,
      userName,
    });
    const scriptExplainText = await scriptExplain.text();
    assert.equal(scriptExplain.status, 200, `脚本 explain 失败：HTTP ${scriptExplain.status} ${scriptExplainText}`);
    const scriptExplainBody = JSON.parse(scriptExplainText);
    assert.equal(scriptExplainBody.ok, true);
    assert.equal(scriptExplainBody.result.kind, "script");
    assert.equal(scriptExplainBody.result.targetId, scriptId);
    assert.equal(scriptExplainBody.result.admissible, true);
    assert.equal(scriptExplainBody.result.users[0].status, "ready");
    assert.equal(scriptExplainBody.result.tasks[0].userCount, 1);

    const cliExplain = runCli(["run", "script", scriptId, "--dry-run", "--user", userName, "--json"]);
    assert.equal(cliExplain.status, 0, `${cliExplain.stdout}\n${cliExplain.stderr}`);
    const cliExplainLines = cliExplain.stdout.trim().split(/\r?\n/).filter(Boolean);
    assert.equal(cliExplainLines.length, 1, `dry-run stdout: ${cliExplain.stdout}`);
    const cliExplainPayload = JSON.parse(cliExplainLines[0]);
    assert.equal(cliExplainPayload.ok, true);
    assert.equal(cliExplainPayload.data.result.targetId, scriptId);

    const queueCreated = await api("POST", "/api/queues", {
      name: `Explain Queue ${Date.now()}`,
      autoRunMode: "none",
      completionAction: "none",
      tasks: [{ index: 0, scriptInstanceId: scriptId }],
      timeSets: [],
      notifyEnabled: false,
    });
    const queueCreatedText = await queueCreated.text();
    assert.equal(queueCreated.status, 200, `创建 explain 队列失败：HTTP ${queueCreated.status} ${queueCreatedText}`);
    queueId = JSON.parse(queueCreatedText).id;
    const queueExplain = await api("POST", "/api/dispatch/explain/queue", { queueId });
    const queueExplainText = await queueExplain.text();
    assert.equal(queueExplain.status, 200, `队列 explain 失败：HTTP ${queueExplain.status} ${queueExplainText}`);
    const queueExplainBody = JSON.parse(queueExplainText);
    assert.equal(queueExplainBody.ok, true);
    assert.equal(queueExplainBody.result.kind, "queue");
    assert.equal(queueExplainBody.result.targetId, queueId);
    assert.equal(queueExplainBody.result.admissible, true);
    assert.ok(queueExplainBody.result.tasks.length >= 1);
  } finally {
    if (queueId) await api("DELETE", `/api/queues/${encodeURIComponent(queueId)}`);
    await deleteScript(scriptId);
  }

  const bundlePath = path.join(runtimeDir, "diagnostics", "system-smoke-support.zip");
  try {
    const exported = await api("POST", "/api/diagnostics/export", { outputPath: bundlePath });
    const exportedText = await exported.text();
    assert.equal(exported.status, 200, `诊断包导出失败：HTTP ${exported.status} ${exportedText}`);
    const exportBody = JSON.parse(exportedText);
    assert.equal(exportBody.ok, true);
    assert.equal(exportBody.path, bundlePath);
    assert.ok(exportBody.sizeBytes > 0 && exportBody.sizeBytes <= 8 * 1024 * 1024);
    assert.equal(fs.statSync(bundlePath).isFile(), true);
  } finally {
    fs.rmSync(bundlePath, { force: true });
  }
});

test("通知测试失败通过非 2xx 与稳定错误码传递到 CLI", { skip, concurrency: false }, async () => {
  const settings = await api("PUT", "/api/settings", {
    webhookEnabled: false,
    smtpEnabled: false,
  });
  assert.equal(settings.status, 200, `关闭通知渠道失败：HTTP ${settings.status} ${await settings.text()}`);

  const webResponse = await api("POST", "/api/settings/test");
  assert.equal(webResponse.status, 502);
  const webPayload = await webResponse.json();
  assert.equal(webPayload.ok, false);
  assert.equal(webPayload.code, "notification_test_failed");

  const cli = runCli(["settings", "test", "--json"]);
  assert.equal(cli.status, 7, `${cli.stdout}\n${cli.stderr}`);
  const lines = cli.stdout.trim().split(/\r?\n/).filter(Boolean);
  assert.equal(lines.length, 1, `CLI stdout: ${cli.stdout}`);
  const cliPayload = JSON.parse(lines[0]);
  assert.equal(cliPayload.ok, false);
  assert.equal(cliPayload.code, "notification_test_failed");
});

test("通知测试允许超过默认 5 秒的合法 Webhook 请求完成", { skip, concurrency: false }, async () => {
  let requestCount = 0;
  const server = http.createServer((_request, response) => {
    requestCount++;
    setTimeout(() => {
      response.writeHead(200, { "Content-Type": "application/json" });
      response.end("{}");
    }, 6000);
  });
  await new Promise((resolve, reject) => {
    server.once("error", reject);
    server.listen(0, resolve);
  });
  const port = server.address().port;
  try {
    const settings = await api("PUT", "/api/settings", {
      webhookEnabled: true,
      smtpEnabled: false,
      webhookType: "generic",
      webhookTemplate: "{\"text\":{text}}",
      webhookTimeout: 10,
      secretKey: "webhookUrl",
      secretValue: `http://127.0.0.1:${port}/delayed-webhook`,
    });
    assert.equal(settings.status, 200, `配置延迟 Webhook 失败：HTTP ${settings.status} ${await settings.text()}`);

    const startedAt = Date.now();
    const cli = await runCliAsync(["settings", "test", "--json"], "", 20000);
    const elapsed = Date.now() - startedAt;
    assert.equal(cli.status, 0, `${cli.stdout}\n${cli.stderr}\nwebhookRequests=${requestCount}`);
    assert.ok(elapsed >= 5500, `延迟请求未实际经过服务端等待：${elapsed}ms`);
    const lines = cli.stdout.trim().split(/\r?\n/).filter(Boolean);
    assert.equal(lines.length, 1, `CLI stdout: ${cli.stdout}`);
    const payload = JSON.parse(lines[0]);
    assert.equal(payload.ok, true);
  } finally {
    await api("PUT", "/api/settings", {
      webhookEnabled: false,
      smtpEnabled: false,
      webhookTemplate: "",
      secretKey: "webhookUrl",
      secretValue: "",
    });
    await new Promise((resolve, reject) => server.close(error => error ? reject(error) : resolve()));
  }
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

test("配置端口被占用时服务回退到下一个可用端口", { skip }, async () => {
  let blocker = null;
  let blockedPort = null;
  const settingsPath = path.join(runtimeDir, "config", "settings.json");
  const originalSettings = fs.existsSync(settingsPath)
    ? fs.readFileSync(settingsPath, "utf8")
    : null;
  const failures = [];
  try {
    await stopRuntime();
    blocker = net.createServer();
    await new Promise((resolve, reject) => {
      blocker.once("error", reject);
      blocker.listen(0, "127.0.0.1", resolve);
    });
    blockedPort = blocker.address().port;
    const settings = originalSettings ? JSON.parse(originalSettings.replace(/^\uFEFF/, "")) : {};
    settings.WebPort = blockedPort;
    fs.writeFileSync(settingsPath, JSON.stringify(settings, null, 2), "utf8");

    startRuntime(["web"]);
    await waitForService(null, 30000);
    const response = await fetchWithTimeout(serviceUrl() + "api/status");
    assert.equal(response.status, 200);
    const status = await response.json();
    assert.notEqual(status.actualPort, blockedPort);
  } catch (error) {
    failures.push(`端口回退启动阶段失败：${error.stack || error.message}\n${runtimeDiagnostic()}`);
  } finally {
    try {
    await stopRuntime();
    } catch (error) {
      failures.push(`端口回退清理阶段停止 runtime 失败：${error.stack || error.message}`);
    }
    try {
      if (blocker?.listening) {
        await new Promise((resolve, reject) => {
          blocker.once("error", reject);
          blocker.close(resolve);
        });
      }
    } catch (error) {
      failures.push(`端口回退清理阶段释放测试占用端口失败：${error.stack || error.message}`);
    }
    try {
      if (originalSettings === null) {
        fs.rmSync(settingsPath, { force: true });
      } else {
        fs.writeFileSync(settingsPath, originalSettings, "utf8");
      }
      startRuntime();
      await waitForService();
    } catch (error) {
      failures.push(`端口回退后的默认 runtime 恢复失败：${error.stack || error.message}\n${runtimeDiagnostic()}`);
    }
  }
  if (failures.length > 0) {
    throw new Error(failures.join("\n\n"));
  }
});

test("非法 limits 配置触发 fatal startup 并可恢复", { skip }, async () => {
  await stopRuntime();
  const limitsPath = path.join(runtimeDir, "config", "limits.json");
  fs.mkdirSync(path.dirname(limitsPath), { recursive: true });
  fs.writeFileSync(limitsPath, "{\"MaxScripts\":0}", "utf8");
  const child = startRuntime();
  const exitCode = child.once
    ? await new Promise(resolve => child.once("exit", code => resolve(code)))
    : await new Promise(resolve => setTimeout(() => resolve(isRuntimeAlive(child.pid) ? 0 : 1), 1000));
  assert.notEqual(exitCode, 0);
  fs.rmSync(limitsPath, { force: true });
  startRuntime();
  await waitForService();
});

test("重启接受后立即冻结旧服务的运行与配置写入准入", { skip, concurrency: false }, async () => {
  await stopRuntime();
  startRuntime(["service"]);
  await waitForService();
  const fixture = makeFixture("restart-maintenance");
  writeBatch(fixture, ["echo restart-maintenance>>\"" + fixture.log + "\""]);
  let scriptId = "";
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

    const restart = await api("POST", "/api/settings/restart");
    const restartBody = await restart.text();
    assert.equal(restart.status, 200, `提交重启失败：HTTP ${restart.status} ${restartBody}`);
    const restartPayload = JSON.parse(restartBody);
    assert.equal(restartPayload.ok, true);
    // 交接标识由旧实例生成并传给新实例，前端据此确认应答来自本次重启。
    assert.match(restartPayload.handoffId, /^[0-9a-f]{32}$/);
    assert.match(restartPayload.instanceId, /^[0-9a-f]{32}$/);
    const previousInstanceId = restartPayload.instanceId;

    const run = await api("POST", "/api/dispatch/script", { scriptId, mode: "manual", userName });
    const runBody = await run.text();
    assert.equal(run.status, 409, `维护期间运行未被拒绝：HTTP ${run.status} ${runBody}`);
    const runPayload = JSON.parse(runBody);
    assert.equal(runPayload.code, "host_maintenance");

    const settings = await api("PUT", "/api/settings", { logLevel: "info" });
    const settingsBody = await settings.text();
    assert.equal(settings.status, 409, `维护期间设置写入未被拒绝：HTTP ${settings.status} ${settingsBody}`);
    const settingsPayload = JSON.parse(settingsBody);
    assert.equal(settingsPayload.code, "host_maintenance");

    const configuredPort = Number(restartPayload.newPort);
    const candidatePorts = Number.isInteger(configuredPort) && configuredPort >= 1024
      ? Array.from({ length: 20 }, (_, offset) => configuredPort + offset)
        .filter(port => port <= 65535)
      : [];
    const restarted = await waitForRestartedService({
      previousInstanceId,
      expectedHandoffId: restartPayload.handoffId,
      candidatePorts,
      timeoutMs: 30000,
    });
    assert.equal(restarted.service, "NexusPipeline");
    assert.notEqual(restarted.instanceId, previousInstanceId, "重启后必须由新的进程实例提供服务");
    assert.equal(restarted.restartHandoffId, restartPayload.handoffId, "新实例必须携带本次重启的交接标识");
  } finally {
    if (scriptId) await deleteScript(scriptId);
    await stopRuntime();
    startRuntime(["web"]);
    await waitForService();
  }
});

test("System Smoke runtime 位于隔离目录", { skip }, () => {
  assert.ok(path.resolve(runtimeDir).startsWith(path.resolve(projectRoot, "tests")));
  assert.ok(fs.existsSync(path.join(runtimeDir, "nexus-pipeline.exe")));
});
