import test from "node:test";
import assert from "node:assert/strict";
import fs from "node:fs";
import path from "node:path";
import { api, prepareRuntime, startRuntime, stopRuntime, waitForService,
  waitForRestartedService, runtimeDir, runtimeDiagnostic } from "./runtime-helper.mjs";

test("official HTTPS store rejects bad bytes and installs, loads and uninstalls a real fixture", { timeout: 45000 }, async () => {
  const plan = process.env.NEXUS_TEST_HTTP_PLAN;
  assert.ok(plan && path.isAbsolute(plan), "explicit HTTP plan is required");
  async function json(method, route, expectedStatus = 200) {
    const response = await api(method, route);
    const text = await response.text();
    assert.equal(response.status, expectedStatus, `${route}: ${text}`);
    return JSON.parse(text);
  }
  async function restart() {
    const result = await json("POST", "/api/settings/restart");
    assert.equal(result.ok, true);
    return waitForRestartedService({ previousInstanceId: result.instanceId,
      expectedHandoffId: result.handoffId, timeoutMs: 10000, intervalMs: 50 });
  }
  await prepareRuntime();
  const settingsFile = path.join(runtimeDir, "config", "settings.json");
  const settings = JSON.parse(fs.readFileSync(settingsFile, "utf8"));
  fs.writeFileSync(settingsFile, JSON.stringify({ ...settings, UpdateCheckEnabled: false }));
  try {
    startRuntime(["service"], { NEXUS_TEST_HTTP_PLAN: plan });
    await waitForService();
    const store = await json("GET", "/api/plugins/store");
    assert.equal(store.available, true);
    assert.equal(store.stale, false);
    assert.equal(store.plugins.length, 2);
    const rejected = await json("POST", "/api/plugins/store/broken-fixture/install", 409);
    assert.match(JSON.stringify(rejected), /sha256_mismatch/);
    assert.equal(fs.existsSync(path.join(runtimeDir, "plugins", "BrokenFixture")), false);
    const installed = await json("POST", "/api/plugins/store/store-fixture/install");
    assert.equal(installed.pending, true);
    assert.equal(installed.restartRequired, true);
    await restart();
    let plugins = await json("GET", "/api/plugins");
    let fixture = plugins.find(item => item.name === "store-fixture");
    assert.ok(fixture, "installed plugin must load through the real manager");
    assert.equal(fixture.version, "0.1.0");
    assert.equal(fixture.configuredEnabled, true);
    assert.equal(fixture.runtimeEnabled, true, JSON.stringify(fixture));
    await restart();
    plugins = await json("GET", "/api/plugins");
    fixture = plugins.find(item => item.name === "store-fixture");
    assert.equal(fixture.runtimeEnabled, true, JSON.stringify(fixture));
    const disabled = await json("POST", "/api/plugins/store-fixture/disable");
    assert.equal(disabled.configuredEnabled, false);
    await restart();
    plugins = await json("GET", "/api/plugins");
    fixture = plugins.find(item => item.name === "store-fixture");
    assert.equal(fixture.runtimeEnabled, false, JSON.stringify(fixture));
    const ownershipPath = path.join(runtimeDir, ".nxp", "state", "plugins", "ownership.json");
    assert.match(fs.readFileSync(ownershipPath, "utf8"), /store-fixture/);
    const removed = await json("POST", "/api/plugins/store/store-fixture/uninstall");
    assert.equal(removed.pending, true);
    await restart();
    const remaining = await json("GET", "/api/plugins");
    assert.equal(remaining.some(item => item.name === "store-fixture"), false);
    assert.equal(fs.existsSync(path.join(runtimeDir, "plugins", "StoreFixture")), false);
    assert.doesNotMatch(fs.readFileSync(ownershipPath, "utf8"), /store-fixture/);
    const receipts = fs.readFileSync(plan + ".receipts.jsonl", "utf8").trim().split("\n").map(JSON.parse);
    assert.deepEqual(receipts.map(item => item.id).sort(), ["bad-hash", "catalog", "install"]);
    assert.ok(receipts.every(item => item.matched && item.runId === process.env.NEXUS_TEST_RUN_ID));
  } catch (error) {
    console.error(runtimeDiagnostic());
    throw error;
  } finally {
    await stopRuntime();
  }
});
