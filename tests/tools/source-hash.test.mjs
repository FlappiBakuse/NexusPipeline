import assert from "node:assert/strict";
import fs from "node:fs";
import os from "node:os";
import path from "node:path";
import test from "node:test";
import { computeSourceHash } from "../../tools/source-hash.mjs";

function fixture() {
  const root = fs.mkdtempSync(path.join(os.tmpdir(), "nexus-source-hash-"));
  const files = {
    "src/App.cs": "class App {}\n",
    "frontend/src/main.ts": "export const value = 1;\n",
    "Directory.Build.props": "<Project />\n",
    "global.json": "{}\n",
    "package.json": "{}\n",
    "package-lock.json": "{}\n",
    "build.cmd": "@echo off\n",
    "tests/run.mjs": "// build recipe\n",
    "tools/source-hash.mjs": "// hashing recipe\n",
  };
  for (const [relative, content] of Object.entries(files)) {
    const target = path.join(root, relative);
    fs.mkdirSync(path.dirname(target), { recursive: true });
    fs.writeFileSync(target, content, "utf8");
  }
  return root;
}

const OPTIONS = Object.freeze({ platform: "win32", arch: "x64", nodeVersion: "v-test", dotnetVersion: "8.0-test" });

test("test-host hash is deterministic and invalidates on source, recipe, and toolchain changes", () => {
  const root = fixture();
  try {
    const initial = computeSourceHash(root, "--test-host", OPTIONS);
    assert.match(initial, /^[A-F0-9]{64}$/u);
    assert.equal(computeSourceHash(root, "--test-host", OPTIONS), initial);

    fs.writeFileSync(path.join(root, "src/App.cs"), "class App { int V = 2; }\n", "utf8");
    const sourceChanged = computeSourceHash(root, "--test-host", OPTIONS);
    assert.notEqual(sourceChanged, initial);

    fs.writeFileSync(path.join(root, "tests/run.mjs"), "// changed build recipe\n", "utf8");
    const recipeChanged = computeSourceHash(root, "--test-host", OPTIONS);
    assert.notEqual(recipeChanged, sourceChanged);

    const toolchainChanged = computeSourceHash(root, "--test-host", { ...OPTIONS, dotnetVersion: "9.0-test" });
    assert.notEqual(toolchainChanged, recipeChanged);
  } finally {
    fs.rmSync(root, { recursive: true, force: true });
  }
});

test("test-host hash excludes generated dependency outputs", () => {
  const root = fixture();
  try {
    const initial = computeSourceHash(root, "--test-host", OPTIONS);
    const generated = path.join(root, "frontend", "node_modules", "package", "index.js");
    fs.mkdirSync(path.dirname(generated), { recursive: true });
    fs.writeFileSync(generated, "generated\n", "utf8");
    assert.equal(computeSourceHash(root, "--test-host", OPTIONS), initial);
  } finally {
    fs.rmSync(root, { recursive: true, force: true });
  }
});
