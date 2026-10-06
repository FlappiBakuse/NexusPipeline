import { controlServiceName } from "../../tools/installation-generation.mjs";
import { spawnSync } from "node:child_process";
import { findAvailablePort } from "../support/test-runtime.mjs";
import { runtime, assert, fs, path, json, target, settled, report } from "./finite-common.mjs";
const handoffs = [], protocol = [], remoteAccessStates = [];
let cliRun, mcpRun, operation = "boot";
function cli(args) {
  operation = `CLI ${args[0]}`;
  const result = spawnSync(runtime.runtimeExe, [...args,"--json"], { cwd: runtime.runtimeDir, env: process.env,
    encoding: "utf8", timeout: 5000, windowsHide: true });
  assert.equal(result.status, 0, result.stdout + result.stderr);
  const lines = result.stdout.trim().split(/\r?\n/); assert.equal(lines.length, 1);
  const envelope = JSON.parse(lines[0]); assert.equal(envelope.ok, true); return envelope.data;
}
async function restart() {
  const before = await json("GET", "api/status");
  const restarted = await json("POST", "api/settings/restart");
  assert.equal(restarted.instanceId, before.instanceId);
  const after = await runtime.waitForRestartedService({ previousInstanceId: before.instanceId,
    expectedHandoffId: restarted.handoffId, timeoutMs: 10000, intervalMs: 50 });
  handoffs.push({ before: before.instanceId, after: after.instanceId, handoff: restarted.handoffId });
}
try {
  await runtime.prepareRuntime();
  const mcpPort = await findAvailablePort();
  const settings = path.join(runtime.runtimeDir, "config/settings.json");
  fs.writeFileSync(settings, JSON.stringify({ ...JSON.parse(fs.readFileSync(settings)),
    UpdateCheckEnabled: false, McpEnabled: true, McpPort: mcpPort }));
  runtime.startRuntime(["service"]); await runtime.waitForService(null, 5000);
  assert.equal(cli(["status"]).service, controlServiceName);
  assert.equal(await runtime.waitFor(async () => (await json("GET", "api/status")).ready === true, 5000, 50), true, "MCP listener did not become ready");
  const owned = await target("control-target"); await runtime.createUserBinding(owned.script.id, "control-user");
  cliRun = cli(["run", "script", owned.script.id, "--detach"]);
  assert.equal((await settled(cliRun.runId)).records[0].status, "success");
  let requestId = 0;
  const rpc = async (method, params) => {
    operation = `MCP ${method}`;
    const id = ++requestId;
    const response = await fetch(`http://127.0.0.1:${mcpPort}/mcp`, { method: "POST",
      headers: { "Content-Type": "application/json", Accept: "application/json, text/event-stream", "MCP-Protocol-Version": "2025-03-26" },
      body: JSON.stringify({ jsonrpc: "2.0", id, method, params }), signal: AbortSignal.timeout(3000) });
    const text = await response.text(); assert.equal(response.status, 200, text);
    const payload = JSON.parse(text.startsWith("{") ? text : text.split(/\r?\n/).find(line => line.startsWith("data: ")).slice(6));
    assert.equal(payload.id, id); assert.equal(payload.error, undefined, text); protocol.push({ method, id }); return payload.result;
  };
  const initialized = await rpc("initialize", { protocolVersion: "2025-03-26", capabilities: {}, clientInfo: { name: "finite-owned", version: "1.0" } });
  assert.equal(initialized.serverInfo.name, "NexusPipeline");
  const tool = async (name, args = {}) => {
    const result = await rpc("tools/call", { name, arguments: args }); assert.notEqual(result.isError, true, JSON.stringify(result));
    const envelope = result.structuredContent ?? JSON.parse(result.content.find(item => item.type === "text").text);
    assert.equal(envelope.ok, true, JSON.stringify(envelope)); return envelope.data;
  };
  await tool("get_status");
  mcpRun = await tool("run_script", { scriptReference: owned.script.id });
  assert.equal((await settled(mcpRun.runId)).records[0].status, "success");
  await tool("get_run", { runId: mcpRun.runId });
  assert.equal((await json("GET", "api/plugins/store")).available, true);
  await json("POST", "api/plugins/store/broken-fixture/install", undefined, 409);
  assert.equal((await json("POST", "api/plugins/store/store-fixture/install")).pending, true);
  await json("PUT", "api/settings", { allowRemoteAccess: true, secretKey: "accessToken", secretValue: "owned-restart-token" });
  let beforeRemoteRestart = (await json("GET", "api/settings")).status.remote;
  assert.equal(beforeRemoteRestart.bound, false);
  await restart();
  let remote = await json("GET", "api/settings");
  assert.equal(remote.settings.allowRemoteAccess, true);
  assert.equal(remote.status.remote.bound, true);
  assert.equal(remote.status.remote.tokenSet, true);
  assert.equal(remote.settings.accessToken, "enc:***");
  remoteAccessStates.push({ change: "enable", before: { allowed: beforeRemoteRestart.allowed, bound: beforeRemoteRestart.bound }, after: { allowed: remote.status.remote.allowed, bound: remote.status.remote.bound, tokenSet: remote.status.remote.tokenSet } });
  let installed = (await json("GET", "api/plugins")).find(item => item.name === "store-fixture");
  assert.equal(installed.version, "0.1.0");
  await json("POST", "api/plugins/store-fixture/enable");
  await json("POST", "api/plugins/store/refresh");
  assert.equal((await json("POST", "api/plugins/store/store-fixture/update")).pending, true);
  await json("PUT", "api/settings", { allowRemoteAccess: false });
  beforeRemoteRestart = (await json("GET", "api/settings")).status.remote;
  assert.equal(beforeRemoteRestart.bound, true);
  await restart();
  remote = await json("GET", "api/settings");
  assert.equal(remote.settings.allowRemoteAccess, false);
  assert.equal(remote.status.remote.bound, false);
  assert.equal(remote.status.remote.tokenSet, true);
  remoteAccessStates.push({ change: "disable", before: { allowed: beforeRemoteRestart.allowed, bound: beforeRemoteRestart.bound }, after: { allowed: remote.status.remote.allowed, bound: remote.status.remote.bound, tokenSet: remote.status.remote.tokenSet } });
  installed = (await json("GET", "api/plugins")).find(item => item.name === "store-fixture");
  assert.equal(installed.version, "0.1.1"); assert.equal(installed.runtimeEnabled, true);
  assert.equal((await json("GET", "api/history?days=1&limit=100")).records.length, 2);
  const index = path.join(runtime.runtimeDir, "history", ".task-latest.json");
  const oldIndex = Buffer.from('{"legacy-record":{"RecordId":"old"}}\r\n');
  fs.writeFileSync(index, oldIndex);
  const rejected = await json("GET", "api/users/task-summaries", undefined, 409);
  assert.equal(rejected.code, "unsupported_history_index");
  assert.deepEqual(fs.readFileSync(index), oldIndex);
  const receipts = fs.readFileSync(process.env.NEXUS_TEST_HTTP_PLAN + ".receipts.jsonl", "utf8").trim().split("\n").map(JSON.parse);
  assert.deepEqual(receipts.map(item => item.id).sort(), ["bad-hash","catalog","catalog-update","install","update"].sort());
  assert.ok(receipts.every(item => item.matched));
} catch (error) {
  console.error(operation, error, runtime.runtimeDiagnostic());
  throw error;
} finally { await runtime.stopRuntime(); }
report("H-E03", { cliRun, mcpRun, protocol, handoffs, remoteAccessStates,
  real: ["CLI service client", "MCP HTTP protocol", "store install/update/hash/managed loading", "restart handoff", "remote access policy and encrypted token survive enable/disable restarts"],
  substituted: ["official HTTPS responses", "owned target and synthetic plugin"] });
