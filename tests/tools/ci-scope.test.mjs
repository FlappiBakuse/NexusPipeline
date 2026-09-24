import assert from "node:assert/strict";
import fs from "node:fs";
import path from "node:path";
import { fileURLToPath } from "node:url";
import test from "node:test";
import { classifyFast, parseNameStatusZ } from "../../tools/ci-scope.mjs";
import { selectHostTestFiles } from "../../tests/registry.mjs";

const hostTests = [
  "tests/NexusPipeline.Tests/Execution/RealtimeEventBusTests.cs",
  "tests/NexusPipeline.Tests/Execution/LogMonitorTests.cs",
  "tests/NexusPipeline.Tests/Platform/JsonStoreTests.cs",
  "tests/NexusPipeline.Tests/Configuration/ConfigSwapSyncTests.cs",
];
const root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), "../..");

test("scope fixtures refer to existing repository test files", () => {
  for (const file of hostTests) assert.equal(fs.existsSync(path.join(root, file)), true, file);
});

test("fast scope treats only an explicit nonempty documentation diff as docs-only", () => {
  assert.equal(classifyFast(["README.md", "docs/testing/commands.md"]).docs_only, true);
  for (const paths of [null, [], ["AGENTS.md"], ["docs/testing/commands.md", "src/Host/Program.cs"], ["unknown.txt"]]) {
    assert.equal(classifyFast(paths).docs_only, false);
  }
});

test("fast scope selects documented frontend backend and conservative tool plans", () => {
  const docs = classifyFast(["docs/testing/commands.md"]);
  assert.equal(docs.docs, true);
  assert.equal(docs.unit_groups.length, 0);
  assert.equal(docs.frontend_groups.length, 0);

  const frontend = classifyFast(["frontend/src/ui/button.ts", "frontend/src/ui/button.test.ts"]);
  assert.deepEqual(frontend.frontend_groups, ["ui"]);
  assert.equal(frontend.architecture, false);

  const backend = classifyFast(["src/Modules/Settings/SettingsService.cs"]);
  assert.deepEqual(backend.unit_groups, ["config"]);
  assert.equal(backend.architecture, true);

  const sdk = classifyFast(["src/NexusPipeline.Plugin.Abstractions/PluginApi.cs"]);
  assert.deepEqual(sdk.frontend_groups, ["bridge"]);
  assert.equal(sdk.contracts, true);
  assert.equal(sdk.needs_plugins, true);

  const sharedTool = classifyFast(["tools/source-hash.mjs"]);
  assert.equal(sharedTool.full, true);
  assert.equal(sharedTool.tooling, true);
  assert.equal(sharedTool.needs_plugins, true);
});

test("renames classify both old and new paths", () => {
  const docsRename = parseNameStatusZ("R100\0docs/old.md\0docs/new.md\0");
  assert.equal(classifyFast(docsRename).docs_only, true);
  const sourceRename = parseNameStatusZ("R100\0docs/old.md\0src/Host/Program.cs\0");
  assert.equal(classifyFast(sourceRename).docs_only, false);
});

test("overlapping source owners select the actual Realtime and Monitoring tests", () => {
  const realtime = classifyFast(["src/Modules/Execution/Realtime/RealtimeEventBus.cs"]);
  assert.ok(realtime.unit_groups.includes("control"));
  assert.ok(selectHostTestFiles(realtime.unit_groups, hostTests).includes(hostTests[0]));

  const monitoring = classifyFast(["src/Modules/Execution/Monitoring/LogMonitor.cs"]);
  assert.deepEqual(monitoring.unit_groups, ["execution", "judgement", "observability"]);
  assert.ok(selectHostTestFiles(monitoring.unit_groups, hostTests).includes(hostTests[1]));
});

test("persistence paths retain module ownership and renamed endpoints deduplicate files", () => {
  const persisted = classifyFast(["src/Modules/Configuration/Snapshots/ConfigSnapshotService.cs"]);
  assert.deepEqual(persisted.unit_groups, ["config", "persistence"]);
  assert.ok(selectHostTestFiles(persisted.unit_groups, hostTests).includes(hostTests[2]));

  const endpoints = parseNameStatusZ("R100\0src/Modules/Configuration/Snapshots/ConfigSnapshotService.cs\0src/Modules/Execution/Realtime/RealtimeEventBus.cs\0D\0src/Modules/Execution/Monitoring/LogMonitor.cs\0");
  const files = selectHostTestFiles(classifyFast(endpoints).unit_groups, [...hostTests, hostTests[0]]);
  assert.equal(files.filter(file => file === hostTests[0]).length, 1);
  assert.ok(files.includes(hostTests[1]));
  assert.ok(files.includes(hostTests[2]));
});

test("every full fallback prepares the dependencies used by the default runner", () => {
  for (const paths of [null, [], ["unknown.txt"], ["tools/ci-scope.mjs"]]) {
    const plan = classifyFast(paths);
    assert.equal(plan.full, true);
    assert.equal(plan.contracts, true);
    assert.equal(plan.needs_plugins, true);
    assert.equal(plan.tooling, true);
    assert.equal(plan.architecture, true);
  }
  for (const paths of [["docs/testing/commands.md"], ["frontend/src/ui/button.ts"]]) {
    const plan = classifyFast(paths);
    assert.equal(plan.full, false);
    assert.equal(plan.needs_plugins, false);
  }
});
