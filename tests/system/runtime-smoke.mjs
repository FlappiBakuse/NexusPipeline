import test, { after, before } from "node:test";
import assert from "node:assert/strict";
import net from "node:net";
import fs from "node:fs";
import path from "node:path";
import {
  baseUrl,
  isElevated,
  isRuntimeAlive,
  prepareRuntime,
  projectRoot,
  runtimeDir,
  startRuntime,
  stopRuntime,
  waitForService,
} from "./runtime-helper.mjs";

const enabled = process.env.NEXUS_SYSTEM_SMOKE === "1" && isElevated();
const skipReason = process.env.NEXUS_SYSTEM_SMOKE !== "1"
  ? "设置 NEXUS_SYSTEM_SMOKE=1 后运行"
  : "System Smoke 需要管理员终端";
const skip = enabled ? false : skipReason;

before(async () => {
  if (!enabled) return;
  prepareRuntime();
  startRuntime();
  await waitForService();
});

after(async () => {
  if (enabled) await stopRuntime();
});

test("release binary 启动并提供 status API", { skip }, async () => {
  const response = await fetch(baseUrl + "api/status");
  assert.equal(response.status, 200);
  const status = await response.json();
  assert.match(status.version, /^\d+\.\d+\.\d+$/);
  assert.ok(Array.isArray(status.running));
});

test("status 与 limits API 在同一服务实例可用", { skip }, async () => {
  const response = await fetch(baseUrl + "api/limits");
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

test("58731 被占用时服务回退到下一个端口", { skip }, async () => {
  await stopRuntime();
  const blocker = net.createServer();
  await new Promise((resolve, reject) => {
    blocker.once("error", reject);
    blocker.listen(58731, "127.0.0.1", resolve);
  });
  try {
    startRuntime(["web"]);
    await waitForService("http://127.0.0.1:58732/", 30000);
    const response = await fetch("http://127.0.0.1:58732/api/status");
    assert.equal(response.status, 200);
  } finally {
    await stopRuntime();
    await new Promise(resolve => blocker.close(resolve));
    startRuntime();
    await waitForService();
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

test("System Smoke runtime 位于隔离目录", { skip }, () => {
  assert.ok(path.resolve(runtimeDir).startsWith(path.resolve(projectRoot, "tests")));
  assert.ok(fs.existsSync(path.join(runtimeDir, "nexus-pipeline.exe")));
});
