function normalizePath(value) {
  if (typeof value !== "string") return "";
  const normalized = value.trim().replaceAll("\\", "/").replace(/^\.\//u, "");
  if (!normalized || normalized.startsWith("/") || /^[A-Za-z]:\//u.test(normalized)) return "";
  if (normalized.split("/").some(segment => segment === "..")) return "";
  return normalized;
}

function unique(values) {
  return [...new Set((Array.isArray(values) ? values : []).map(normalizePath).filter(Boolean))].sort();
}

function fileFromText(value, candidates) {
  const normalized = String(value || "").trim().replaceAll("\\", "/");
  if (!normalized) return "";
  const files = unique(candidates);
  return files.find(file => normalized === file || normalized.endsWith(`/${file}`) || file.endsWith(`/${normalized}`)) || "";
}

function caseId(title, fallback) {
  const value = String(title || "").trim();
  const stablePrefix = /^([A-Za-z][A-Za-z0-9_-]*:[A-Za-z][A-Za-z0-9_-]*)\b/u.exec(value)?.[1];
  return stablePrefix || value || String(fallback || "");
}

function withObservation(result, options, observedCases = [], fileHints = []) {
  if (options === undefined) return result;
  const expectedFiles = unique(options?.expectedFiles);
  const invokedFiles = unique(options?.invokedFiles);
  const observedFiles = unique(fileHints).filter(file => expectedFiles.length === 0 || expectedFiles.includes(file));
  if (observedFiles.length === 0 && expectedFiles.length === 1 && result.testCount > 0) observedFiles.push(expectedFiles[0]);
  return {
    ...result,
    observedFiles,
    observedCases: observedCases.map((item, index) => ({
      id: caseId(item.id || item.title, `${index + 1}`),
      title: String(item.title || item.id || ""),
      status: String(item.status || "unknown"),
      ...(item.file ? { file: normalizePath(item.file) } : {}),
      ...(item.testId ? { testId: String(item.testId) } : {}),
    })),
    invokedFiles,
  };
}

/** Read counts emitted by the test engines; discovery and build steps are not test results. */
export function validateTestCounts(value) {
  const { passed, failed, skipped } = value;
  if (![passed, failed, skipped].every(n => Number.isSafeInteger(n) && n >= 0)) throw new Error("无效测试结果计数");
  const testCount = passed + failed + skipped;
  if (!testCount || !passed && !failed) throw new Error("所选测试未执行有效用例");
  return { testCount, passed, failed, skipped };
}

function parseTapCases(text, expectedFiles) {
  const cases = [];
  const fileHints = [];
  for (const match of String(text).matchAll(/^\s*# Subtest:\s*(.+?)\s*$/gmu)) {
    const title = match[1].trim();
    const file = fileFromText(title, expectedFiles) || (/\.(?:m?js|c?js|ts)$/u.test(title) ? normalizePath(title) : "");
    if (file) fileHints.push(file);
  }
  for (const match of String(text).matchAll(/^\s*(ok|not ok)\s+(?:\d+\s*)?(?:-\s*)?(.*?)(?:\s+#\s*(SKIP|TODO)\b.*)?\s*$/gmu)) {
    const statusWord = match[1];
    const title = match[2].trim();
    const directive = String(match[3] || "").toUpperCase();
    const status = directive === "SKIP" || directive === "TODO"
      ? "skipped-by-engine"
      : statusWord === "ok" ? "passed" : "failed";
    const file = fileFromText(title, expectedFiles);
    cases.push({ id: caseId(title, cases.length + 1), title, status, ...(file ? { file } : {}) });
  }
  return { cases, fileHints };
}

export function parseTapResults(text, options) {
  const read = key => {
    const values = [...String(text).matchAll(new RegExp(`^# ${key} (\\d+)\\s*$`, "gm"))];
    if (values.length !== 1) throw new Error(`TAP 缺少唯一 ${key} 计数`);
    return Number(values[0][1]);
  };
  const result = validateTestCounts({ passed: read("pass"), failed: read("fail") + read("cancelled"), skipped: read("skipped") + read("todo") });
  if (options === undefined) return result;
  const parsed = parseTapCases(text, options.expectedFiles || []);
  return withObservation(result, options, parsed.cases, parsed.fileHints);
}

/** Require every registered real-clock case to appear exactly once and pass. */
export function validateTimingSelection(result, caseIds) {
  if (!Array.isArray(caseIds) || caseIds.length === 0 || new Set(caseIds).size !== caseIds.length) {
    throw new Error("Timing 预期用例身份无效");
  }
  if (result.failed !== 0 || result.skipped !== 0) {
    throw new Error(`Timing 所选用例失败或跳过/TODO：failed=${result.failed} skipped=${result.skipped}`);
  }
  const observed = result.observedCases;
  if (!Array.isArray(observed)) throw new Error("Timing 缺少原生用例结果");
  for (const id of caseIds) {
    const matches = observed.filter(item => item.title === id
      || item.title.startsWith(`${id} `) || item.title.startsWith(`${id}：`));
    if (matches.length !== 1) throw new Error(`Timing 用例缺失或重复：${id}`);
    if (matches[0].status !== "passed") throw new Error(`Timing 用例状态无效：${id}=${matches[0].status}`);
  }
  if (observed.length !== caseIds.length || result.testCount !== caseIds.length || result.passed !== caseIds.length) {
    throw new Error(`Timing 所选用例结果不完整：expected=${caseIds.length} observed=${observed.length} passed=${result.passed}`);
  }
  return true;
}

function parseTrxCases(text, expectedFiles) {
  const testNames = new Map();
  const source = String(text);
  for (const match of source.matchAll(/<UnitTest\b([^>]*?)(?:\/>|>([\s\S]*?)<\/UnitTest>)/gu)) {
    const attrs = Object.fromEntries([...match[1].matchAll(/([A-Za-z][A-Za-z0-9_]*)="([^"]*)"/gu)].map(item => [item[1], item[2]]));
    const body = match[2] || "";
    const testMethod = /<TestMethod\b([^>]*?)(?:\/>|>)/u.exec(body)?.[1] || "";
    const methodAttrs = Object.fromEntries([...testMethod.matchAll(/([A-Za-z][A-Za-z0-9_]*)="([^"]*)"/gu)].map(item => [item[1], item[2]]));
    const identity = attrs.id || attrs.testId || attrs.name || "";
    if (identity) testNames.set(identity, { ...attrs, ...methodAttrs });
  }
  const cases = [];
  const fileHints = [];
  for (const match of source.matchAll(/<UnitTestResult\b([^>]*?)(?:\/>|>(?:[\s\S]*?)<\/UnitTestResult>)/gu)) {
    const attrs = Object.fromEntries([...match[1].matchAll(/([A-Za-z][A-Za-z0-9_]*)="([^"]*)"/gu)].map(item => [item[1], item[2]]));
    const definition = testNames.get(attrs.testId) || {};
    const title = attrs.testName || definition.name || attrs.testId || "";
    const className = definition.testMethod || definition.className || "";
    const classLeaf = String(className).split(".").pop() || "";
    const exactFile = expectedFiles.find(candidate => candidate.split("/").pop()?.replace(/\.cs$/u, "") === classLeaf) || "";
    const file = fileFromText(`${className} ${title}`, expectedFiles)
      || exactFile
      || expectedFiles.find(candidate => candidate.replace(/\.cs$/u, "").endsWith(classLeaf))
      || "";
    if (file) fileHints.push(file);
    const outcome = String(attrs.outcome || "").toLowerCase();
    const status = ["notexecuted", "inconclusive", "skipped"].includes(outcome)
      ? "skipped-by-engine"
      : ["failed", "error", "timeout", "aborted"].includes(outcome) ? "failed" : "passed";
    cases.push({ id: title, title, status, ...(file ? { file } : {}), testId: attrs.testId || "" });
  }
  return { cases, fileHints };
}

export function parseTrxResults(text, options) {
  const counters = /<Counters\s+([^>]+)\/?\s*>/u.exec(text)?.[1];
  if (!counters) throw new Error("TRX 缺少 Counters");
  const attrs = Object.fromEntries([...counters.matchAll(/(\w+)="(\d+)"/gu)].map(m => [m[1], Number(m[2])]));
  const failed = attrs.failed + (attrs.error || 0) + (attrs.timeout || 0) + (attrs.aborted || 0);
  const skipped = (attrs.notExecuted || 0) + (attrs.inconclusive || 0);
  if (!Number.isSafeInteger(attrs.total) || attrs.total !== attrs.passed + failed + skipped) {
    throw new Error("TRX Counters total 与状态计数不守恒");
  }
  const result = validateTestCounts({ passed: attrs.passed, failed, skipped });
  if (options === undefined) return result;
  const parsed = parseTrxCases(text, options.expectedFiles || []);
  return withObservation(result, options, parsed.cases, parsed.fileHints);
}

export function parseVitestResults(value, options) {
  const cases = (value.testResults || []).flatMap(suite => (suite.assertionResults || []).map(item => ({
    ...item,
    file: suite.name || suite.file || "",
  })));
  const passed = cases.filter(item => item.status === "passed").length;
  const failed = cases.filter(item => item.status === "failed").length;
  const skipped = cases.filter(item => ["pending", "skipped", "todo", "disabled"].includes(item.status)).length;
  if (passed + failed + skipped !== cases.length) throw new Error("Vitest 存在未知测试状态");
  if (value.numTotalTests !== undefined && value.numTotalTests !== cases.length) throw new Error("Vitest total 与测试用例不一致");
  if (value.numPassedTests !== undefined && value.numPassedTests !== passed) throw new Error("Vitest passed 与测试用例不一致");
  if (value.numFailedTests !== undefined && value.numFailedTests !== failed) throw new Error("Vitest failed 与测试用例不一致");
  if (value.numPendingTests !== undefined && value.numPendingTests !== skipped) throw new Error("Vitest skipped 与测试用例不一致");
  const result = validateTestCounts({ passed, failed, skipped });
  if (options === undefined) return result;
  const expectedFiles = options.expectedFiles || [];
  const observedCases = cases.map(item => ({
    id: [...(item.ancestorTitles || []), item.title || item.fullName || ""].join(" > "),
    title: item.fullName || item.title || "",
    status: ["pending", "skipped", "todo", "disabled"].includes(item.status) ? "skipped-by-engine" : item.status,
    file: fileFromText(item.file, expectedFiles) || item.file,
  }));
  const fileHints = cases.map(item => fileFromText(item.file, expectedFiles)).filter(Boolean);
  return withObservation(result, options, observedCases, fileHints);
}

function collectPlaywrightCases(value, expectedFiles, cases = [], fileHints = [], titlePath = []) {
  if (!value || typeof value !== "object") return { cases, fileHints };
  const file = fileFromText(value.file, expectedFiles);
  if (file) fileHints.push(file);
  const currentTitles = value.title ? [...titlePath, String(value.title)] : titlePath;
  for (const spec of value.specs || []) {
    const specFile = fileFromText(spec.file || value.file, expectedFiles) || file;
    if (specFile) fileHints.push(specFile);
    for (const test of spec.tests || []) {
      const status = String(test.status || (test.results || []).at(-1)?.status || "").toLowerCase();
      const normalizedStatus = ["skipped", "pending"].includes(status)
        ? "skipped-by-engine"
        : ["unexpected", "failed", "flaky"].includes(status) ? "failed" : "passed";
      const title = [...currentTitles, ...(test.titlePath || []), test.title || ""].filter(Boolean).join(" > ");
      cases.push({ id: title, title, status: normalizedStatus, ...(specFile ? { file: specFile } : {}) });
    }
  }
  for (const child of value.suites || []) collectPlaywrightCases(child, expectedFiles, cases, fileHints, currentTitles);
  return { cases, fileHints };
}

export function parsePlaywrightResults(value, options) {
  const stats = value.stats || {};
  const failed = stats.unexpected + stats.flaky;
  const skipped = stats.skipped;
  if (stats.total !== undefined && stats.total !== stats.expected + failed + skipped) {
    throw new Error("Playwright total 与状态计数不守恒");
  }
  const result = validateTestCounts({ passed: stats.expected, failed, skipped });
  if (options === undefined) return result;
  const parsed = collectPlaywrightCases(value, options.expectedFiles || []);
  return withObservation(result, options, parsed.cases, parsed.fileHints);
}
