import assert from "node:assert/strict";
import test from "node:test";
import { classifyFast, parseNameStatusZ } from "../../tools/ci-scope.mjs";

test("fast scope treats only an explicit nonempty documentation diff as docs-only", () => {
  assert.equal(classifyFast(["README.md", "docs/testing/commands.md"]).docs_only, true);
  for (const paths of [null, [], ["AGENTS.md"], ["docs/testing/commands.md", "src/Host/Program.cs"], ["unknown.txt"]]) {
    assert.equal(classifyFast(paths).docs_only, false);
  }
});

test("renames classify both old and new paths", () => {
  const docsRename = parseNameStatusZ("R100\0docs/old.md\0docs/new.md\0");
  assert.equal(classifyFast(docsRename).docs_only, true);
  const sourceRename = parseNameStatusZ("R100\0docs/old.md\0src/Host/Program.cs\0");
  assert.equal(classifyFast(sourceRename).docs_only, false);
});
