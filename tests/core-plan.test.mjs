import test from "node:test";
import assert from "node:assert/strict";
import fs from "node:fs";
import path from "node:path";
import { coreUnits } from "./core-plan.mjs";
const root = path.resolve(import.meta.dirname, "..");
const registry = JSON.parse(fs.readFileSync(path.join(root, "tests/gates.json")));
const policy = JSON.parse(fs.readFileSync(path.join(root, "tests/policy.json")));

test("all applicable obligations have exactly one predeclared native provider", () => {
  const selected = registry.gates.flatMap(gate => ["scope", "aggregate", "release"].includes(gate.kind) ? []
    : gate.kind === "matrix-template" ? Object.entries(policy.plugins).flatMap(([name, item]) => {
      const dimension = gate.id.split(".").at(-1);
      const applicable = item.kind === "data-specialized" ? ["contract", "package", "adapter"].includes(dimension)
        : dimension !== "adapter" || name === "MaaFrameworkDriver";
      return applicable ? [{ id: `${gate.id}:${name}` }] : [];
    }) : [{ id: gate.id }]);
  const units = coreUnits(selected, registry, policy);
  assert.deepEqual(units.flatMap(unit => unit.provides).sort(), selected.map(item => item.id).sort());
  if (registry.repository === "Host") {
    const backend = units.find(unit => unit.id === "host.backend");
    assert.deepEqual(backend.expectedCaseIds, [...new Set(Object.entries(policy.groups).filter(([group]) => group.startsWith("backend.")).flatMap(([, item]) => item.caseIds))].sort());
    assert.ok(backend.replacementEvidence);
  } else {
    for (const [name, plugin] of Object.entries(policy.plugins)) {
      const unit = units.find(unit => unit.id === `plugins.runtime:${name}`);
      if (plugin.kind === "data-specialized") {
        assert.deepEqual(unit.expectedCaseIds, [...plugin.fixtureIds].sort());
        assert.equal(unit.observeCount, 12); assert.equal(unit.isolatedRunRequired, true);
        assert.equal(unit.expectedEditorCaseIds.length, ["BetterGI", "ZenlessZoneZeroOneDragon"].includes(name) ? 2 : 0);
      } else assert.deepEqual(unit.expectedMethodIds, [...plugin.expectedMethods].sort());
    }
  }
});
test("unregistered obligations fail before execution", () => {
  assert.throws(() => coreUnits([{id:"unknown"}], registry, policy));
});
