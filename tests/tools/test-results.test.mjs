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
