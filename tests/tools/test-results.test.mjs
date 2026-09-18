import test from "node:test";
import assert from "node:assert/strict";
import { parseTapResults, parseTrxResults, parseVitestResults, parsePlaywrightResults } from "../../tools/test-results.mjs";

test("结果读取拒绝空执行、全部跳过和缺失报告", () => {
  assert.throws(() => parseTapResults("# pass 0\n# fail 0\n# cancelled 0\n# skipped 8\n# todo 0\n"));
  assert.throws(() => parseTrxResults(""));
  assert.throws(() => parseVitestResults({ testResults: [] }));
  assert.throws(() => parsePlaywrightResults({}));
});

test("各引擎结果保留真实通过、失败与跳过数量", () => {
  const expected = { testCount: 6, passed: 3, failed: 2, skipped: 1 };
  assert.deepEqual(parseTapResults("# pass 3\n# fail 1\n# cancelled 1\n# skipped 1\n# todo 0\n"), expected);
  assert.deepEqual(parseTrxResults('<Counters total="6" passed="3" failed="2" notExecuted="1" />'), expected);
  assert.deepEqual(parseVitestResults({ testResults: [{ assertionResults: ["passed", "passed", "passed", "failed", "failed", "pending"].map(status => ({ status })) }] }), expected);
  assert.deepEqual(parsePlaywrightResults({ stats: { expected: 3, unexpected: 1, flaky: 1, skipped: 1 } }), expected);
});

test("各引擎拒绝未知状态和不守恒的原生 total", () => {
  assert.throws(
    () => parseTrxResults('<Counters total="7" passed="3" failed="2" notExecuted="1" />'),
    /不守恒/u,
  );
  assert.throws(
    () => parseVitestResults({
      numTotalTests: 2,
      testResults: [{ assertionResults: [{ status: "passed" }, { status: "failed" }, { status: "unknown" }] }],
    }),
    /未知测试状态/u,
  );
  assert.throws(
    () => parsePlaywrightResults({ stats: { total: 5, expected: 3, unexpected: 1, flaky: 0, skipped: 0 } }),
    /不守恒/u,
  );
});

test("结构化结果保留实际文件与 update:swap-ready skip 身份", () => {
  const tap = [
    "TAP version 13",
    "# Subtest: update:swap-ready | 故障注入",
    "    1..0 # SKIP SwapReady 故障注入需要 Test Host",
    "ok 1 - update:swap-ready | 故障注入 # SKIP SwapReady 故障注入需要 Test Host",
    "ok 2 - update:apply",
    "1..2",
    "# tests 2",
    "# pass 1",
    "# fail 0",
    "# cancelled 0",
    "# skipped 1",
    "# todo 0",
  ].join("\n");
  const result = parseTapResults(tap, {
    expectedFiles: ["tests/system/update-smoke.mjs"],
    invokedFiles: ["tests/system/update-smoke.mjs"],
  });
  assert.deepEqual(result.observedFiles, ["tests/system/update-smoke.mjs"]);
  assert.deepEqual(result.invokedFiles, ["tests/system/update-smoke.mjs"]);
  assert.equal(result.observedCases[0].id, "update:swap-ready");
  assert.equal(result.observedCases[0].status, "skipped-by-engine");
});

test("TRX 从 UnitTest 的 TestMethod 身份映射实际源码文件", () => {
  const trx = [
    "<TestDefinitions>",
    '<UnitTest name="NexusPipeline.Tests.UpdateTests.SwapReady" storage="x.dll" id="trx-1">',
    '<TestMethod codeBase="x.dll" adapterTypeName="x" className="NexusPipeline.Tests.UpdateTests" name="SwapReady" />',
    "</UnitTest>",
    "</TestDefinitions>",
    "<Results>",
    '<UnitTestResult testId="trx-1" testName="NexusPipeline.Tests.UpdateTests.SwapReady" outcome="Passed" />',
    "</Results>",
    '<ResultSummary><Counters total="1" executed="1" passed="1" failed="0" error="0" timeout="0" aborted="0" inconclusive="0" passedButRunAborted="0" notExecuted="0" /></ResultSummary>',
  ].join("");
  const result = parseTrxResults(trx, {
    expectedFiles: ["tests/NexusPipeline.Tests/UpdateTests.cs"],
    invokedFiles: ["tests/NexusPipeline.Tests/UpdateTests.cs"],
  });
  assert.deepEqual(result.observedFiles, ["tests/NexusPipeline.Tests/UpdateTests.cs"]);
  assert.equal(result.observedCases[0].file, "tests/NexusPipeline.Tests/UpdateTests.cs");
});

test("Vitest 绝对 suite 路径只回写到已计划的相对文件", () => {
  const result = parseVitestResults({
    testResults: [{
      name: "D:\\build\\frontend\\contracts\\official-plugins.test.ts",
      assertionResults: [{ status: "passed", title: "mounts plugin" }],
    }],
  }, {
    expectedFiles: ["frontend/contracts/official-plugins.test.ts"],
    invokedFiles: ["frontend/contracts/official-plugins.test.ts"],
  });
  assert.deepEqual(result.observedFiles, ["frontend/contracts/official-plugins.test.ts"]);
});
