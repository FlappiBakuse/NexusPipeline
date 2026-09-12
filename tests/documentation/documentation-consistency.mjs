import assert from "node:assert/strict";
import { spawnSync } from "node:child_process";
import fs from "node:fs";
import path from "node:path";
import test from "node:test";
import { fileURLToPath } from "node:url";
import { CI_DOMAINS, CI_SHARED_PATHS } from "../../tools/ci-domains.mjs";
import { evaluateDomains, globToRegExp } from "../../tools/ci-changes.mjs";

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
  "docs/CONTROL_PLANE.md",
  "docs/DEVELOPMENT.md",
  "docs/TESTING.md",
  "docs/STATUS.md",
  "docs/PLUGIN_API.md",
  ".github/PULL_REQUEST_TEMPLATE.md",
];

const DEPRECATED_REFERENCES = [
  /tests[\\/]legacy[\\/]/,
  /edit-hidden/,
  /store-rebind/,
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

function currentProjectVersion() {
  const project = read("src/NexusPipeline.csproj");
  const match = project.match(/<Version>(\d+\.\d+\.\d+)<\/Version>/u);
  assert.ok(match, "NexusPipeline.csproj 缺少可解析的 Version");
  return match[1];
}

function escapeRegExp(value) {
  return value.replace(/[.*+?^${}()|[\]\\]/gu, "\\$&");
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
    "docs/CONTROL_PLANE.md",
    "docs/DEVELOPMENT.md",
    "docs/TESTING.md",
    "docs/STATUS.md",
    "CONTRIBUTING.md",
    "SECURITY.md",
    "CHANGELOG.md",
    "docs/PLUGIN_API.md",
  ];
  const missing = required.filter((relativePath) => !fs.existsSync(path.join(ROOT, relativePath)));
  assert.deepEqual(missing, [], `Missing README navigation targets: ${missing.join(", ")}`);
});

test("current persistence and plugin-profile contract stays documented", () => {
  const project = read("src/NexusPipeline.csproj");
  const version = currentProjectVersion();
  const status = read("docs/STATUS.md");
  const design = read("docs/DESIGN.md");
  const pluginApi = read("docs/PLUGIN_API.md");
  const development = read("docs/DEVELOPMENT.md");

  assert.match(project, new RegExp(`<Version>${escapeRegExp(version)}<\\/Version>`, "u"));
  assert.match(read("CHANGELOG.md"), new RegExp(`^## v${escapeRegExp(version)}(?:（|\\s)`, "mu"));
  assert.match(status, /## 当前未完成事项/u);
  assert.doesNotMatch(status, /^##\s+v\d+\.\d+\.\d+/mu);
  assert.match(design, /config\/judge-scripts\/<scriptId>\.js\|py/u);
  assert.match(design, /PluginType \+ RootPath/u);
  assert.match(pluginApi, /当前 profile 解析成功后将 `judgeScript`/u);
  assert.match(development, /config\/judge-scripts\//u);

});

test("dual-mode production contracts stay on data files and behavior", () => {
  // 权限门禁以 manifest 数据文件为准（产品 requireAdministrator，Test Host asInvoker）。
  const productionManifest = read("src/app.manifest");
  const testManifest = read("src/app.test.manifest");
  const project = read("src/NexusPipeline.csproj");
  assert.match(productionManifest, /requestedExecutionLevel level="requireAdministrator"/u);
  assert.match(testManifest, /requestedExecutionLevel level="asInvoker"/u);
  assert.match(project, /Condition="'\$\(NexusTestHost\)' == 'true'"/u);
  assert.match(project, /ApplicationManifest>app\.test\.manifest/u);

  // 文档不再描述已移除的 Broker/TestLauncher 架构。
  const ci = read(".github/workflows/ci.yml");
  const docs = [
    ["AGENTS.md", read("AGENTS.md")],
    ["docs/TESTING.md", read("docs/TESTING.md")],
    ["docs/DEVELOPMENT.md", read("docs/DEVELOPMENT.md")],
    ["docs/DESIGN.md", read("docs/DESIGN.md")],
  ];
  for (const [relativeFile, text] of docs) {
    assert.doesNotMatch(
      text,
      /AdminTestBroker|admin-broker|Elevated Test Broker|PowerShell Direct|Hyper-V|Windows Sandbox Broker/u,
      `${relativeFile} still describes removed Broker architecture`,
    );
  }
  assert.doesNotMatch(ci, /TestLauncher|launcher-probe|New-LocalUser|NEXUS_CI_TEST_USER|NEXUS_CI_TEST_PASSWORD|CreateRestrictedToken|linked-token|restricted-token|AdminTestBroker|admin-broker|NEXUS_TEST_HOST/u);
  assert.equal(fs.existsSync(path.join(ROOT, "tests/support/NexusPipeline.TestLauncher")), false);
  assert.equal(fs.existsSync(path.join(ROOT, "tests/support/launcher-probe.mjs")), false);
  assert.equal(fs.existsSync(path.join(ROOT, "tests/support/admin-broker")), false);

  // 行为级防线：省略模式必须以 exit code 2 拒绝执行（真正的权限契约走 admin 门禁）。
  assert.equal(
    spawnSync(process.execPath, [path.join(ROOT, "tests", "run.mjs"), "default"], { encoding: "utf8" }).status,
    2,
    "bare default must require an explicit codex/admin mode",
  );
});

test("CI impact domains match the System Smoke groups and workflow gates", () => {
  const domainKeys = CI_DOMAINS.map(domain => domain.key);
  assert.deepEqual(
    new Set(domainKeys).size,
    domainKeys.length,
    `影响域 key 重复：${domainKeys.join(", ")}`,
  );
  for (const domain of CI_DOMAINS) {
    assert.ok(domain.paths.length > 0, `${domain.key} 缺少触发路径`);
    for (const pattern of domain.paths) {
      assert.doesNotMatch(pattern, /\\|[?[\]{}]/u, `${domain.key} 触发路径格式无效：${pattern}`);
      assert.doesNotThrow(() => globToRegExp(pattern), `${domain.key} 触发路径无法编译：${pattern}`);
    }
  }
  for (const pattern of CI_SHARED_PATHS) {
    assert.doesNotThrow(() => globToRegExp(pattern), `共享路径无法编译：${pattern}`);
  }

  // 影响域判定对示例改动给出预期结果。
  const sample = evaluateDomains([
    "docs/TESTING.md",
    "frontend/src/plugin-bridge/contract.test.ts",
    "src/NexusPipeline.Plugin.Abstractions/PluginApi.cs",
  ]).domains;
  assert.deepEqual(
    Object.fromEntries(Object.entries(sample).map(([key, value]) => [key, value.affected])),
    { frontend: true, host: true, docs: true, plugin: true, ui: true, system: true },
    "示例改动的影响域判定与预期不一致",
  );
  const docsOnly = evaluateDomains(["docs/STATUS.md"]).domains;
  assert.equal(docsOnly.docs.affected, true);
  assert.equal(docsOnly.frontend.affected, false);
  assert.equal(docsOnly.system.affected, false);
  const unknownOnly = evaluateDomains(["SECURITY.md"]);
  assert.equal(unknownOnly.failOpen, true, "未命中影响域的改动需要按全量门禁处理");
  const sharedOnly = evaluateDomains([".github/workflows/ci.yml"]);
  assert.equal(sharedOnly.failOpen, true, "共享路径改动需要按全量门禁处理");

  // run.mjs 的 System Smoke 影响域分组与 CI 作业引用的入口必须一致且指向真实 suite 文件。
  const workflow = read(".github/workflows/ci.yml");
  const outputs = [...workflow.matchAll(/^ {6}([a-z]+): \$\{\{ steps\.domains\.outputs\.\1 \}\}$/gmu)]
    .map(match => match[1])
    .sort();
  assert.deepEqual(outputs, [...domainKeys].sort(), "ci.yml 影响域输出与 tools/ci-domains.mjs 不一致");
  for (const key of domainKeys) {
    assert.match(
      workflow,
      new RegExp(`needs\\.changes\\.outputs\\.${key} == 'true'`, "u"),
      `ci.yml 没有作业按影响域 ${key} 触发`,
    );
  }

  const dryRun = spawnSync(process.execPath, [path.join(ROOT, "tests", "run.mjs"), "admin", "system", "--dry"], {
    cwd: ROOT,
    encoding: "utf8",
  });
  assert.equal(dryRun.status, 0, `admin system --dry 退出码异常：${dryRun.stderr}`);
  const listedSuites = [...dryRun.stderr.matchAll(/\[System Smoke\] 影响域 (\S+) \| (\S+) \| (.+)$/gmu)]
    .map(match => ({ group: match[1], runtimeName: match[2], file: match[3].trim() }));
  assert.ok(listedSuites.length > 0, "admin system --dry 未列出任何 suite");
  const groupNames = [...new Set(listedSuites.map(suite => suite.group))];
  const systemDir = path.join(ROOT, "tests", "system");
  for (const suite of listedSuites) {
    assert.equal(path.dirname(suite.file), systemDir, `suite 文件不在 tests/system：${suite.file}`);
    assert.ok(fs.existsSync(suite.file), `suite 文件不存在：${suite.file}`);
  }
  const workflowGroups = [...new Set(
    [...workflow.matchAll(/node tests\\run\.mjs admin system ([\w-]+)/gu)].map(match => match[1]),
  )];
  assert.deepEqual(workflowGroups.sort(), groupNames.slice().sort(), "ci.yml 的 System Smoke 分组与 run.mjs 声明的分组不一致");
  for (const group of workflowGroups) {
    const groupArgs = spawnSync(
      process.execPath,
      [path.join(ROOT, "tests", "run.mjs"), "admin", "system", group, "--dry"],
      { cwd: ROOT, encoding: "utf8" },
    );
    assert.equal(groupArgs.status, 0, `admin system ${group} --dry 退出码异常：${groupArgs.stderr}`);
  }

  const unknownGroup = spawnSync(
    process.execPath,
    [path.join(ROOT, "tests", "run.mjs"), "admin", "system", "not-a-group"],
    { cwd: ROOT, encoding: "utf8" },
  );
  assert.equal(unknownGroup.status, 2, "未知 System Smoke 分组必须以 exit code 2 拒绝");
  assert.match(unknownGroup.stderr, /未知 System Smoke 分组/u);
  assert.match(unknownGroup.stderr, new RegExp(`可用分组：${groupNames.join(" \\| ")}`, "u"));
});
