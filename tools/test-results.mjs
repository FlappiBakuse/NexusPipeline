/** Read counts emitted by the test engines; discovery and build steps are not test results. */
export function validateTestCounts(value) {
  const { passed, failed, skipped } = value;
  if (![passed, failed, skipped].every(n => Number.isSafeInteger(n) && n >= 0)) throw new Error("无效测试结果计数");
  const testCount = passed + failed + skipped;
  if (!testCount || !passed && !failed) throw new Error("所选测试未执行有效用例");
  return { testCount, passed, failed, skipped };
}

export function parseTapResults(text) {
  const read = key => {
    const values = [...text.matchAll(new RegExp(`^# ${key} (\\d+)\\s*$`, "gm"))];
    if (values.length !== 1) throw new Error(`TAP 缺少唯一 ${key} 计数`);
    return Number(values[0][1]);
  };
  return validateTestCounts({ passed: read("pass"), failed: read("fail") + read("cancelled"), skipped: read("skipped") + read("todo") });
}

export function parseTrxResults(text) {
  const counters = /<Counters\s+([^>]+)\/?\s*>/u.exec(text)?.[1];
  if (!counters) throw new Error("TRX 缺少 Counters");
  const attrs = Object.fromEntries([...counters.matchAll(/(\w+)="(\d+)"/gu)].map(m => [m[1], Number(m[2])]));
  const failed = attrs.failed + (attrs.error || 0) + (attrs.timeout || 0) + (attrs.aborted || 0);
  const skipped = (attrs.notExecuted || 0) + (attrs.inconclusive || 0);
  if (!Number.isSafeInteger(attrs.total) || attrs.total !== attrs.passed + failed + skipped) {
    throw new Error("TRX Counters total 与状态计数不守恒");
  }
  return validateTestCounts({ passed: attrs.passed, failed, skipped });
}

export function parseVitestResults(value) {
  const cases = (value.testResults || []).flatMap(suite => suite.assertionResults || []);
  const passed = cases.filter(item => item.status === "passed").length;
  const failed = cases.filter(item => item.status === "failed").length;
  const skipped = cases.filter(item => ["pending", "skipped", "todo", "disabled"].includes(item.status)).length;
  if (passed + failed + skipped !== cases.length) throw new Error("Vitest 存在未知测试状态");
  if (value.numTotalTests !== undefined && value.numTotalTests !== cases.length) throw new Error("Vitest total 与测试用例不一致");
  if (value.numPassedTests !== undefined && value.numPassedTests !== passed) throw new Error("Vitest passed 与测试用例不一致");
  if (value.numFailedTests !== undefined && value.numFailedTests !== failed) throw new Error("Vitest failed 与测试用例不一致");
  if (value.numPendingTests !== undefined && value.numPendingTests !== skipped) throw new Error("Vitest skipped 与测试用例不一致");
  return validateTestCounts({ passed, failed, skipped });
}

export function parsePlaywrightResults(value) {
  const stats = value.stats || {};
  const failed = stats.unexpected + stats.flaky;
  const skipped = stats.skipped;
  if (stats.total !== undefined && stats.total !== stats.expected + failed + skipped) {
    throw new Error("Playwright total 与状态计数不守恒");
  }
  return validateTestCounts({ passed: stats.expected, failed, skipped });
}
