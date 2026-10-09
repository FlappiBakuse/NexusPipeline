import test from "node:test";
import assert from "node:assert/strict";
import fs from "node:fs";
import path from "node:path";
import { execFileSync, spawnSync } from "node:child_process";
import { fileURLToPath } from "node:url";
import { checkContracts, repositoryPath } from "./doc-contracts.mjs";
import { architectureDelta, checkChanges, contextFor, pathRule } from "./governance.mjs";
import { collectChanges } from "../tests/scope-plan.mjs";

const source = path.resolve(path.dirname(fileURLToPath(import.meta.url)), "..");
function fixture(callback) {
  const external = process.env.NEXUS_TEST_ARTIFACT_ROOT;
  assert.ok(external && path.isAbsolute(external), "Explicit external fixture root required");
  const root = fs.mkdtempSync(path.join(external, "governance-"));
  const write = (file, text) => { const target = path.join(root, file); fs.mkdirSync(path.dirname(target), { recursive: true }); fs.writeFileSync(target, text); };
  try {
    for (const file of ["src/NexusPipeline.Plugin.Abstractions/PluginApi.cs", "frontend/src/plugin-bridge/runtime.ts", "docs/reference/plugin-api/managed.md", "docs/reference/plugin-api/frontend.md", "docs/testing/policy.md", "AGENTS.md", "frontend/src/ui/register.ts"])
      write(file, fs.readFileSync(path.join(source, file)));
    const register = fs.readFileSync(path.join(root, "frontend/src/ui/register.ts"), "utf8");
    for (const match of register.matchAll(/^import \w+ from "(\.\/[^\"]+\.vue)";/gm)) write(path.posix.normalize("frontend/src/ui/" + match[1]), "<template><div /></template>");
    write("docs/map.json", JSON.stringify({ schemaVersion: 1, topics: [{ id: "ui", path: "reference/plugin-api/frontend.md", codePaths: ["frontend/src/ui"], testDomains: ["ui"], authorityFor: ["public-ui"] }] }));
    callback(root, write);
  } finally { fs.rmSync(root, { recursive: true, force: true }); }
}

test("T01-T03: designated API claims reject drift and preserve historical/negative text", () => fixture((root, write) => {
  assert.ok(checkContracts(root).every(rule => rule.status === "PASS"));
  write("docs/history.md", "Frontend API 1.5\n");
  write("tests/old.test.ts", 'expect(api("1.5")).toBe(false);');
  assert.ok(checkContracts(root).every(rule => rule.status === "PASS"));
  const file = "docs/testing/policy.md", current = fs.readFileSync(path.join(root, file), "utf8");
  write(file, current.replace("Frontend API 1.6 的宿主外部契约", "Frontend API 1.5 的宿主外部契约"));
  assert.equal(checkContracts(root).find(rule => rule.file === file).status, "FAIL");
  write(file, current.replace("Frontend API 1.6 的宿主外部契约", "当前前端的外部契约"));
  assert.equal(checkContracts(root).find(rule => rule.file === file).status, "REVIEW");
  write("src/NexusPipeline.Plugin.Abstractions/PluginApi.cs", "// cannot extract facts");
  assert.equal(checkContracts(root).at(-1).status, "NOT_CHECKED");
}));

test("T04-T05: normalized internal parent path is allowed; outside/missing targets fail before reading", () => fixture((root, write) => {
  write("CONTRIBUTING.md", "# Guide\n");
  assert.equal(repositoryPath(root, "docs", "../CONTRIBUTING.md"), path.join(root, "CONTRIBUTING.md"));
  assert.throws(() => repositoryPath(root, "docs", "../../external.md"));
  assert.throws(() => repositoryPath(root, "docs", "missing.md"));
  write("AGENTS.md", "# Fixture\n");
  write("docs/reference/plugin-api/managed.md", "当前 managed 插件精确声明 Plugin API `2.1`\nFrontend API 独立维持 `1.6`\n");
  write("docs/reference/plugin-api/frontend.md", "## 前端插件运行时（Frontend API 1.6）\nFrontend API `1.6` 精确版本；只有 `1.6` 被接受\n");
  write("docs/testing/policy.md", "- Frontend API 1.6 的宿主外部契约\n");
  const run = () => spawnSync(process.execPath, [path.join(source, "tools/check-doc-links.mjs"), "--root", root], { encoding: "utf8" });
  write("docs/map.json", JSON.stringify({ schemaVersion: 1, topics: [{ id: "guide", path: "../CONTRIBUTING.md" }] }));
  assert.equal(run().status, 0);
  write("docs/map.json", JSON.stringify({ schemaVersion: 1, topics: [{ id: "guide", path: "../../external.md" }] }));
  const result = run(); assert.equal(result.status, 1); assert.match(result.stderr, /GOV-DOC-02/);
}));

test("T06-T07/T09/T11: new path roles distinguish UI, fixtures, runtime and unknown JSON", () => fixture(root => {
  const result = file => pathRule(root, { status: "A", path: file });
  assert.equal(result("frontend/src/ui/composites/NxpExample.vue").status, "PASS");
  assert.equal(result("tests/fixtures/task-protocol/input.json").status, "PASS");
  assert.equal(result("unknown.json").status, "REVIEW");
  assert.equal(result("config/new.json").status, "FAIL");
  assert.equal(pathRule(root, { status: "R", old: "docs/old.md", path: "logs/old.md" }).status, "FAIL");
}));

test("T11-T13: real staged rename, untracked paths and broad same-Owner change use existing Git selector", () => fixture((root, write) => {
  const git = (...args) => execFileSync("git", ["-C", root, ...args], { encoding: "utf8", windowsHide: true }).trim();
  git("init", "--quiet"); git("add", ".");
  git("-c", "user.name=Governance Fixture", "-c", "user.email=fixture@invalid", "commit", "--quiet", "-m", "fixture baseline");
  const base = git("rev-parse", "HEAD");
  fs.mkdirSync(path.join(root, "logs")); git("mv", "docs/testing/policy.md", "logs/policy.md");
  write("docs/testing/policy.md", fs.readFileSync(path.join(source, "docs/testing/policy.md")));
  write("new.json", "{}");
  write("src/Modules/Surprise/Surprise.cs", "namespace Fixture; public class Surprise {}\n");
  const evidence = collectChanges(root, { base, includeWorkingTree: true });
  assert.ok(evidence.changes.some(change => change.status === "R" && change.path === "logs/policy.md"));
  assert.ok(evidence.changes.some(change => change.untracked && change.path === "new.json"));
  assert.equal(pathRule(root, { status: "A", path: "src/Modules/Surprise/Surprise.cs" }, base).status, "REVIEW");
  const files = Array.from({ length: 150 }, (_, index) => ({ status: "M", path: `tools/helper-${index}.py` }));
  const report = checkChanges(root, { ...evidence, changes: files }, { owners: ["Tools"] });
  assert.equal(report.status, "PASS");
  const wider = checkChanges(root, { ...evidence, changes: [...files, { status: "M", path: "docs/extra.md" }] }, { owners: ["Tools"] });
  assert.equal(wider.status, "REVIEW"); assert.equal(wider.rules.find(rule => rule.rule === "GOV-SCOPE-01").file, "docs/extra.md");
}));

test("T14: legal semantic edge growth is REVIEW, same graph PASS, existing violation FAIL and missing graph NOT_CHECKED", () => {
  const baseline = { schemaVersion: 1, modes: ["production"], sourceFingerprints: { production: "a".repeat(64) }, status: "PASS", violations: [], edges: [] };
  const current = { ...baseline, edges: [{ Mode: "production", From: "Modules.A", To: "Modules.B" }] };
  assert.equal(architectureDelta(baseline, baseline).status, "PASS");
  assert.equal(architectureDelta(current, baseline).status, "REVIEW");
  assert.equal(architectureDelta({ ...current, status: "FAIL", violations: [{ RuleId: "A01" }] }, baseline).status, "FAIL");
  assert.equal(architectureDelta(current).status, "NOT_CHECKED");
});

test("T17-T18: preflight reads real UI imports/navigation with explicit missing partner", () => fixture(root => {
  const context = contextFor(root, { paths: ["frontend/src/ui/composites/New.vue"], keywords: ["button"] });
  assert.ok(context.publicElements.find(element => element.element === "nxp-button" && element.layer === "primitive"));
  assert.ok(context.testDomains.includes("ui")); assert.equal(context.partner.status, "NOT_CHECKED");
}));

test("T19-T20: version scope requires review, retained exemptions pass and added exemptions review", () => fixture((root, write) => {
  const evidence = { baseSha: "0".repeat(40), headSha: "0".repeat(40), changes: [{ status: "M", path: "desktop/package.json" }] };
  assert.equal(checkChanges(root, evidence).rules.find(rule => rule.rule === "GOV-VER-01").status, "REVIEW");
  assert.equal(checkChanges(root, evidence, { governanceOnly: true }).status, "FAIL");
  const git = (...args) => execFileSync("git", ["-C", root, ...args], { encoding: "utf8", windowsHide: true }).trim();
  write("frontend/src/app/example.ts", "// @ts-nocheck\nexport {};\n");
  git("init", "--quiet"); git("add", "."); git("-c", "user.name=Fixture", "-c", "user.email=fixture@invalid", "commit", "--quiet", "-m", "fixture baseline");
  evidence.baseSha = git("rev-parse", "HEAD"); evidence.headSha = evidence.baseSha;
  evidence.changes = [{ status: "M", path: "frontend/src/app/example.ts" }];
  assert.ok(!checkChanges(root, evidence, { workingTree: true }).rules.some(rule => rule.rule === "GOV-TYPE-01"));
  write("frontend/src/app/example.ts", "// @ts-nocheck\n// @ts-nocheck\nexport {};\n");
  assert.equal(checkChanges(root, evidence, { workingTree: true }).rules.find(rule => rule.rule === "GOV-TYPE-01").status, "REVIEW");
}));
