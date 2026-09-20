import test from "node:test";
import assert from "node:assert/strict";
import { gateSequence, runtimePolicy, RuntimePolicyError } from "../support/runtime-policy.mjs";

const base = {
  group: "ui-runtime",
  phase: "default",
  testHostDir: "D:\\isolated\\runs\\run-1\\test-host",
  exitFile: "D:\\isolated\\runs\\run-1\\ui\\.nxp\\test-host.exit",
  runId: "run-1",
  env: {
    PATH: "fixture-path",
    NEXUS_TIME_SCALE: "999",
    NEXUS_TEST_MODE: "admin",
    NEXUS_CI_EXECUTION_PLAN: "secret-plan",
    GITHUB_TOKEN: "secret-token",
    CUSTOM_VALUE: "preserved",
  },
};

test("Windows Medium/High/System/Unknown 都选择同一 Test Host", () => {
  for (const integrity of ["Medium", "High", "System", "Unknown"]) {
    const result = runtimePolicy({ ...base, integrity, platform: "win32" });
    assert.equal(result.artifactKind, "test-host");
    assert.equal(result.requestElevation, false);
    assert.equal(result.observedIntegrity, integrity);
    assert.equal(result.env.NEXUS_TEST_MODE, "test-host");
    assert.equal(result.env.NEXUS_TEST_HOST, "1");
  }
});

test("release gate 的 timeScale 不受外部 999 覆盖，realtime 固定为 1", () => {
  assert.equal(runtimePolicy({ ...base, platform: "win32" }).timeScale, 10);
  const realtime = runtimePolicy({ ...base, group: "update-acceptance", phase: "update-realtime", platform: "win32" });
  assert.equal(realtime.timeScale, 1);
  assert.equal(realtime.env.NEXUS_TIME_SCALE, "1");
});

test("测试子进程清除旧 CI 选择和远端凭据，但保留普通环境变量", () => {
  const result = runtimePolicy({ ...base, platform: "win32" });
  assert.equal(result.env.CUSTOM_VALUE, "preserved");
  assert.equal(result.env.NEXUS_CI_EXECUTION_PLAN, undefined);
  assert.equal(result.env.GITHUB_TOKEN, undefined);
  assert.equal(result.env.NEXUS_TEST_MODE, "test-host");
});

test("非 Windows UI/System 运行返回明确平台不可用", () => {
  assert.throws(
    () => runtimePolicy({ ...base, platform: "linux" }),
    error => error instanceof RuntimePolicyError && error.code === "PLATFORM_UNAVAILABLE",
  );
});

test("gateSequence 只表达测试范围，不包含权限模式", () => {
  assert.deepEqual(gateSequence("all"), ["core", "frontend-contract", "ui-runtime", "execution-emulator", "update-acceptance"]);
  assert.throws(() => gateSequence("admin"), /Unknown release group/u);
});
