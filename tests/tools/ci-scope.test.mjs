import assert from "node:assert/strict";
import test from "node:test";
import { classifyFast, parseNameStatusZ } from "../../tools/ci-scope.mjs";

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
