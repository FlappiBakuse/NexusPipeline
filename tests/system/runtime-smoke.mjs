import test, { after, before } from "node:test";
import assert from "node:assert/strict";
import { spawn, spawnSync } from "node:child_process";
import http from "node:http";
import net from "node:net";
import fs from "node:fs";
import path from "node:path";
import {
  api,
  fetchWithTimeout,
  isNormalIntegrity,
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
  waitForService,
  writeBatch,
} from "./runtime-helper.mjs";

const enabled = process.env.NEXUS_SYSTEM_SMOKE === "1";
const skipReason = process.env.NEXUS_SYSTEM_SMOKE !== "1"
  ? "设置 NEXUS_SYSTEM_SMOKE=1 后运行"
  : "System Smoke 需要普通权限 Test Host";
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
  assert.ok(isNormalIntegrity(), "System Smoke 必须在普通权限（Medium Integrity）终端运行");
  prepareRuntime();
  startRuntime();
  await waitForService();
});

after(async () => {
  if (enabled) await stopRuntime();
});

test("release binary 启动并提供 status API", { skip }, async () => {
  const response = await fetchWithTimeout(serviceUrl() + "api/status");
  assert.equal(response.status, 200);
  const status = await response.json();
  assert.equal(status.service, "NexusPipeline");
  assert.equal(status.controlApiVersion, 1);
  assert.match(status.version, /^\d+\.\d+\.\d+$/);
  assert.ok(status.actualPort >= 1024 && status.actualPort <= 65535);
  assert.ok(Array.isArray(status.running));
});

test("普通权限 Test Host 可在无 URLACL 的 loopback 随机端口提供 status API", { skip, concurrency: false }, async () => {
  assert.ok(isNormalIntegrity(), "HTTP Probe 必须在普通权限（Medium Integrity）终端运行");
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
      // 仍由 Test Host 的托管 transport 直接验证普通权限路径。
      process.stderr.write("ℹ 普通权限无法读取 HTTP.sys URLACL，继续验证随机 loopback 绑定。\n");
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
  assert.equal(payload.limits.maxScripts, 25);
  assert.equal(payload.limits.maxQueues, 10);
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
    const addUser = await api("POST", `/api/scripts/${encodeURIComponent(scriptId)}/users`, {
      name: userName,
      enabled: true,
    });
    assert.equal(addUser.status, 200, `添加重启测试用户失败：HTTP ${addUser.status} ${await addUser.text()}`);

    const restart = await api("POST", "/api/settings/restart");
    const restartBody = await restart.text();
    assert.equal(restart.status, 200, `提交重启失败：HTTP ${restart.status} ${restartBody}`);
    const restartPayload = JSON.parse(restartBody);
    assert.equal(restartPayload.ok, true);

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

    await new Promise(resolve => setTimeout(resolve, 2500));
    await waitForService();
  } finally {
    if (scriptId) await api("DELETE", `/api/scripts/${encodeURIComponent(scriptId)}`);
    await stopRuntime();
    startRuntime(["web"]);
    await waitForService();
  }
});

test("System Smoke runtime 位于隔离目录", { skip }, () => {
  assert.ok(path.resolve(runtimeDir).startsWith(path.resolve(projectRoot, "tests")));
  assert.ok(fs.existsSync(path.join(runtimeDir, "nexus-pipeline.exe")));
});
