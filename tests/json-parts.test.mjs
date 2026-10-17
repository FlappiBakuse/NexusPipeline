import test from "node:test";
import assert from "node:assert/strict";
import fs from "node:fs";
import path from "node:path";
import { execFileSync } from "node:child_process";
import { loadJsonInput, readJsonInput } from "../tools/json-parts.mjs";

const repository = path.resolve(import.meta.dirname, "..");
const encode = value => Buffer.from(JSON.stringify(value, null, 2) + "\n");
function fixture(run) {
  const artifact = process.env.NEXUS_TEST_ARTIFACT_ROOT;
  assert.ok(artifact && path.isAbsolute(artifact), "Explicit external fixture root required");
  const root = fs.mkdtempSync(path.join(artifact, "json-parts-"));
  const write = (file, value) => {
    const target = path.join(root, file);
    fs.mkdirSync(path.dirname(target), { recursive: true });
    fs.writeFileSync(target, Buffer.isBuffer(value) ? value : encode(value));
  };
  try { run(root, write); }
  finally { fs.rmSync(root, { recursive: true, force: true }); }
}

test("partitioned policy preserves groups and budgets while every part affects its identity", () => fixture((root, write) => {
  const group = { filter: "FullyQualifiedName~Fixture", caseIds: ["Fixture.Runs"], scenarioIds: ["rules"] };
  const inline = { schemaVersion: 1, invocationBudgetMs: null, cleanupReserveMs: 0, groups: { backend: group } };
  write("tests/policy.json", inline);
  const original = readJsonInput(root, "tests/policy.json");
  write("tests/policy/backend.json", { backend: group });
  write("tests/policy.json", { schemaVersion: 1, invocationBudgetMs: null, cleanupReserveMs: 0,
    partitionVersion: 1, parts: [{ path: "tests/policy/backend.json", section: "groups" }] });
  const split = readJsonInput(root, "tests/policy.json");
  assert.deepEqual(JSON.parse(split.bytes), inline);
  assert.equal(split.files.length, 2);
  fs.appendFileSync(path.join(root, "tests/policy/backend.json"), "\n");
  const changed = readJsonInput(root, "tests/policy.json");
  assert.deepEqual(JSON.parse(changed.bytes), inline);
  assert.notEqual(changed.digest, split.digest);
  assert.notEqual(split.digest, original.digest);
  const python = execFileSync(process.platform === "win32" ? "python" : "python3", ["-B", "-c",
    "import sys,json;sys.path.insert(0,sys.argv[1]);from json_parts import read_json_input;print(json.dumps(read_json_input(sys.argv[2],sys.argv[3])['digest']))",
    path.join(repository, "tests"), root, "tests/policy.json"], { encoding: "utf8", windowsHide: true });
  assert.equal(JSON.parse(python), changed.digest);
}));

test("topic partitions retain the declared complete order and reject omissions", () => {
  const parts = new Map([
    ["docs/map.json", encode({ schemaVersion: 1, partitionVersion: 1,
      parts: [{ path: "docs/map/architecture.json", section: "topics" }, { path: "docs/map/guide.json", section: "topics" }],
      order: { topics: ["guide", "architecture"] } })],
    ["docs/map/architecture.json", encode([{ id: "architecture", path: "architecture/README.md" }])],
    ["docs/map/guide.json", encode([{ id: "guide", path: "user/README.md" }])],
  ]);
  const read = file => { assert.ok(parts.has(file), `Missing ${file}`); return parts.get(file); };
  assert.deepEqual(loadJsonInput("docs/map.json", read).value.topics.map(topic => topic.id), ["guide", "architecture"]);
  parts.set("docs/map/guide.json", encode([]));
  assert.throws(() => loadJsonInput("docs/map.json", read), /Incomplete/);
});

test("missing duplicate escaping and unknown parts fail without fallback", () => fixture((root, write) => {
  const part = { path: "tests/policy/backend.json", section: "groups" };
  write(part.path, { backend: { caseIds: ["Fixture.Runs"] } });
  for (const parts of [[part, part], [{ ...part, path: "tests/policy/../outside.json" }],
    [{ ...part, path: "tests/policy/missing.json" }], [{ ...part, section: "unknown" }]]) {
    write("tests/policy.json", { partitionVersion: 1, parts });
    assert.throws(() => readJsonInput(root, "tests/policy.json"));
  }
  write("tests/policy/other.json", { backend: {} });
  write("tests/policy.json", { partitionVersion: 1, parts: [part, { ...part, path: "tests/policy/other.json" }] });
  assert.throws(() => readJsonInput(root, "tests/policy.json"), /Duplicate JSON group/);
}));

test("immutable Git tree reads every part from the selected tree", () => fixture((root, write) => {
  write("tests/policy.json", { schemaVersion: 1, partitionVersion: 1, parts: [{ path: "tests/policy/backend.json", section: "groups" }] });
  write("tests/policy/backend.json", { backend: { caseIds: ["Fixture.Original"] } });
  const git = (...args) => execFileSync("git", ["-C", root, ...args], { windowsHide: true });
  git("init", "--quiet"); git("add", ".");
  const tree = git("write-tree").toString().trim();
  const fixed = () => loadJsonInput("tests/policy.json", file => git("show", `${tree}:${file}`));
  const before = fixed();
  write("tests/policy/backend.json", { backend: { caseIds: ["Fixture.Changed"] } });
  assert.equal(fixed().digest, before.digest);
  assert.deepEqual(fixed().value.groups.backend.caseIds, ["Fixture.Original"]);
  assert.notEqual(readJsonInput(root, "tests/policy.json").digest, before.digest);
}));
