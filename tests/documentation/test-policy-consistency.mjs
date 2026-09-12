import assert from "node:assert/strict";
import fs from "node:fs";
import path from "node:path";
import { fileURLToPath } from "node:url";
import { test } from "node:test";

const projectRoot = path.resolve(path.dirname(fileURLToPath(import.meta.url)), "..", "..");
const persistentSourceRoots = [
  path.join(projectRoot, "frontend", "src"),
  path.join(projectRoot, "tests", "e2e", "tests"),
];

function walk(root) {
  const files = [];
  if (!fs.existsSync(root)) return files;
  for (const entry of fs.readdirSync(root, { withFileTypes: true })) {
    const target = path.join(root, entry.name);
    if (entry.isDirectory()) files.push(...walk(target));
    else files.push(target);
  }
  return files;
}

function lineNumber(text, index) {
  return text.slice(0, index).split("\n").length;
}

function findPolicyViolations() {
  const violations = [];
  const forbidden = [
    ["Playwright screenshot matcher", /\.toHaveScreenshot\s*\(/u],
    ["persistent snapshot matcher", /\.(?:toMatchSnapshot|toMatchInlineSnapshot)\s*\(/u],
    ["snapshot update switch", /NEXUS_UPDATE_SNAPSHOTS/u],
  ];

  for (const root of persistentSourceRoots) {
    for (const file of walk(root)) {
      if (!/\.(?:js|mjs|ts|vue)$/u.test(file)) continue;
      const text = fs.readFileSync(file, "utf8");
      for (const [name, pattern] of forbidden) {
        const match = pattern.exec(text);
        if (match) {
          violations.push(`${path.relative(projectRoot, file)}:${lineNumber(text, match.index)} ${name}`);
        }
      }
    }
  }

  for (const file of walk(path.join(projectRoot, "tests", "e2e", "tests"))) {
    const relative = path.relative(projectRoot, file);
    const name = path.basename(file);
    if (name.endsWith("-snapshots") || /(?:^|[-_.])visual(?:[-_.]|$)/iu.test(name)) {
      violations.push(`${relative} persistent visual-test asset or suite`);
    }
    if (/\.(?:snap|png)$/iu.test(file) && file.includes(`${path.sep}tests${path.sep}e2e${path.sep}tests${path.sep}`)) {
      violations.push(`${relative} persistent visual-test baseline`);
    }
  }
  return violations;
}

test("persistent tests follow the functional UI policy", () => {
  const violations = findPolicyViolations();
  assert.deepEqual(violations, [], [
    "持久化测试不得加入截图匹配器、视觉回归套件或快照基线。",
    "发现：",
    ...violations,
  ].join("\n"));
});
