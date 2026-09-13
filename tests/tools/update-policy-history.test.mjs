import test from "node:test";
import assert from "node:assert/strict";
import { validatePolicyHistory } from "../../tools/validate-update-policy-history.mjs";

function policy(barriers) {
  return JSON.stringify({
    schemaVersion: 1,
    repository: "FlappiBakuse/NexusPipeline",
    barriers,
  });
}

const firstBarrier = {
  version: "0.17.0",
  code: "installation-layout-v2",
  migrationUrl: "https://example.com/v0.17.0",
};

test("update-policy 允许从空列表追加新 barrier", () => {
  const result = validatePolicyHistory(
    policy([]),
    policy([firstBarrier]),
  );
  assert.deepEqual(result, { basePresent: true, baseCount: 0, headCount: 1 });
});

test("update-policy 允许在历史末尾追加更高版本 barrier", () => {
  assert.doesNotThrow(() => validatePolicyHistory(
    policy([firstBarrier]),
    policy([
      firstBarrier,
      { version: "0.18.0-rc.1", code: "layout-v3" },
      { version: "0.18.0", code: "layout-v3-final" },
    ]),
  ));
});

test("update-policy 拒绝删除历史 barrier", () => {
  assert.throws(
    () => validatePolicyHistory(policy([firstBarrier]), policy([])),
    /不能删除历史 barrier/u,
  );
});

test("update-policy 拒绝修改历史 barrier version、code 或迁移地址", () => {
  for (const changed of [
    { ...firstBarrier, version: "0.17.1" },
    { ...firstBarrier, code: "installation-layout-v3" },
    { ...firstBarrier, migrationUrl: "https://example.com/other" },
  ]) {
    assert.throws(
      () => validatePolicyHistory(policy([firstBarrier]), policy([changed])),
      /被删除、修改或重新排序/u,
    );
  }
});

test("update-policy 拒绝把新 barrier 插入历史中间", () => {
  assert.throws(
    () => validatePolicyHistory(
      policy([firstBarrier, { version: "0.18.0", code: "layout-v3" }]),
      policy([firstBarrier, { version: "0.17.5", code: "hotfix-layout" }, { version: "0.18.0", code: "layout-v3" }]),
    ),
    /被删除、修改或重新排序/u,
  );
});

test("update-policy 拒绝清空已有 barrier", () => {
  assert.throws(
    () => validatePolicyHistory(policy([firstBarrier, { version: "0.18.0", code: "layout-v3" }]), policy([])),
    /不能删除历史 barrier/u,
  );
});

test("update-policy 拒绝追加不递增的版本", () => {
  assert.throws(
    () => validatePolicyHistory(
      policy([firstBarrier]),
      policy([firstBarrier, { version: "0.16.0", code: "older" }]),
    ),
    /严格追加在历史最后一个版本之后/u,
  );
});
