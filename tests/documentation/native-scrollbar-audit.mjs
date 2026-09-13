import assert from "node:assert/strict";
import fs from "node:fs";
import path from "node:path";
import test from "node:test";
import { fileURLToPath } from "node:url";

const ROOT = path.resolve(path.dirname(fileURLToPath(import.meta.url)), "../..");
const FRONTEND = path.join(ROOT, "frontend", "src");
const SOURCE_EXTENSIONS = new Set([".css", ".js", ".mjs", ".ts", ".vue"]);
const REVIEWED_SCROLL_PRIMITIVES = new Set([
  "ui/primitives/NxpScrollArea.vue",
  "ui/primitives/NxpTextArea.vue",
]);

function walk(directory) {
  const files = [];
  for (const entry of fs.readdirSync(directory, { withFileTypes: true })) {
    if (entry.name === "node_modules" || entry.name === ".artifacts") continue;
    const absolute = path.join(directory, entry.name);
    if (entry.isDirectory()) files.push(...walk(absolute));
    else if (entry.isFile() && SOURCE_EXTENSIONS.has(path.extname(entry.name).toLowerCase())) files.push(absolute);
  }
  return files;
}

function relative(absolute) {
  return path.relative(FRONTEND, absolute).replaceAll(path.sep, "/");
}

test("frontend scroll containers use reviewed overlay primitives", () => {
  const violations = [];
  for (const file of walk(FRONTEND)) {
    const relativePath = relative(file);
    const source = fs.readFileSync(file, "utf8");
    if (REVIEWED_SCROLL_PRIMITIVES.has(relativePath)) continue;
    if (/\boverflow(?:-[xy])?\s*:\s*(?:auto|scroll)\b/iu.test(source)) {
      violations.push(`${relativePath}: native overflow container`);
    }
    if (/\bscrollbar-width\s*:/iu.test(source) || /::-[\w-]*scrollbar/iu.test(source)) {
      violations.push(`${relativePath}: direct scrollbar styling`);
    }
    if (/<textarea\b/iu.test(source)) {
      violations.push(`${relativePath}: textarea must use NxpTextArea`);
    }
  }
  assert.deepEqual(violations, [], violations.join("\n"));

  const scrollArea = fs.readFileSync(path.join(FRONTEND, "ui/primitives/NxpScrollArea.vue"), "utf8");
  const textArea = fs.readFileSync(path.join(FRONTEND, "ui/primitives/NxpTextArea.vue"), "utf8");
  for (const [name, source] of [["NxpScrollArea", scrollArea], ["NxpTextArea", textArea]]) {
    assert.match(source, /scrollbar-width:\s*none/iu, `${name} must hide native scrollbar chrome`);
    assert.match(source, /::-[\w-]*scrollbar/iu, `${name} must define the WebKit native-scrollbar fallback`);
    assert.match(source, /role=["']scrollbar["']/iu, `${name} must expose an accessible overlay thumb`);
  }
});
