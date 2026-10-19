import { chromium } from "../e2e/node_modules/playwright/index.mjs";
import { runtime, assert, fs, json, boot, target, settled, report } from "./finite-common.mjs";
import http from "node:http";
let browser;
const deliveries = [];
const sink = http.createServer(async (request, response) => {
  let body = ""; for await (const chunk of request) body += chunk;
  deliveries.push({ path: request.url, body: JSON.parse(body) });
  response.writeHead(200, { "content-type": "application/json" }); response.end("{}");
});
await new Promise(resolve => sink.listen(0, "127.0.0.1", resolve));
const sinkUrl = `http://127.0.0.1:${sink.address().port}`;
const runs = [], targets = [], users = [], queues = [];
let queries = 0;
try {
  await boot();
  for (let index = 0; index < 6; index++) targets.push(await target(`execution-${index}`, { hold: index < 2, failure: index === 5 }));
  for (let index = 0; index < 4; index++) users.push(await json("POST", "api/users", { name: `execution-user-${index}` }));
  for (let index = 0; index < 6; index++) await json("POST", `api/users/${users[index % 4].id}/bindings`,
    { scriptInstanceId: targets[index].script.id, enabled: true });
  for (let index = 0; index < 3; index++) queues.push(await json("POST", "api/queues", {
    name: `execution-queue-${index}`, tasks: [{ scriptInstanceId: targets[index + 2].script.id, index: 0 }] }));
  browser = await chromium.launch({ channel: "msedge", headless: true });
  const page = await browser.newPage({ locale: "zh-CN" }); page.setDefaultTimeout(4000);
  await page.goto(runtime.serviceUrl() + "#/dispatch");
  await page.locator("#dc-kind-trigger").click();
  await page.getByRole("option").filter({ hasText: "调度队列" }).click();
  await page.locator("#dc-queue-trigger").click();
  await page.getByRole("option", { name: queues[0].name, exact: true }).click();
  const [started] = await Promise.all([page.waitForResponse(response => response.url().endsWith("/api/dispatch/queue")
    && response.request().method() === "POST"), page.getByTestId("dispatch-run").click()]);
  assert.equal(started.status(), 200); const initial = await started.json();
  runs.push(await settled(initial.runId));
  for (let index = 1; index < 12; index++) {
    const cancel = index <= 4;
    const item = targets[cancel ? index % 2 : 2 + (index % 4)];
    fs.rmSync(item.effect, { force: true });
    const request = index === 6 || index === 7
      ? await json("POST", "api/dispatch/queue", { queueId: queues[index - 5].id })
      : await json("POST", "api/dispatch/script", { scriptId: item.script.id });
    if (cancel) {
      assert.equal(await runtime.waitFor(() => fs.existsSync(item.effect), 3000, 25), true);
      const effect = JSON.parse(fs.readFileSync(item.effect));
      const active = await json("GET", `api/dispatch/${request.runId}`); assert.equal(active.status, "running");
      await json("POST", "api/cancel", { runId: request.runId });
      const result = await settled(request.runId);
      assert.equal(result.status, "cancelled", JSON.stringify(result));
      assert.equal(runtime.isRuntimeAlive(effect.pid), false, "Cancelled owned worker survives");
      runs.push(result);
    } else runs.push(await settled(request.runId));
    const count = index === 11 ? 4 : 6;
    for (let read = 0; read < count; read++) {
      const value = await json("GET", read % 2 ? `api/dispatch/${request.runId}` : "api/status");
      if (read % 2) assert.equal(value.id, request.runId);
      queries++;
    }
  }
  assert.equal(queries, 64); assert.equal(new Set(runs.map(run => run.id)).size, 12);
  assert.equal(runs.filter(run => run.status === "cancelled").length, 4);
  assert.ok(runs.some(run => run.records.some(record => record.status === "failed")));
  const history = await json("GET", "api/history?days=1&limit=100");
  assert.equal(history.records.length, 12);
  for (const run of runs) {
    assert.equal(run.records.length, 1);
    const record = run.records[0];
    assert.ok(history.records.some(item => item.id === record.id && item.userId === record.userId));
    const index = targets.findIndex(item => item.script.id === record.scriptInstanceId);
    assert.equal(record.userId, users[index % 4].id);
    assert.equal((await json("GET", `api/dispatch/${run.id}`)).status, run.status);
  }

  const disabled = targets[4], disabledUser = users[0];
  await json("PUT", `api/users/${disabledUser.id}/bindings/${disabled.script.id}`, { enabled: false });
  const singlePlan = (await json("POST", "api/dispatch/explain/script", { scriptId: disabled.script.id })).result;
  assert.equal(singlePlan.admissible, false);
  assert.equal(singlePlan.admissionFailure.code, "no_enabled_bindings");
  assert.equal((await json("POST", "api/dispatch/script", { scriptId: disabled.script.id }, 409)).code, "no_enabled_bindings");
  await json("PUT", `api/queues/${queues[0].id}`, { ...queues[0], tasks: [
    { scriptInstanceId: targets[2].script.id, index: 0 }, { scriptInstanceId: disabled.script.id, index: 1 }] });
  const mixedRun = await settled((await json("POST", "api/dispatch/queue", { queueId: queues[0].id })).runId);
  const skipped = mixedRun.records.find(record => record.scriptInstanceId === disabled.script.id);
  assert.equal(skipped.status, "skipped");
  assert.equal(skipped.resultCode, "run.no_enabled_users");
  assert.equal((await json("GET", `api/history/detail?id=${skipped.id}`)).record.status, "skipped");
  await json("PUT", `api/users/${users[2].id}/bindings/${targets[2].script.id}`, { enabled: false });
  const queuePlan = (await json("POST", "api/dispatch/explain/queue", { queueId: queues[0].id })).result;
  assert.equal(queuePlan.admissible, false);
  assert.equal(queuePlan.tasks.length, 2);
  assert.equal((await json("POST", "api/dispatch/queue", { queueId: queues[0].id }, 409)).code, "no_enabled_bindings");
  const createNotice = async path => json("POST", "api/notifications", {
    name: `controlled-${path}`, type: "webhook", enabled: true,
    configuration: { webhookType: "generic", webhookTemplate: '{"text":{text}}' },
    secrets: { webhookUrl: { action: "replace", value: `${sinkUrl}/${path}` } }
  });
  const userNotice = await createNotice("user"), queueNotice = await createNotice("queue");
  for (const index of [0, 2, 5]) await json("PUT", `api/users/${users[index % 4].id}/bindings/${targets[index].script.id}`,
    { enabled: true, notifyEnabled: true, notificationTaskIds: [userNotice.id, userNotice.id] });
  for (const notifyEnabled of [true, false]) {
    for (const index of [2, 5, 0]) {
      deliveries.length = 0;
      await json("PUT", `api/queues/${queues[0].id}`, { ...queues[0], notifyEnabled,
        notificationTaskIds: [queueNotice.id, queueNotice.id], tasks: [
          { scriptInstanceId: targets[index].script.id, index: 0 },
          { scriptInstanceId: disabled.script.id, index: 1 }
        ] });
      fs.rmSync(targets[index].effect, { force: true });
      const dispatched = await json("POST", "api/dispatch/queue", { queueId: queues[0].id });
      if (index === 0) {
        assert.equal(await runtime.waitFor(() => fs.existsSync(targets[index].effect), 3000, 25), true);
        await json("POST", "api/cancel", { runId: dispatched.runId });
      }
      const result = await settled(dispatched.runId);
      assert.equal(deliveries.length, 1, JSON.stringify(deliveries));
      assert.equal(deliveries[0].path, notifyEnabled ? "/queue" : "/user");
      assert.ok(deliveries[0].body.text.includes(index === 0 ? "取消" : index === 5 ? "失败" : "成功"));
      if (index === 0) assert.equal(result.status, "cancelled");
      else if (notifyEnabled) assert.ok(deliveries[0].body.text.includes("跳过"));
    }
  }
  for (const notifyEnabled of [true, false]) {
    deliveries.length = 0;
    const firstTaskId = "f".repeat(32);
    await json("PUT", `api/queues/${queues[0].id}`, { ...queues[0], notifyEnabled,
      notificationTaskIds: [queueNotice.id], tasks: [
        { id: firstTaskId, scriptInstanceId: targets[5].script.id, index: 0 },
        { id: "e".repeat(32), scriptInstanceId: targets[2].script.id, index: 1, dependsOnTaskIds: [firstTaskId] }
      ] });
    const dispatched = await json("POST", "api/dispatch/queue", { queueId: queues[0].id });
    const result = await settled(dispatched.runId);
    assert.ok(result.records.some(record => record.resultCode === "run.not_started_dependency"));
    assert.equal(deliveries.length, notifyEnabled ? 1 : 2);
    assert.ok(deliveries.every(item => item.path === (notifyEnabled ? "/queue" : "/user")));
  }
  deliveries.length = 0;
  await json("PUT", `api/queues/${queues[0].id}`, { ...queues[0], notifyEnabled: true,
    notificationTaskIds: [], tasks: [{ scriptInstanceId: targets[2].script.id, index: 0 }] });
  const emptyTargets = await json("POST", "api/dispatch/queue", { queueId: queues[0].id });
  await settled(emptyTargets.runId);
  assert.equal(deliveries.length, 0, "Queue summary mode with no targets must also suppress user notifications");
  deliveries.length = 0;
  await json("PUT", `api/queues/${queues[0].id}`, { ...queues[0], notifyEnabled: true,
    notificationTaskIds: [queueNotice.id], tasks: [{ scriptInstanceId: disabled.script.id, index: 0 }] });
  assert.equal((await json("POST", "api/dispatch/queue", { queueId: queues[0].id }, 409)).code, "no_enabled_bindings");
  assert.equal(deliveries.length, 0, "Rejected admission must not emit a completed run notification");
} finally { await browser?.close(); await runtime.stopRuntime(); await new Promise(resolve => sink.close(resolve)); }
report("H-E01", { runs, queries, scriptCount: targets.length, queueCount: queues.length, userCount: users.length,
  real: ["browser queue selection and dispatch", "execution admission", "cancel process tree", "history identity"],
  substituted: ["six owned external targets"] });
