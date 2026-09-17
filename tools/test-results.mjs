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
  return validateTestCounts({ passed: attrs.passed, failed: attrs.failed + (attrs.error || 0) + (attrs.timeout || 0) + (attrs.aborted || 0), skipped: attrs.notExecuted });
}

export function parseVitestResults(value) {
  const cases = (value.testResults || []).flatMap(suite => suite.assertionResults || []);
  const passed = cases.filter(item => item.status === "passed").length;
  const failed = cases.filter(item => item.status === "failed").length;
  const skipped = cases.filter(item => ["pending", "skipped", "todo", "disabled"].includes(item.status)).length;
  if (passed + failed + skipped !== cases.length) throw new Error("Vitest 存在未知测试状态");
  return validateTestCounts({ passed, failed, skipped });
}

export function parsePlaywrightResults(value) {
  const stats = value.stats || {};
  return validateTestCounts({ passed: stats.expected, failed: stats.unexpected + stats.flaky, skipped: stats.skipped });
}
