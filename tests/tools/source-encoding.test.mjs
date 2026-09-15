import assert from "node:assert/strict";
import { readFileSync, readdirSync } from "node:fs";
import { test } from "node:test";
import { dirname, extname, join, relative, resolve } from "node:path";
import { fileURLToPath } from "node:url";

const repoRoot = resolve(dirname(fileURLToPath(import.meta.url)), "../..");
const controlledExtensions = new Set([".cs", ".ts", ".vue", ".js", ".mjs", ".py", ".json", ".md"]);

const ignoredDirectories = new Set([
  ".git",
  "node_modules",
  "bin",
  "obj",
  "dist",
  "runtime",
  "test-results",
  "flake-monitor-logs",
]);

function controlledFiles(directory = repoRoot) {
  const files = [];
  for (const entry of readdirSync(directory, { withFileTypes: true })) {
    if (entry.isDirectory() && ignoredDirectories.has(entry.name)) {
      continue;
    }
    const absolutePath = join(directory, entry.name);
    if (entry.isDirectory()) {
      files.push(...controlledFiles(absolutePath));
    } else if (entry.isFile() && controlledExtensions.has(extname(entry.name))) {
      files.push(relative(repoRoot, absolutePath));
    }
  }
  return files;
}

function findReplacementCharacters(file) {
  const text = readFileSync(resolve(repoRoot, file), "utf8");
  const violations = [];
  let offset = text.indexOf("\ufffd");
  while (offset >= 0) {
    violations.push(`${file}:${text.slice(0, offset).split("\n").length}`);
    offset = text.indexOf("\ufffd", offset + 1);
  }
  return violations;
}

test("受控文本源码不得包含 Unicode replacement character", () => {
  const violations = controlledFiles().flatMap(findReplacementCharacters);
  assert.deepEqual(
    violations,
    [],
    `发现损坏的 UTF-8 replacement character（U+FFFD）：\n${violations.join("\n")}`,
  );
});
