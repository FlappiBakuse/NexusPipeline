import assert from "node:assert/strict";
import fs from "node:fs";
import path from "node:path";
import { fileURLToPath } from "node:url";
import test from "node:test";
import { collectAnchors, findLocalLinks, parseLinkTarget } from "../../tools/markdown.mjs";
import { validateCrossRepositoryLinks, validateMap, loadMap } from "../../tools/docs-index.mjs";
import { GOVERNANCE_DOMAINS, HOST_TEST_AREAS, SYSTEM_TEST_GROUPS, TEST_DOMAIN_REGISTRY } from "../registry.mjs";

const ROOT = path.resolve(path.dirname(fileURLToPath(import.meta.url)), "../..");
const SKIP_DIRECTORIES = new Set([".git", "node_modules", "bin", "obj", "release", "NexusPipeline-Plugins", "tests/.artifacts"]);
const EVERGREEN_DOCUMENTS = [
  "AGENTS.md",
  "README.md",
  "CONTRIBUTING.md",
  "SECURITY.md",
  "docs/DESIGN.md",
  "docs/CONTROL_PLANE.md",
  "docs/DEVELOPMENT.md",
  "docs/TESTING.md",
  "docs/STATUS.md",
  "docs/PLUGIN_API.md",
  ".github/PULL_REQUEST_TEMPLATE.md",
];
const RETIRED_REFERENCES = [
  /tools[\\/]ci-(?:changes|domains|summary|fingerprint)\.mjs/u,
  /tests[\\/]run\.mjs\s+(?:codex|admin)/u,
  /tests[\\/]run\.mjs[^\n]*--(?:affected|dry)(?:\s|`)/u,
  /NEXUS_CI_(?:MANIFEST|JOB_ID|MODE|DOMAIN|EXECUTION_PLAN|PLAN_DIGEST|RESULT)/u,
];

function walkMarkdown(directory) {
  const files = [];
  for (const entry of fs.readdirSync(directory, { withFileTypes: true })) {
    const relative = path.relative(ROOT, path.join(directory, entry.name)).replaceAll("\\", "/");
    if (entry.isDirectory() && (SKIP_DIRECTORIES.has(entry.name) || relative.startsWith("tests/.artifacts/"))) continue;
    const fullPath = path.join(directory, entry.name);
    if (entry.isDirectory()) files.push(...walkMarkdown(fullPath));
    else if (entry.isFile() && entry.name.toLowerCase().endsWith(".md")) files.push(fullPath);
  }
  return files;
}

function read(relativePath) {
  return fs.readFileSync(path.join(ROOT, relativePath), "utf8");
}

function lineNumber(text, index) {
  return text.slice(0, index).split(/\r?\n/u).length;
}

function isExternalTarget(target) {
  return target.startsWith("/") || /^[a-z][a-z0-9+.-]*:/i.test(target) || target.startsWith("//");
}

test("Markdown local links and fragments resolve", () => {
  const failures = [];
  for (const absoluteFile of walkMarkdown(ROOT)) {
    const relativeFile = path.relative(ROOT, absoluteFile).replaceAll("\\", "/");
    const text = fs.readFileSync(absoluteFile, "utf8");
    for (const link of findLocalLinks(text)) {
      let target;
      try {
        target = parseLinkTarget(link.rawTarget);
      } catch {
        failures.push(`${relativeFile}:${lineNumber(text, link.index)} invalid target ${link.rawTarget}`);
        continue;
      }
      if (isExternalTarget(target.path)) continue;
      const resolved = path.resolve(path.dirname(absoluteFile), target.path || path.basename(absoluteFile));
      if (!resolved.startsWith(ROOT) || !fs.existsSync(resolved)) {
        failures.push(`${relativeFile}:${lineNumber(text, link.index)} -> ${target.path || "(self)"}`);
        continue;
      }
      if (target.fragment && fs.statSync(resolved).isFile()
        && !collectAnchors(fs.readFileSync(resolved, "utf8")).has(target.fragment)) {
        failures.push(`${relativeFile}:${lineNumber(text, link.index)} -> ${target.path || "(self)"}#${target.fragment}`);
      }
    }
  }
  assert.deepEqual(failures, [], `Markdown link failures:\n${failures.join("\n")}`);
});

test("cross-repository documentation links resolve against the checked-out fixed source", () => {
  const documents = walkMarkdown(ROOT).map(file => ({
    file: path.relative(ROOT, file).replaceAll("\\", "/"),
    text: fs.readFileSync(file, "utf8"),
  }));
  const result = validateCrossRepositoryLinks(documents, {
    root: ROOT,
    workspaceRoot: path.resolve(ROOT, ".."),
    checkouts: { "NexusPipeline-Plugins": process.env.NEXUS_OFFICIAL_PLUGINS_ROOT?.trim() },
  });
  assert.deepEqual(result.issues, [], result.issues.join("\n"));
  assert.ok(result.checked.length > 0, "至少应校验一条跨仓库文档链接");
  assert.ok(Object.values(result.baselines).every(sha => /^[0-9a-f]{40}$/u.test(sha)));
});

test("evergreen documentation has no retired CI proof or permission-mode authority", () => {
  const failures = [];
  for (const relativePath of EVERGREEN_DOCUMENTS) {
    assert.ok(fs.existsSync(path.join(ROOT, relativePath)), `缺少 evergreen 文档：${relativePath}`);
    const text = read(relativePath);
    for (const pattern of RETIRED_REFERENCES) {
      if (pattern.test(text)) failures.push(`${relativePath} matches ${pattern}`);
    }
  }
  assert.deepEqual(failures, [], failures.join("\n"));
});

test("documentation map and local test registry are current", () => {
  assert.deepEqual(validateMap(ROOT, loadMap(ROOT)), { ok: true, errors: [] });
  assert.equal(TEST_DOMAIN_REGISTRY.schemaVersion, 1);
  for (const collection of [HOST_TEST_AREAS, SYSTEM_TEST_GROUPS, GOVERNANCE_DOMAINS]) {
    assert.ok(collection.length > 0);
    assert.equal(new Set(collection.map(item => item.key)).size, collection.length);
  }
  const runner = read("tests/run.mjs");
  assert.doesNotMatch(runner, /validateExecutionPlan|readCiPlan|recordCiCheck|upsertCiGroup|refreshGroupSelection/u);
  assert.doesNotMatch(runner, /NEXUS_CI_(?:MANIFEST|JOB_ID|MODE|DOMAIN|EXECUTION_PLAN|PLAN_DIGEST)/u);
  assert.match(runner, /release <\$\{RELEASE_GROUPS\.join\("\|"\)\}>/u);
});

test("architecture governance uses generated maps and current composition-root paths", () => {
  assert.equal(fs.existsSync(path.join(ROOT, "docs/backend-map.json")), false);
  assert.equal(fs.existsSync(path.join(ROOT, "docs/migration-map.json")), false);
  assert.match(read(".gitignore"), /^\.generated\/$/mu);
  assert.match(read("docs/architecture/README.md"), /\.generated\/architecture\/backend-map\.json/u);

  for (const relativePath of ["AGENTS.md", "docs/DESIGN.md", "docs/PLUGIN_API.md", "docs/architecture/overview.md"]) {
    const text = read(relativePath);
    assert.doesNotMatch(text, /migration-map\.json/u, `${relativePath} still depends on migration-map.json`);
    assert.doesNotMatch(text, /src[\\/]Host[\\/]Composition[\\/]RuntimeContext\.cs/u, `${relativePath} still points to RuntimeContext.cs`);
  }
});

test("production and Test Host manifest contracts remain distinct", () => {
  assert.match(read("src/app.manifest"), /requestedExecutionLevel level="requireAdministrator"/u);
  assert.match(read("src/app.test.manifest"), /requestedExecutionLevel level="asInvoker"/u);
  assert.match(read("src/NexusPipeline.csproj"), /ApplicationManifest>app\.test\.manifest/u);
});
