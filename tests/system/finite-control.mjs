import { controlServiceName } from "../../tools/installation-generation.mjs";
import { spawnSync } from "node:child_process";
import { findAvailablePort } from "../support/test-runtime.mjs";
import { runtime, assert, fs, path, json, target, settled, report } from "./finite-common.mjs";
const handoffs = [], protocol = [], remoteAccessStates = [];
let cliRun, mcpRun, operation = "boot";
function cli(args, expected = true) {
  operation = `CLI ${args[0]}`;
  const result = spawnSync(runtime.runtimeExe, [...args,"--json"], { cwd: runtime.runtimeDir, env: process.env,
    encoding: "utf8", timeout: 5000, windowsHide: true });
  if (expected) assert.equal(result.status, 0, result.stdout + result.stderr);
  else assert.notEqual(result.status, 0, result.stdout + result.stderr);
  const lines = result.stdout.trim().split(/\r?\n/); assert.equal(lines.length, 1);
  const envelope = JSON.parse(lines[0]); assert.equal(envelope.ok, expected); return expected ? envelope.data : envelope;
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
  const payloadFile = path.join(runtime.runtimeDir, "cli-request.json");
  const file = value => { fs.writeFileSync(payloadFile, JSON.stringify(value)); return payloadFile; };
  const cliUser = cli(["user", "create", "--name", "cli-completion-user"]);
  cli(["user", "binding", "add", cliUser.id, owned.script.id, "--file", file({enabled:false})]);
  assert.equal((await json("GET", `api/users/${cliUser.id}/bindings`))[0].enabled, false);
  cli(["user", "binding", "reorder", cliUser.id, "--ids", owned.script.id]);
  cli(["user", "binding", "config-status", cliUser.id, owned.script.id]);
  cli(["user", "binding", "task-plan", cliUser.id, owned.script.id]);
  cli(["user", "binding", "repair-preview", cliUser.id, owned.script.id]);
  cli(["user", "task-summaries"]); cli(["user", "badges"]);
  cli(["script", "edit-sessions"]); cli(["provider-config", "list"]);
  cli(["provider-config", "get", "missing"], false);
  cli(["provider-config", "start", "--file", file({})], false);
  cli(["script", "probe", "--plugin", "missing", "--root", owned.directory, "--file", file({custom:"value"})], false);
  cli(["limits"]); cli(["plugin", "frontend"]);
  const layout = cli(["dashboard", "get"]);
  assert.match(layout.etag, /^"dashboard-sha256-[0-9a-f]{64}"$/);
  cli(["dashboard", "cards"]);
  const layoutPayload = {schemaVersion:1,catalogRevision:layout.snapshot.catalogRevision,visibleCardIds:[]};
  cli(["dashboard", "update", "--etag", layout.etag, "--file", file(layoutPayload)]);
  assert.deepEqual((await json("GET", "api/dashboard/layout")).visibleCardIds, []);
  const staleLayout = cli(["dashboard", "update", "--etag", '"dashboard-sha256-' + '0'.repeat(64) + '"', "--file", file(layoutPayload)], false);
  assert.equal(staleLayout.code, "dashboard_layout_conflict");
  const iconPath = path.join(runtime.runtimeDir, "cli-type.ico");
  const icon = cli(["script", "type-icon", "general", "--output", iconPath]);
  assert.ok(icon.sizeBytes > 0); assert.equal(fs.statSync(iconPath).size, icon.sizeBytes);
  cli(["script", "type-icon", "general", "--output", iconPath], false);
  cli(["plugin", "request", "fixture", "GET", "../settings"], false);
  cli(["settings", "access-token"]);
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
  const advertised = (await rpc("tools/list", {})).tools;
  const names = advertised.map(t => t.name);
  for (const name of ["get_script","get_user","get_queue","get_task_plan","get_task_summaries","get_run_tasks","get_history","get_history_summary","get_dashboard","probe_script","preview_config_repair","list_provider_configurations","create_queue","update_queue","update_user"]) assert.ok(names.includes(name), name);
  for (const name of ["delete_script","delete_user","delete_queue","get_access_token","install_plugin","restart","request","shell"]) assert.ok(!names.includes(name), name);
  const rejectedTool = async (name, args, code) => {
    const result = await rpc("tools/call", {name, arguments:args});
    const envelope = result.structuredContent ?? JSON.parse(result.content.find(x => x.type === "text").text);
    assert.equal(envelope.ok, false); assert.equal(envelope.errorCode, code);
  };
  await tool("get_script", {reference:owned.script.id});
  await tool("get_user", {reference:cliUser.id});
  await tool("update_user", {reference:cliUser.id,input:{name:"cli-completion-renamed",remark:"mcp"}});
  assert.equal((await json("GET", `api/users/${cliUser.id}`)).name, "cli-completion-renamed");
  const safeQueue = await tool("create_queue", {input:{name:"mcp-safe",tasks:[{scriptInstanceId:owned.script.id}],completionAction:"none"}});
  await tool("update_queue", {reference:safeQueue.id,input:{name:"mcp-safe-updated",tasks:[{scriptInstanceId:owned.script.id}],completionAction:"none"}});
  assert.equal((await tool("get_queue", {reference:safeQueue.id})).name, "mcp-safe-updated");
  for (const action of ["exit","sleep","reboot","shutdown"]) await rejectedTool("create_queue", {input:{name:"unsafe",completionAction:action}}, "dangerous_completion_action");
  await rejectedTool("update_queue", {reference:safeQueue.id,input:{name:"unsafe",completionAction:"shutdown"}}, "dangerous_completion_action");
  assert.equal((await tool("get_queue", {reference:safeQueue.id})).completionAction, "none");
  const unsafeQueue = await json("POST", "api/queues", {name:"web-power-queue",autoRunMode:"none",completionAction:"shutdown",tasks:[{scriptInstanceId:owned.script.id}]});
  await rejectedTool("run_queue", {queueReference:unsafeQueue.id}, "dangerous_completion_action");
  await rejectedTool("update_queue", {reference:unsafeQueue.id,input:{name:"replacement",completionAction:"none"}}, "dangerous_completion_action");
  await rejectedTool("update_queue", {reference:"",input:{name:"cannot-create"}}, "validation_error");
  await tool("get_task_summaries");
  await tool("get_task_plan", {userReference:cliUser.id,scriptReference:owned.script.id});
  await tool("preview_config_repair", {userReference:cliUser.id,scriptReference:owned.script.id});
  await tool("list_provider_configurations"); await tool("get_dashboard");
  await tool("get_history_summary", {days:1});
  await rejectedTool("get_history_summary", {days:0}, "validation_error");
  await tool("get_status");
  mcpRun = await tool("run_script", { scriptReference: owned.script.id });
  assert.equal((await settled(mcpRun.runId)).records[0].status, "success");
  await tool("get_run", { runId: mcpRun.runId });
  await tool("get_run_tasks", {runId:mcpRun.runId});
  cli(["run", "tasks", mcpRun.runId]);
  const history = cli(["history", "list", "--days", "1", "--limit", "20"]);
  assert.equal(history.records.length, 2);
  await tool("get_history", {recordId:history.records[0].id});
  cli(["history", "summary", "--days", "1"]);
  const dates = cli(["history", "dates", "--days", "1"]);
  cli(["history", "users", "--date", dates.dates[0].date]);

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
  const session = cli(["client-session", "create"]);
  const sessionFile = path.join(runtime.runtimeDir, "cli-session.json"); fs.writeFileSync(sessionFile, JSON.stringify(session));
  const clientSession = cli(["client-session", "get", "--session-file", sessionFile]);
  assert.equal(clientSession.nativeBrowserAvailable, false);
  const echoed = cli(["plugin", "request", "store-fixture", "PUT", "json/echo", "--file", file({value:"cli-json"}), "--session-file", sessionFile]);
  assert.equal(JSON.parse(echoed.jsonBody).value, "cli-json");
  const binaryFile = path.join(runtime.runtimeDir, "cli-upload.bin"); fs.writeFileSync(binaryFile, "cli-binary");
  const binaryEcho = cli(["plugin", "request", "store-fixture", "POST", "binary/echo", "--binary", binaryFile, "--content-type", "application/octet-stream"]);
  assert.equal(binaryEcho.text, "cli-binary");
  const binaryOutput = path.join(runtime.runtimeDir, "cli-plugin-image.png");
  cli(["plugin", "request", "store-fixture", "GET", "binary/image", "--output", binaryOutput]);
  assert.equal(fs.readFileSync(binaryOutput, "utf8"), "fixture-binary-payload");
  cli(["plugin", "request", "store-fixture", "GET", "binary/image"], false);
  cli(["client-session", "revoke", "--session-file", sessionFile]);
  cli(["client-session", "get", "--session-file", sessionFile], false);
  const details = cli(["plugin", "get", "store-fixture"]); assert.ok(details);

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
  real: ["CLI resource writes, ETag conflicts, binary transfer and Web session revocation", "MCP typed queries and safe queue policy", "store install/update/hash/managed loading", "restart handoff", "remote access policy and encrypted token survive enable/disable restarts"],
  substituted: ["official HTTPS responses", "owned target and synthetic plugin"] });
