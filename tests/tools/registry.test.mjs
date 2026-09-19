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
  TEST_DOMAIN_REGISTRY,
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
});

test("registry does not treat shared fixtures or directories as a test file", () => {
  const paths = JSON.stringify(TEST_DOMAIN_REGISTRY);
  assert.doesNotMatch(paths, /tests[\\/]fixtures[\\/][^" ]+\.csproj/u);
  assert.doesNotMatch(paths, /testPaths[^\n]*tests[\\/]fixtures[\\/]/u);
  for (const group of [...HOST_TEST_AREAS, ...FRONTEND_TEST_GROUPS, ...GOVERNANCE_DOMAINS]) {
    for (const testPath of group.testPaths) assert.doesNotMatch(testPath, /(?:^|[\\/])(?:bin|obj|node_modules)(?:[\\/]|$)/u);
  }
});

