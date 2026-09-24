import assert from "node:assert/strict";
import { spawnSync } from "node:child_process";
import path from "node:path";
import { fileURLToPath } from "node:url";
import test from "node:test";
import { parseTapResults, validateTimingSelection } from "../../tools/test-results.mjs";

const root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), "../..");
const expectedCases = ["ER07", "ER10"];
const fixtureRelative = "tests/fixtures/timing-selection.mjs";
const fixture = path.join(root, fixtureRelative);

function nativeTimingResult(scenario) {
  const env = { ...process.env, TIMING_SCENARIO: scenario };
  delete env.NODE_TEST_CONTEXT;
  const native = spawnSync(process.execPath, ["--test", "--test-reporter=tap", "--test-name-pattern", "ER07|ER10", fixture], {
    encoding: "utf8", env,
  });
  assert.equal(native.error, undefined);
  assert.equal(native.status, 0, native.stderr);
  return parseTapResults(native.stdout, { expectedFiles: [fixtureRelative], invokedFiles: [fixtureRelative] });
}

test("Node 24 native timing selection accepts all selected cases and ignores nonselected cases", () => {
  const result = nativeTimingResult("pass");
  assert.equal(result.testCount, 2);
  assert.equal(validateTimingSelection(result, expectedCases), true);
});

test("Node 24 native timing selection rejects selected skip and TODO", () => {
  for (const scenario of ["skip", "todo"]) {
    const result = nativeTimingResult(scenario);
    assert.equal(result.passed, 1);
    assert.throws(() => validateTimingSelection(result, expectedCases), /跳过|TODO|不完整/u);
  }
});

test("Node 24 native timing selection rejects a missing registered case", () => {
  const result = nativeTimingResult("missing");
  assert.equal(result.passed, 1);
  assert.throws(() => validateTimingSelection(result, expectedCases), /ER10|缺失/u);
});

test("timing selection rejects cancelled, zero, corrupt and unknown results", () => {
  assert.throws(() => parseTapResults("# pass 0\n# fail 0\n# cancelled 0\n# skipped 0\n# todo 0\n"));
  assert.throws(() => parseTapResults("broken report"));
  for (const result of [
    { passed: 1, failed: 1, skipped: 0, testCount: 2, observedCases: [{ title: "ER07 case", status: "passed" }, { title: "ER10 case", status: "failed" }] },
    { passed: 2, failed: 0, skipped: 0, testCount: 2, observedCases: [{ title: "ER07 case", status: "passed" }, { title: "ER10 case", status: "unknown" }] },
  ]) assert.throws(() => validateTimingSelection(result, expectedCases));
});
