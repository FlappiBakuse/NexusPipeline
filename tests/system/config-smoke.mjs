import test, { after, before } from "node:test";
import assert from "node:assert/strict";
import fs from "node:fs";
import path from "node:path";
import {
  api,
  executionMode,
  prepareRuntime,
  runtimeDir,
  startRuntime,
  stopRuntime,
  waitForService,
} from "./runtime-helper.mjs";

const enabled = process.env.NEXUS_SYSTEM_SMOKE === "1";
const skip = enabled ? false : "设置 NEXUS_SYSTEM_SMOKE=1 后运行";

before(async () => {
  if (!enabled) return;
  await prepareRuntime();
  startRuntime();
  await waitForService();
});

after(async () => {
  if (enabled) await stopRuntime();
});

test(`${executionMode} 配置读取与写入在同一运行目录内闭环`, { skip, concurrency: false }, async () => {
  const beforeResponse = await api("GET", "/api/settings");
  const beforeBody = await beforeResponse.json();
  assert.equal(beforeResponse.status, 200);
  assert.equal(typeof beforeBody.settings, "object");

  const original = beforeBody.settings.historyRetentionDays;
  const next = Number(original) === 30 ? 31 : 30;
  const updateResponse = await api("PUT", "/api/settings", { historyRetentionDays: next });
  assert.equal(updateResponse.status, 200, await updateResponse.text());
  try {
    const afterResponse = await api("GET", "/api/settings");
    const afterBody = await afterResponse.json();
    assert.equal(afterResponse.status, 200);
    assert.equal(afterBody.settings.historyRetentionDays, next);
    await stopRuntime();
    startRuntime();
    await waitForService();
    const restarted = await api("GET", "/api/settings");
    assert.equal((await restarted.json()).settings.historyRetentionDays, next);
  } finally {
    const restoreResponse = await api("PUT", "/api/settings", { historyRetentionDays: original });
    assert.equal(restoreResponse.status, 200, await restoreResponse.text());
  }
});

test("损坏配置在真实重启后保留原始字节，重复启动保留同一证据", { skip, concurrency: false }, async () => {
  await stopRuntime();
  const configDir = path.join(runtimeDir, "config");
  const scriptPath = path.join(configDir, "scripts.json");
  const corrupt = Buffer.from("{broken-config-smoke\u0001");
  fs.writeFileSync(scriptPath, corrupt);
  startRuntime();
  await waitForService();
  const preserved = fs.readdirSync(configDir).filter(name => name.startsWith("scripts.json.corrupt-"));
  assert.equal(preserved.length, 1);
  assert.deepEqual(fs.readFileSync(path.join(configDir, preserved[0])), corrupt);
  await stopRuntime();
  startRuntime();
  await waitForService();
  assert.deepEqual(fs.readdirSync(configDir).filter(name => name.startsWith("scripts.json.corrupt-")), preserved);
  assert.deepEqual(fs.readFileSync(path.join(configDir, preserved[0])), corrupt);
});

test("配置诊断返回可审计的 recovery 检查项", { skip }, async () => {
  const response = await api("GET", "/api/diagnostics");
  const body = await response.json();
  assert.equal(response.status, 200);
  const check = body.checks?.find(item => item.id === "config.recovery");
  assert.ok(check);
  assert.match(String(check.status), /^(pass|warn|fail|skipped)$/);
});
