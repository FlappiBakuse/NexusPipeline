import { chromium } from "../e2e/node_modules/playwright/index.mjs";
import { runtime, assert, fs, json, boot, target, settled, report } from "./finite-common.mjs";
let browser;
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
} finally { await browser?.close(); await runtime.stopRuntime(); }
report("H-E01", { runs, queries, scriptCount: targets.length, queueCount: queues.length, userCount: users.length,
  real: ["browser queue selection and dispatch", "execution admission", "cancel process tree", "history identity"],
  substituted: ["six owned external targets"] });
