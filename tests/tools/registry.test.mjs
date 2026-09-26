import assert from "node:assert/strict";
import fs from "node:fs";
import path from "node:path";
import { fileURLToPath } from "node:url";
import test from "node:test";
import {
  FRONTEND_TEST_GROUPS,
  GOVERNANCE_DOMAINS,
  HOST_TEST_AREAS,
  SYSTEM_TEST_GROUPS,
  TIMING_TESTS,
  TEST_DOMAIN_REGISTRY,
  systemRuntimeName,
  selectHostTestFiles,
  validateRegistry,
} from "../../tests/registry.mjs";

const ROOT = path.resolve(path.dirname(fileURLToPath(import.meta.url)), "../..");
const PROOF_FIELDS = new Set([
  "physicalJobId",
  "logicalGroupId",
  "selectedGroups",
  "manifest",
  "planDigest",
  "resultProof",
]);

function assertNoProofFields(value, location = "registry") {
  if (!value || typeof value !== "object") return;
  for (const [key, nested] of Object.entries(value)) {
    assert.equal(PROOF_FIELDS.has(key), false, `${location} contains retired proof field ${key}`);
    assertNoProofFields(nested, `${location}.${key}`);
  }
}

test("registry is unique and contains only runnable, existing suites", () => {
  assert.equal(validateRegistry(), true);
  assertNoProofFields(TEST_DOMAIN_REGISTRY);
  for (const group of [...HOST_TEST_AREAS, ...FRONTEND_TEST_GROUPS, ...GOVERNANCE_DOMAINS]) {
    assert.ok(group.key);
    assert.ok(Array.isArray(group.testPaths));
  }
  for (const group of SYSTEM_TEST_GROUPS) {
    for (const suitePath of group.suitePaths) {
      assert.equal(fs.existsSync(path.join(ROOT, suitePath)), true, `${group.key}: ${suitePath}`);
    }
  }
  for (const timing of TIMING_TESTS) {
    assert.equal(fs.existsSync(path.join(ROOT, timing.suitePath)), true, `${timing.key}: ${timing.suitePath}`);
    assert.ok(timing.namePattern);
  }
});

test("real-clock acceptance selects explicit timing cases instead of whole system groups", () => {
  assert.deepEqual(TIMING_TESTS.map(item => item.key), ["update", "execution"]);
  for (const timing of TIMING_TESTS) {
    assert.match(timing.runtimeName, /-timing$/u);
    assert.ok(SYSTEM_TEST_GROUPS.some(group => group.suitePaths.includes(timing.suitePath)));
    assert.equal(timing.caseIds.length, timing.key === "execution" ? 5 : 2);
    assert.equal(new Set(timing.caseIds).size, timing.caseIds.length);
    if (timing.key === "execution") assert.deepEqual(timing.caseIds,
      ["ER07", "ER10", "ER14 stdout only", "ER14 stderr only", "EX01"]);
  }
});

test("host test selection returns concrete files once across overlapping areas", () => {
  const realtime = "tests/NexusPipeline.Tests/Execution/RealtimeEventBusTests.cs";
  assert.equal(fs.existsSync(path.join(ROOT, realtime)), true);
  assert.deepEqual(selectHostTestFiles(["execution", "control"], [realtime, realtime]), [realtime]);
});

test("H5 real-time phases use distinct runtime directories", () => {
  for (const name of ["runtime-startup-update", "runtime-update", "runtime-execution-resilience"]) {
    const accelerated = systemRuntimeName(name, "accelerated");
    const updateRealtime = systemRuntimeName(name, "update-realtime");
    const executionRealtime = systemRuntimeName(name, "execution-realtime");
    assert.equal(accelerated, name);
    assert.notEqual(accelerated, updateRealtime);
    assert.notEqual(accelerated, executionRealtime);
    assert.match(updateRealtime, /^[A-Za-z0-9_-]+$/u);
    assert.match(executionRealtime, /^[A-Za-z0-9_-]+$/u);
  }
});
test("registry does not treat shared fixtures or directories as a test file", () => {
  const paths = JSON.stringify(TEST_DOMAIN_REGISTRY);
  assert.doesNotMatch(paths, /tests[\\/]fixtures[\\/][^" ]+\.csproj/u);
  assert.doesNotMatch(paths, /testPaths[^\n]*tests[\\/]fixtures[\\/]/u);
  for (const group of [...HOST_TEST_AREAS, ...FRONTEND_TEST_GROUPS, ...GOVERNANCE_DOMAINS]) {
    for (const testPath of group.testPaths) assert.doesNotMatch(testPath, /(?:^|[\\/])(?:bin|obj|node_modules)(?:[\\/]|$)/u);
  }
});
