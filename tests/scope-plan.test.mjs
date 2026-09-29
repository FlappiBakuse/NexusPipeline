import test from "node:test";
import assert from "node:assert/strict";
import fs from "node:fs";
import os from "node:os";
import path from "node:path";
import { execFileSync } from "node:child_process";
import { collectChanges, parseNameStatus, planForChanges, readRegistry } from "./scope-plan.mjs";

const root = path.resolve(import.meta.dirname, "..");
const { registry } = readRegistry(root);
const policy = JSON.parse(fs.readFileSync(path.join(root, "tests/policy.json"), "utf8"));
const ids = changes => planForChanges(root, changes, registry, policy).selected.map(item => item.id);
const change = name => [{ status: "M", path: name }];

test("host scope keeps documentation, installer, backend and frontend owners separate", () => {
  const docs = ids(change("docs/user/README.en.md"));
  assert.deepEqual(docs, ["host.docs", "host.required", "host.scope"]);
  const packageText = ids(change("README.md"));
  assert.ok(packageText.includes("host.release-contract"));
  assert.ok(!packageText.some(id => id.startsWith("host.backend.")));
  const update = ids(change("src/Modules/Updates/UpdateApply.cs"));
  for (const id of ["host.backend.updates-restart", "host.architecture.backend", "host.integration.restart-update"]) assert.ok(update.includes(id), id);
  const frontend = ids(change("frontend/src/platform/service-restart.ts"));
  for (const id of ["host.frontend.state", "host.frontend.typecheck", "host.frontend.build", "host.integration.restart-update"]) assert.ok(frontend.includes(id), id);
  assert.ok(!frontend.some(id => id.startsWith("host.backend.")));
  const mixed = ids([...change("docs/README.md"), ...change("frontend/src/platform/plugin-list.ts")]);
  assert.ok(mixed.includes("host.docs") && mixed.includes("host.frontend.state"));
});

test("unknown backend source widens selection and identifies policy owner", () => {
  const selected = ids(change("src/NewUnknown/Feature.cs"));
  assert.ok(selected.includes("host.ci-policy"));
  assert.ok(selected.includes("host.architecture.backend"));
  assert.ok(selected.includes("host.backend.execution"));
});

test("git name-status parser retains both rename paths and deletions", () => {
  assert.deepEqual(parseNameStatus(Buffer.from("R100\0old.cs\0new.cs\0D\0gone.cs\0")), [
    { status: "R", old: "old.cs", path: "new.cs" }, { status: "D", path: "gone.cs" },
  ]);
});

test("scope reads a real git rename, deletion and untracked input", () => {
  const fixtureRoot = fs.mkdtempSync(path.join(process.env.NEXUS_TEST_ARTIFACT_ROOT || os.tmpdir(), "host-scope-"));
  const git = (...args) => execFileSync("git", ["-C", fixtureRoot, ...args], { encoding: "utf8" }).trim();
  try {
    git("init", "-q"); git("config", "user.name", "Scope Test"); git("config", "user.email", "scope@example.invalid");
    fs.writeFileSync(path.join(fixtureRoot, "old.cs"), "class Old {}\n");
    fs.writeFileSync(path.join(fixtureRoot, "gone.cs"), "class Gone {}\n");
    git("add", "."); git("commit", "-qm", "base");
    const base = git("rev-parse", "HEAD");
    git("mv", "old.cs", "new.cs"); git("rm", "gone.cs"); git("commit", "-qm", "change");
    fs.writeFileSync(path.join(fixtureRoot, "untracked.cs"), "class New {}\n");
    const plan = collectChanges(fixtureRoot, { base, includeWorkingTree: true });
    assert.equal(plan.baseSha, base);
    assert.equal(plan.mergeBase, base);
    assert.equal(plan.dirty, true);
    assert.ok(plan.changes.some(item => item.old === "old.cs" && item.path === "new.cs"));
    assert.ok(plan.changes.some(item => item.status === "D" && item.path === "gone.cs"));
    assert.ok(plan.changes.some(item => item.untracked && item.path === "untracked.cs"));
  } finally { fs.rmSync(fixtureRoot, { recursive: true, force: true }); }
});

const selectionCases = JSON.parse(fs.readFileSync(path.join(root, "tests/selection-cases.json"), "utf8")).cases;
const matches = (id, expected) => new RegExp(`^${expected.replaceAll(/[.*+?^${}()|[\]\\]/g, "\\$&").replaceAll("\\*", ".*")}$`).test(id);
for (const scenario of selectionCases) {
  test(`${scenario.id} ${scenario.reason}`, () => {
    const changes = scenario.changes.map(input => typeof input === "string" ? { status: "M", path: input }
      : { status: input.status, old: input.old, path: input.new ?? input.path });
    const selected = ids(changes);
    for (const expected of scenario.mustInclude)
      assert.ok(selected.some(id => matches(id, expected)), `${scenario.id}: missing ${expected}`);
    for (const excluded of scenario.mustExclude)
      assert.ok(!selected.some(id => matches(id, excluded)), `${scenario.id}: unexpectedly selected ${excluded}`);
  });
}
