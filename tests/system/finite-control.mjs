import { spawnSync } from "node:child_process";
import { findAvailablePort } from "../support/test-runtime.mjs";
import { runtime, assert, fs, path, json, target, settled, report } from "./finite-common.mjs";
const handoffs = [], protocol = [];
let cliRun, mcpRun;
function cli(args) {
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
  assert.equal(cli(["status"]).service, "NexusPipeline");
  const owned = await target("control-target"); await runtime.createUserBinding(owned.script.id, "control-user");
  cliRun = cli(["run", "script", owned.script.id, "--detach"]);
  assert.equal((await settled(cliRun.runId)).records[0].status, "success");
  let requestId = 0;
  const rpc = async (method, params) => {
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
  await restart();
  let installed = (await json("GET", "api/plugins")).find(item => item.name === "store-fixture");
  assert.equal(installed.version, "0.1.0");
  await json("POST", "api/plugins/store-fixture/enable");
  await json("POST", "api/plugins/store/refresh");
  assert.equal((await json("POST", "api/plugins/store/store-fixture/update")).pending, true);
  await restart();
  installed = (await json("GET", "api/plugins")).find(item => item.name === "store-fixture");
  assert.equal(installed.version, "0.1.1"); assert.equal(installed.runtimeEnabled, true);
  assert.equal((await json("GET", "api/history?days=1&limit=100")).records.length, 2);
  const receipts = fs.readFileSync(process.env.NEXUS_TEST_HTTP_PLAN + ".receipts.jsonl", "utf8").trim().split("\n").map(JSON.parse);
  assert.deepEqual(receipts.map(item => item.id).sort(), ["bad-hash","catalog","catalog-update","install","update"].sort());
  assert.ok(receipts.every(item => item.matched));
} finally { await runtime.stopRuntime(); }
report("H-E03", { cliRun, mcpRun, protocol, handoffs,
  real: ["CLI service client", "MCP HTTP protocol", "store install/update/hash/managed loading", "restart handoff"],
  substituted: ["official HTTPS responses", "owned target and synthetic plugin"] });
