import assert from "node:assert/strict";
import fs from "node:fs";
import path from "node:path";
import test from "node:test";
import { fileURLToPath } from "node:url";

const ROOT = path.resolve(path.dirname(fileURLToPath(import.meta.url)), "../..");
const SKIP_DIRECTORIES = new Set([
  ".git",
  "node_modules",
  "bin",
  "obj",
  "release",
  "runtime",
  "test-results",
  "flake-monitor-logs",
  "browsers",
]);

const EVERGREEN_DOCUMENTS = [
  "AGENTS.md",
  "README.md",
  "CONTRIBUTING.md",
  "SECURITY.md",
  "docs/DESIGN.md",
  "docs/ARCHITECTURE.md",
  "docs/DEVELOPMENT.md",
  "docs/TESTING.md",
  "docs/RELEASING.md",
  "docs/ROADMAP.md",
  "docs/KNOWN_ISSUES.md",
  "docs/PLUGIN_API.md",
  ".github/PULL_REQUEST_TEMPLATE.md",
];

const DEPRECATED_REFERENCES = [
  /tests[\\/]stress[\\/]chaos-queue\.mjs/,
  /NexusPipeline 后续开发报告\.md/,
];

function walkMarkdown(directory) {
  const files = [];
  for (const entry of fs.readdirSync(directory, { withFileTypes: true })) {
    if (entry.isDirectory() && SKIP_DIRECTORIES.has(entry.name)) {
      continue;
    }
    const absolutePath = path.join(directory, entry.name);
    if (entry.isDirectory()) {
      files.push(...walkMarkdown(absolutePath));
    } else if (entry.isFile() && entry.name.toLowerCase().endsWith(".md")) {
      files.push(absolutePath);
    }
  }
  return files;
}

function read(relativePath) {
  return fs.readFileSync(path.join(ROOT, relativePath), "utf8");
}

function lineNumber(text, index) {
  return text.slice(0, index).split(/\r?\n/).length;
}

function isExternalTarget(target) {
  return target.startsWith("#")
    || target.startsWith("/")
    || /^[a-z][a-z0-9+.-]*:/i.test(target)
    || target.startsWith("//");
}

function normalizeTarget(rawTarget) {
  let target = rawTarget.trim();
  if (target.startsWith("<") && target.endsWith(">")) {
    target = target.slice(1, -1);
  } else {
    target = target.split(/\s+/u, 1)[0];
  }
  const fragmentIndex = target.indexOf("#");
  if (fragmentIndex >= 0) {
    target = target.slice(0, fragmentIndex);
  }
  const queryIndex = target.indexOf("?");
  if (queryIndex >= 0) {
    target = target.slice(0, queryIndex);
  }
  return decodeURIComponent(target);
}

function findLocalLinks(text) {
  const links = [];
  const withoutFencedCode = text.replace(/```[\s\S]*?```/gu, (block) => "\n".repeat(block.split(/\r?\n/).length - 1));
  const inlinePattern = /\[[^\]\r\n]+\]\(([^)\r\n]+)\)/gu;
  for (const match of withoutFencedCode.matchAll(inlinePattern)) {
    links.push({ rawTarget: match[1], index: match.index ?? 0 });
  }
  const referencePattern = /^\s*\[[^\]\r\n]+\]:\s*(\S+)(?:\s+.*)?$/gmu;
  for (const match of withoutFencedCode.matchAll(referencePattern)) {
    links.push({ rawTarget: match[1], index: match.index ?? 0 });
  }
  return links;
}

function extractVersionHeadings(text) {
  const versions = [];
  const pattern = /^##\s+\[?(v\d+\.\d+\.\d+)\]?/gimu;
  for (const match of text.matchAll(pattern)) {
    versions.push({ version: match[1].toLowerCase(), index: match.index ?? 0 });
  }
  return versions;
}

test("Markdown local links resolve to files or directories", () => {
  const failures = [];
  for (const absoluteFile of walkMarkdown(ROOT)) {
    const relativeFile = path.relative(ROOT, absoluteFile).replaceAll(path.sep, "/");
    const text = fs.readFileSync(absoluteFile, "utf8");
    for (const link of findLocalLinks(text)) {
      let target;
      try {
        target = normalizeTarget(link.rawTarget);
      } catch {
        failures.push(`${relativeFile}:${lineNumber(text, link.index)} invalid URI ${link.rawTarget}`);
        continue;
      }
      if (!target || isExternalTarget(target)) {
        continue;
      }
      const resolved = path.resolve(path.dirname(absoluteFile), target);
      const relativeResolved = path.relative(ROOT, resolved);
      if (relativeResolved.startsWith("..") || path.isAbsolute(relativeResolved) || !fs.existsSync(resolved)) {
        failures.push(`${relativeFile}:${lineNumber(text, link.index)} -> ${target}`);
      }
    }
  }
  assert.deepEqual(failures, [], `Broken local Markdown links:\n${failures.join("\n")}`);
});

test("CHANGELOG has one heading per release version", () => {
  const headings = extractVersionHeadings(read("CHANGELOG.md"));
  const seen = new Map();
  const duplicates = [];
  for (const heading of headings) {
    if (seen.has(heading.version)) {
      duplicates.push(`${heading.version} at lines ${seen.get(heading.version)} and ${lineNumber(read("CHANGELOG.md"), heading.index)}`);
    } else {
      seen.set(heading.version, lineNumber(read("CHANGELOG.md"), heading.index));
    }
  }
  assert.deepEqual(duplicates, [], `Duplicate CHANGELOG headings:\n${duplicates.join("\n")}`);
});

test("ROADMAP does not repeat published version headings", () => {
  const changelogVersions = new Set(extractVersionHeadings(read("CHANGELOG.md")).map(({ version }) => version));
  const roadmapVersions = extractVersionHeadings(read("docs/ROADMAP.md"));
  const overlaps = roadmapVersions
    .filter(({ version }) => changelogVersions.has(version))
    .map(({ version, index }) => `${version} at line ${lineNumber(read("docs/ROADMAP.md"), index)}`);
  assert.deepEqual(overlaps, [], `ROADMAP contains published version headings:\n${overlaps.join("\n")}`);
});

test("evergreen documents contain no deprecated authority or path references", () => {
  const failures = [];
  for (const relativeFile of EVERGREEN_DOCUMENTS) {
    const text = read(relativeFile);
    for (const pattern of DEPRECATED_REFERENCES) {
      const match = pattern.exec(text);
      if (match) {
        failures.push(`${relativeFile}:${lineNumber(text, match.index)} contains ${match[0]}`);
      }
    }
  }
  assert.deepEqual(failures, [], `Deprecated references:\n${failures.join("\n")}`);
});

test("README documentation navigation points to existing files", () => {
  const required = [
    "docs/DESIGN.md",
    "docs/ARCHITECTURE.md",
    "docs/DEVELOPMENT.md",
    "docs/TESTING.md",
    "docs/RELEASING.md",
    "docs/ROADMAP.md",
    "docs/KNOWN_ISSUES.md",
    "CONTRIBUTING.md",
    "SECURITY.md",
    "CHANGELOG.md",
    "docs/PLUGIN_API.md",
  ];
  const missing = required.filter((relativePath) => !fs.existsSync(path.join(ROOT, relativePath)));
  assert.deepEqual(missing, [], `Missing README navigation targets: ${missing.join(", ")}`);
});
