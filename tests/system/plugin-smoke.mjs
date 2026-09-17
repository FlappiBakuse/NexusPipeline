import test, { after, before } from "node:test";
import assert from "node:assert/strict";
import {
  api,
  executionMode,
  isAdminMode,
  isAdministrator,
  prepareRuntime,
  startRuntime,
  stopRuntime,
  waitForService,
} from "./runtime-helper.mjs";

const enabled = process.env.NEXUS_SYSTEM_SMOKE === "1";
const skip = enabled ? false : "设置 NEXUS_SYSTEM_SMOKE=1 后运行";

before(async () => {
  if (!enabled) return;
  if (isAdminMode) assert.ok(isAdministrator(), "管理员 plugin Smoke 必须在 Administrator / High Integrity 终端运行");
  prepareRuntime();
  startRuntime();
  await waitForService();
});

after(async () => {
  if (enabled) await stopRuntime();
});

test(`${executionMode} 插件管理与前端运行时清单使用稳定 JSON 形状`, { skip }, async () => {
  const pluginsResponse = await api("GET", "/api/plugins");
  const plugins = await pluginsResponse.json();
  assert.equal(pluginsResponse.status, 200);
  assert.ok(Array.isArray(plugins));
  for (const plugin of plugins) {
    assert.equal(typeof plugin.name, "string");
    assert.equal(typeof plugin.kind, "string");
    assert.equal(typeof plugin.compatible, "boolean");
  }

  const runtimeResponse = await api("GET", "/api/plugin-runtime/frontend");
  const runtime = await runtimeResponse.json();
  assert.equal(runtimeResponse.status, 200);
  assert.ok(Array.isArray(runtime));
  for (const descriptor of runtime) {
    assert.equal(typeof descriptor.name, "string");
    assert.equal(typeof descriptor.entryUrl, "string");
    assert.equal(typeof descriptor.frontendApiVersion, "string");
  }
});

test("插件诊断同时覆盖 runtime 与 pending 安装状态", { skip }, async () => {
  const response = await api("GET", "/api/diagnostics");
  const body = await response.json();
  assert.equal(response.status, 200);
  for (const id of ["plugin.runtime", "plugin.pending"]) {
    const check = body.checks?.find(item => item.id === id);
    assert.ok(check, `缺少诊断项 ${id}`);
    assert.match(String(check.status), /^(pass|warn|fail|skipped)$/);
  }
});
