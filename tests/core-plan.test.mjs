import test from "node:test";
import assert from "node:assert/strict";
import fs from "node:fs";
import path from "node:path";
import { coreUnits, requiresPartner, allocateUnits } from "./core-plan.mjs";
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

test("partner editor evidence follows the fixed plugin policy", () => {
  const plugin = { kind: "data-specialized", fixtureIds: ["daily"], realScenario: "P-S-MaaStellaSora" };
  const plan = entry => coreUnits([{ id: "host.partner-jint" }], registry, policy,
    { plugins: { MaaStellaSora: entry } })[0];
  assert.deepEqual(plan(plugin).expectedEditorCaseIds, []);
  const cases = ["MaaStellaSora.editor-select-and-preserve", "MaaStellaSora.editor-repeat"];
  assert.deepEqual(plan({ ...plugin, editorCaseIds: cases }).expectedEditorCaseIds, [...cases].sort());
  assert.throws(() => plan({ ...plugin, editorCaseIds: ["invented"] }));
});

test("finite subsets retain exactly their selected independent scenarios", () => {
  for (const groups of [["config"], ["control", "execution"], ["config", "control", "execution"]]) {
    const selected = groups.map(group => ({ id: `host.integration.${group}` }));
    const [unit] = coreUnits(selected, registry, policy);
    assert.equal(unit.kind, "finite");
    assert.deepEqual(unit.groups, [...groups].sort());
    assert.deepEqual(unit.provides, selected.map(item => item.id).sort());
    assert.deepEqual(unit.expectedScenarioIds, selected.map(item => policy.integrationScenarios[item.id]).sort());
    assert.equal(unit.preparations.includes("host.browser"), groups.includes("execution"));
  }
});

test("full application obligations resolve a fixed partner before execution", () => {
  for (const id of ["host.architecture.backend", "host.integration.desktop", "host.integration.restart-update"]) {
    const [unit] = coreUnits([{ id }], registry, policy);
    assert.equal(requiresPartner(unit), true, id);
    assert.ok(unit.preparations.includes("host.application.inputs"));
  }
  const [unit] = coreUnits([{ id: "host.integration.schedule" }], registry, policy);
  assert.equal(requiresPartner(unit), false);
});

test("concurrent restart and store observations retain both required providers", () => {
  const selected = ["host.integration.restart-update", "host.integration.store"].map(id => ({ id }));
  const units = coreUnits(selected, registry, policy);
  const plan = allocateUnits(units, {}, policy.ciBatchPolicy);
  assert.equal(plan.capacityStatus, "PLANNED");
  assert.equal(plan.batches.length, 1);
  assert.deepEqual(plan.batches[0].units.flatMap(unit => unit.provides).sort(), selected.map(item => item.id).sort());
  const separate = units.map(unit => allocateUnits([unit], {}, policy.ciBatchPolicy).batches[0].estimatedMs);
  assert.ok(plan.batches[0].estimatedMs < separate.reduce((a, b) => a + b, 0));
});
