import test, { after, before } from "node:test";
import assert from "node:assert/strict";
import fs from "node:fs";
import http from "node:http";
import net from "node:net";
import path from "node:path";
import {
  api,
  deleteScript,
  isAdminMode,
  fetchWithTimeout,
  isAdministrator,
  makeFixture,
  prepareRuntime,
  runtimeDir,
  runtimeOutput,
  serviceUrl,
  systemWebPort,
  startRuntime,
  stopRuntime,
  waitFor,
  waitForService,
  waitNoRunning,
  writeBatch,
} from "./runtime-helper.mjs";

const enabled = process.env.NEXUS_SYSTEM_SMOKE === "1";
const skipReason = process.env.NEXUS_SYSTEM_SMOKE !== "1"
  ? "设置 NEXUS_SYSTEM_SMOKE=1 后运行"
  : "";
const skip = enabled ? false : skipReason;
let mcpPort = 58732;
let mcpUrl = `http://127.0.0.1:${mcpPort}/mcp`;
let requestId = 0;

before(async () => {
  if (!enabled) return;
  if (isAdminMode) {
    assert.ok(isAdministrator(), "管理员 System Smoke 必须在 Administrator / High Integrity 终端运行");
  }
  prepareRuntime();
  writeSettings({ mcpEnabled: false });
  startRuntime();
  await waitForService();
  await selectMcpPort();
});

after(async () => {
  if (enabled) await stopRuntime();
});

function writeSettings({ mcpEnabled, lightweightMode = false }) {
  const configDir = path.join(runtimeDir, "config");
  fs.mkdirSync(configDir, { recursive: true });
  fs.writeFileSync(
    path.join(configDir, "settings.json"),
    JSON.stringify({
      WebPort: systemWebPort,
      McpEnabled: mcpEnabled,
      McpPort: mcpPort,
      LightweightMode: lightweightMode,
      AutoOpenBrowser: false,
      UpdateCheckEnabled: false,
      LogLevel: "info",
    }, null, 2),
    "utf8",
  );
}

async function restartRuntime(settings) {
  await stopRuntime();
  writeSettings(settings);
  startRuntime();
  await waitForService();
  if (settings.mcpEnabled) {
    await waitForMcpPort();
  }
}

function canConnect(port) {
  return new Promise(resolve => {
    const socket = net.createConnection({ host: "127.0.0.1", port });
    const finish = connected => {
      socket.destroy();
      resolve(connected);
    };
    socket.once("connect", () => finish(true));
    socket.once("error", () => finish(false));
    socket.setTimeout(1000, () => finish(false));
  });
}

async function selectMcpPort() {
  const webPort = Number(new URL(serviceUrl()).port);
  for (const candidate of [58732, 58733, 58734, 58735]) {
    if (candidate === webPort) continue;
    if (!await canConnect(candidate)) {
      mcpPort = candidate;
      mcpUrl = `http://127.0.0.1:${mcpPort}/mcp`;
      return;
    }
  }
  throw new Error("没有可用的 MCP System Smoke 端口");
}

async function waitForMcpPort() {
  assert.equal(
    await waitFor(() => canConnect(mcpPort), 30000, 100),
    true,
    "MCP 端口未在服务启动后监听",
  );
}

function parseRpcBody(text) {
  const trimmed = text.trim();
  if (!trimmed) return null;
  try {
    return JSON.parse(trimmed);
  } catch {
    // Streamable HTTP may choose an SSE response when both response media types are accepted.
    const dataLines = trimmed
      .split(/\r?\n/)
      .filter(line => line.startsWith("data:"))
      .map(line => line.slice("data:".length).trim())
      .filter(Boolean);
    for (const line of dataLines.reverse()) {
      try {
        return JSON.parse(line);
      } catch {
        // Continue until the last complete JSON-RPC event.
      }
    }
    throw new Error(`无法解析 MCP 响应：${trimmed.slice(0, 500)}`);
  }
}

async function mcpRequest(method, params = {}) {
  const id = ++requestId;
  let response;
  try {
    response = await fetchWithTimeout(mcpUrl, {
      method: "POST",
      headers: {
        "Content-Type": "application/json",
        Accept: "application/json, text/event-stream",
      },
      body: JSON.stringify({ jsonrpc: "2.0", id, method, params }),
    }, 10000);
  } catch (error) {
    const output = runtimeOutput();
    throw new Error(
      `MCP ${method} 请求失败：${error.message}\n${output.stdout}\n${output.stderr}`,
      { cause: error },
    );
  }
  const text = await response.text();
  assert.equal(response.ok, true, `MCP ${method} HTTP ${response.status}: ${text}`);
  const payload = parseRpcBody(text);
  assert.ok(payload, `MCP ${method} 返回空响应`);
  assert.equal(payload.id, id, `MCP ${method} 返回了错误的 request id`);
  assert.equal(payload.error, undefined, `MCP ${method} JSON-RPC 错误：${JSON.stringify(payload.error)}`);
  return payload.result;
}

function rawMcpPost(headers = {}) {
  const body = JSON.stringify({ jsonrpc: "2.0", id: ++requestId, method: "tools/list", params: {} });
  return new Promise((resolve, reject) => {
    const request = http.request({
      hostname: "127.0.0.1",
      port: mcpPort,
      path: "/mcp",
      method: "POST",
      headers: {
        "Content-Type": "application/json",
        "Content-Length": Buffer.byteLength(body),
        ...headers,
      },
    }, response => {
      const chunks = [];
      response.on("data", chunk => chunks.push(chunk));
      response.on("end", () => resolve({
        status: response.statusCode || 0,
        body: Buffer.concat(chunks).toString("utf8"),
      }));
    });
    request.once("error", reject);
    request.end(body);
  });
}

async function mcpTool(name, args = {}) {
  const envelope = await mcpToolEnvelope(name, args);
  assert.equal(envelope.ok, true, `MCP 工具 ${name} 业务失败：${JSON.stringify(envelope)}`);
  return envelope;
}

async function mcpToolEnvelope(name, args = {}) {
  const wireResult = await mcpRequest("tools/call", { name, arguments: args });
  const structured = wireResult?.structuredContent
    || wireResult?.content?.find(item => item.type === "text")?.text;
  const envelope = typeof structured === "string" ? JSON.parse(structured) : structured;
  assert.ok(envelope, `MCP 工具 ${name} 没有结构化 envelope`);
  return envelope;
}

async function mcpUnavailable() {
  try {
    await fetchWithTimeout(mcpUrl, {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: "{}",
    }, 2000);
    return false;
  } catch {
    return true;
  }
}

test("MCP 默认关闭时端点不存在", { skip, concurrency: false }, async () => {
  assert.equal(await mcpUnavailable(), true);
});

test("启用 MCP 后可完成握手、工具发现、状态读取与 loopback 安全校验", { skip, concurrency: false }, async () => {
  await restartRuntime({ mcpEnabled: true });

  const initialize = await mcpRequest("initialize", {
    protocolVersion: "2025-11-25",
    capabilities: {},
    clientInfo: { name: "NexusPipeline.SystemSmoke", version: "1.0" },
  });
  assert.match(initialize.protocolVersion, /^\d{4}-\d{2}-\d{2}$/);

  const discovery = await mcpRequest("tools/list");
  const toolNames = (discovery.tools || []).map(tool => tool.name);
  assert.ok(toolNames.includes("get_status"));
  assert.ok(toolNames.includes("list_scripts"));
  assert.ok(toolNames.includes("run_script"));
  assert.ok(toolNames.includes("get_run"));
  assert.ok(!toolNames.includes("delete_script"));
  assert.ok(!toolNames.includes("set_secret"));

  const status = await mcpTool("get_status");
  assert.equal(status.data.mcpEnabled, true);
  assert.equal(status.data.mcpPort, mcpPort);
  assert.equal(status.data.mcpEndpoint, mcpUrl);

  const settings = await mcpTool("get_settings");
  assert.equal(settings.data.mcpEnabled, true);
  assert.equal(settings.data.accessToken, "");

  const wrongOrigin = await rawMcpPost({
    Host: `127.0.0.1:${mcpPort}`,
    Origin: `http://evil.example:${mcpPort}`,
  });
  assert.equal(wrongOrigin.status, 403, `错误 Origin 未被拒绝：${wrongOrigin.status} ${wrongOrigin.body}`);

  const wrongHost = await rawMcpPost({ Host: `192.168.1.20:${mcpPort}` });
  assert.ok([400, 403].includes(wrongHost.status), `错误 Host 未被拒绝：${wrongHost.status}`);
});

test("MCP 可提交运行、轮询并取消长任务", { skip, concurrency: false }, async () => {
  await waitForMcpPort();
  const fixture = makeFixture("mcp-run");
  writeBatch(fixture, [
    "ping -n 20 127.0.0.1 >nul",
    `echo mcp-ok>>"${fixture.log}"`,
  ]);
  const userName = `MCP system user ${Date.now()}`;
  let scriptId = "";
  let userId = "";
  let runId = "";
  let dangerousQueueId = "";
  try {
    const scriptResponse = await api("POST", "/api/scripts", {
      name: `MCP System Smoke ${Date.now()}`,
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
    if (scriptResponse.status !== 200) {
      assert.fail(`创建 MCP Smoke 脚本失败：HTTP ${scriptResponse.status} ${await scriptResponse.text()}`);
    }
    const script = await scriptResponse.json();
    scriptId = script.id;

    const userResponse = await api("POST", "/api/users", {
      name: userName,
    });
    if (userResponse.status !== 200) {
      assert.fail(`创建 MCP Smoke 用户失败：HTTP ${userResponse.status} ${await userResponse.text()}`);
    }
    const user = await userResponse.json();
    userId = user.id;

    const addedBinding = await mcpTool("add_binding", {
      userReference: userId,
      input: {
        scriptInstanceId: scriptId,
        enabled: true,
        notifyEnabled: false,
        runDays: -1,
        maxSuccessfulRunsPerDay: 1,
      },
    });
    assert.equal(addedBinding.data.maxSuccessfulRunsPerDay, 1);

    const listedScripts = await mcpTool("list_scripts");
    assert.ok(listedScripts.data.some(item => item.id === scriptId));
    const listedUsers = await mcpTool("list_users");
    assert.ok(listedUsers.data.some(item => item.id === userId));
    const listedUser = listedUsers.data.find(item => item.id === userId);
    const listedBinding = listedUser?.bindings?.find(item => item.scriptInstanceId === scriptId);
    assert.equal(listedBinding?.maxSuccessfulRunsPerDay, 1);
    assert.equal(listedBinding?.effective?.maxSuccessfulRunsPerDay, 1);

    const dangerousQueueResponse = await api("POST", "/api/queues", {
      name: `MCP Dangerous Queue ${Date.now()}`,
      autoRunMode: "none",
      completionAction: "shutdown",
      tasks: [{ index: 0, scriptInstanceId: scriptId }],
      timeSets: [],
      notifyEnabled: false,
    });
    if (dangerousQueueResponse.status !== 200) {
      assert.fail(`创建已有系统完成操作的队列失败：HTTP ${dangerousQueueResponse.status} ${await dangerousQueueResponse.text()}`);
    }
    const dangerousQueue = await dangerousQueueResponse.json();
    dangerousQueueId = dangerousQueue.id;
    const blockedQueueRun = await mcpToolEnvelope("run_queue", {
      queueReference: dangerousQueue.id,
    });
    assert.equal(blockedQueueRun.ok, false);
    assert.equal(blockedQueueRun.errorCode, "dangerous_completion_action");

    const started = await mcpTool("run_script", {
      scriptReference: scriptId,
      userReference: userId,
    });
    runId = started.data.runId;
    assert.match(runId, /^[a-f0-9-]{20,}$/i);

    const observed = await mcpTool("get_run", { runId });
    assert.equal(observed.data.id, runId);
    assert.ok(["running", "cancelled", "done", "error"].includes(observed.data.status));

    const canceled = await mcpTool("cancel_run", { runId });
    assert.equal(canceled.data.canceled, true);

    let finalRun = null;
    assert.equal(await waitFor(async () => {
      try {
        finalRun = (await mcpTool("get_run", { runId })).data;
        return finalRun.status !== "running";
      } catch {
        return false;
      }
    }, 30000, 250), true, `MCP 运行任务未结束：${runId}`);
    assert.ok(["cancelled", "done", "error"].includes(finalRun.status), `未知终态：${finalRun.status}`);
  } finally {
    await waitNoRunning(30000);
    if (dangerousQueueId) {
      await api("DELETE", `/api/queues/${encodeURIComponent(dangerousQueueId)}`);
    }
    if (userId) {
      await api("DELETE", `/api/users/${encodeURIComponent(userId)}`, { confirmName: userName });
    }
    await deleteScript(scriptId);
  }
});

test("轻量模式保留 MCP 与 Control API，端口占用时主服务继续运行", { skip, concurrency: false }, async () => {
  await restartRuntime({ mcpEnabled: true, lightweightMode: true });
  const status = await mcpTool("get_status");
  assert.equal(status.data.lightweightMode, true);
  const root = await fetchWithTimeout(serviceUrl());
  assert.equal(root.status, 404);

  await stopRuntime();
  const blocker = net.createServer();
  await new Promise((resolve, reject) => {
    blocker.once("error", reject);
    blocker.listen(mcpPort, "127.0.0.1", resolve);
  });
  try {
    writeSettings({ mcpEnabled: true, lightweightMode: true });
    startRuntime();
    await waitForService();
    const controlStatus = await api("GET", "/api/status");
    assert.equal(controlStatus.status, 200);
    assert.equal(
      await waitFor(() => {
        const output = runtimeOutput();
        return /MCP 服务启动失败/.test(`${output.stdout}\n${output.stderr}`);
      }, 10000, 100),
      true,
      `MCP 端口占用诊断未出现：${JSON.stringify(runtimeOutput())}`,
    );
  } finally {
    await stopRuntime();
    await new Promise((resolve, reject) => {
      blocker.close(error => error ? reject(error) : resolve());
    });
  }
});
