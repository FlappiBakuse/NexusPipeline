import { collectAnchors, findLocalLinks, parseLinkTarget } from "../../tools/markdown.mjs";
import assert from "node:assert/strict";
import { spawnSync } from "node:child_process";
import fs from "node:fs";
import os from "node:os";
import path from "node:path";
import test from "node:test";
import { fileURLToPath } from "node:url";
import { CI_DOMAINS, CI_SHARED_PATHS } from "../../tools/ci-domains.mjs";
import { evaluateDomains, globToRegExp } from "../../tools/ci-changes.mjs";
import { validateCrossRepositoryLinks } from "../../tools/docs-index.mjs";

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

function probeEnvironment() {
  const env = { ...process.env };
  delete env.NEXUS_CI_MANIFEST;
  return env;
}

function currentProjectVersion() {
  const project = read("src/NexusPipeline.csproj");
  const match = project.match(/<Version>((?:0|[1-9]\d*)\.(?:0|[1-9]\d*)\.(?:0|[1-9]\d*)(?:-(?:beta|rc)\.(?:0|[1-9]\d*))?)<\/Version>/u);
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
  return target.startsWith("/")
    || /^[a-z][a-z0-9+.-]*:/i.test(target)
    || target.startsWith("//");
}

function extractVersionHeadings(text) {
  const versions = [];
  const pattern = /^##\s+\[?(v(?:0|[1-9]\d*)\.(?:0|[1-9]\d*)\.(?:0|[1-9]\d*)(?:-(?:beta|rc)\.(?:0|[1-9]\d*))?)\]?/gimu;
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
        target = parseLinkTarget(link.rawTarget);
      } catch {
        failures.push(`${relativeFile}:${lineNumber(text, link.index)} invalid URI ${link.rawTarget}`);
        continue;
      }
      if ((!target.path && !target.fragment) || isExternalTarget(target.path)) {
        continue;
      }
      const resolved = path.resolve(path.dirname(absoluteFile), target.path || path.basename(absoluteFile));
      const relativeResolved = path.relative(ROOT, resolved);
      if (relativeResolved.startsWith("..") || path.isAbsolute(relativeResolved) || !fs.existsSync(resolved)) {
        failures.push(`${relativeFile}:${lineNumber(text, link.index)} -> ${target.path || "(本文件)"}`);
      }
    }
  }
  assert.deepEqual(failures, [], `Broken local Markdown links:\n${failures.join("\n")}`);
});

test("Markdown fragments resolve headings and explicit anchors", () => {
  const failures = [];
  for (const absoluteFile of walkMarkdown(ROOT)) {
    const relativeFile = path.relative(ROOT, absoluteFile).replaceAll(path.sep, "/");
    const text = fs.readFileSync(absoluteFile, "utf8");
    for (const link of findLocalLinks(text)) {
      let target;
      try {
        target = parseLinkTarget(link.rawTarget);
      } catch {
        continue;
      }
      if (!target.fragment || isExternalTarget(target.path)) continue;
      const resolved = path.resolve(path.dirname(absoluteFile), target.path || path.basename(absoluteFile));
      if (!fs.existsSync(resolved) || !fs.statSync(resolved).isFile()) continue;
      if (!collectAnchors(fs.readFileSync(resolved, "utf8")).has(target.fragment)) {
        failures.push(`${relativeFile}:${lineNumber(text, link.index)} -> ${target.path || "(本文件)"}#${target.fragment}`);
      }
    }
  }
  assert.deepEqual(failures, [], `Broken Markdown fragments:\n${failures.join("\n")}`);
});

test("project GitHub links resolve against local checkouts and record fixed baselines", () => {
  const documents = walkMarkdown(ROOT).map(file => ({
    file: path.relative(ROOT, file).replaceAll(path.sep, "/"),
    text: fs.readFileSync(file, "utf8"),
  }));
  const result = validateCrossRepositoryLinks(documents, {
    root: ROOT,
    workspaceRoot: path.resolve(ROOT, ".."),
  });
  assert.deepEqual(
    result.issues,
    [],
    "Cross-repository documentation failures:\n" + result.issues.join("\n"),
  );
  assert.ok(result.checked.length > 0, "未发现需要交叉校验的项目文档链接");
  assert.ok(
    Object.values(result.baselines).every(sha => /^[0-9a-f]{40}$/u.test(sha)),
    "跨仓库校验必须记录固定 commit SHA",
  );
  console.log("[文档] 跨仓库固定基线：" + JSON.stringify(result.baselines));
});

test("missing cross-repository checkout is an explicit incomplete result", () => {
  const fixtureRoot = fs.mkdtempSync(path.join(os.tmpdir(), "nxp-docs-cross-repo-missing-"));
  try {
    const result = validateCrossRepositoryLinks(
      [{
        file: "fixture.md",
        text: "[插件指南](https://github.com/FlappiBakuse/NexusPipeline-Plugins/blob/main/docs/FRONTEND_PLUGIN.md)",
      }],
      {
        root: fixtureRoot,
        workspaceRoot: fixtureRoot,
      },
    );
    assert.equal(result.ok, false);
    assert.match(result.issues[0], /无法完成跨仓库检查/u);
  } finally {
    fs.rmSync(fixtureRoot, { recursive: true, force: true });
  }
});

test("Markdown link fixtures handle Chinese headings, duplicate slugs, explicit anchors, references and fenced code", () => {
  const fixture = [
    "# 中文 标题",
    "## 重复标题",
    "## 重复标题",
    '<a id="稳定-anchor"></a>',
    "[正文](#中文-标题)",
    "[第二个][dup] [显式](#稳定-anchor)",
    "",
    "[dup]: #重复标题-1",
    "```md",
    "[假链接](#不存在)",
    "```",
  ].join("\n");
  const links = findLocalLinks(fixture);
  assert.deepEqual(links.map(link => decodeURIComponent(link.rawTarget)), ["#中文-标题", "#重复标题-1", "#稳定-anchor"]);
  const anchors = collectAnchors(fixture);
  assert.ok(anchors.has("中文-标题"));
  assert.ok(anchors.has("重复标题-1"));
  assert.ok(anchors.has("稳定-anchor"));
  assert.equal(anchors.has("不存在"), false);
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

  assert.match(project, new RegExp(`<Version>${escapeRegExp(version)}<\\/Version>`, "u"));
  assert.match(read("CHANGELOG.md"), new RegExp(`^## v${escapeRegExp(version)}(?:（|\\s)`, "mu"));
  assert.match(status, /## 当前未完成事项/u);
  assert.doesNotMatch(status, /^##\s+v\d+\.\d+\.\d+/mu);
  const migration = JSON.parse(read("docs/migration-map.json"));
  assert.ok(migration.length > 0);
  for (const item of migration) {
    assert.ok(collectAnchors(read(item.target)).has(item.targetAnchor), `${item.source}#${item.anchor} -> ${item.target}`);
  }

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
    spawnSync(process.execPath, [path.join(ROOT, "tests", "run.mjs"), "default"], {
      encoding: "utf8",
      env: probeEnvironment(),
    }).status,
    2,
    "bare default must require an explicit codex/admin mode",
  );
});

test("CI impact domains match the System Smoke groups and workflow gates", () => {
  const domainKeys = new Set(CI_DOMAINS.map(domain => domain.key));
  assert.deepEqual(
    domainKeys.size,
    CI_DOMAINS.length,
    `影响域 key 重复：${[...domainKeys].join(", ")}`,
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

  // 影响域判定对示例改动给出预期结果：插件 SDK 只影响宿主与插件契约，不额外启动 System 作业。
  const sample = evaluateDomains([
    "docs/TESTING.md",
    "frontend/src/plugin-bridge/contract.test.ts",
    "src/NexusPipeline.Plugin.Abstractions/PluginApi.cs",
  ]).domains;
  assert.deepEqual(
    Object.fromEntries(Object.entries(sample).map(([key, value]) => [key, value.affected])),
    {
      frontend: true,
      host: true,
      docs: true,
      plugin: true,
      ui: true,
      system_runtime: false,
      system_execution: false,
      system_emulator: false,
      system_update: false,
    },
    "示例改动的影响域判定与预期不一致",
  );
  // 逐域映射由 tests/tools/ci-domains.test.mjs 覆盖，这里只核对文档改动不牵连任何 System 作业。
  const docsOnly = evaluateDomains(["docs/STATUS.md"]).domains;
  assert.equal(docsOnly.docs.affected, true);
  assert.equal(docsOnly.frontend.affected, false);
  assert.equal(docsOnly.system_runtime.affected, false);
  assert.equal(docsOnly.system_execution.affected, false);
  assert.equal(docsOnly.system_emulator.affected, false);
  assert.equal(docsOnly.system_update.affected, false);
  const unknownOnly = evaluateDomains(["SECURITY.md"]);
  assert.equal(unknownOnly.failOpen, true, "未命中影响域的改动需要按全量门禁处理");
  const sharedOnly = evaluateDomains([".github/workflows/ci.yml"]);
  assert.equal(sharedOnly.failOpen, true, "共享路径改动需要按全量门禁处理");

  // run.mjs 的 System Smoke 影响域分组与 CI 作业引用的入口必须一致且指向真实 suite 文件。
  const workflow = read(".github/workflows/ci.yml");
  const outputs = [...workflow.matchAll(/^ {6}([a-z_]+): \$\{\{ steps\.domains\.outputs\.\1 \}\}$/gmu)]
    .map(match => match[1])
    .filter(key => domainKeys.has(key))
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
    env: probeEnvironment(),
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
    [...workflow.matchAll(/node tests\\run\.mjs admin system([^\r\n]*)/gu)].flatMap(match => {
      const args = match[1];
      const namedGroups = [...args.matchAll(/--group\s+([\w-]+)/gu)].map(group => group[1]);
      if (namedGroups.length > 0) return namedGroups;
      const directGroup = args.match(/^\s+([\w-]+)/u);
      return directGroup ? [directGroup[1]] : [];
    }),
  )];
  assert.deepEqual(workflowGroups.sort(), groupNames.slice().sort(), "ci.yml 的 System Smoke 分组与 run.mjs 声明的分组不一致");
  for (const group of workflowGroups) {
    const groupArgs = spawnSync(
      process.execPath,
      [path.join(ROOT, "tests", "run.mjs"), "admin", "system", group, "--dry"],
      { cwd: ROOT, encoding: "utf8", env: probeEnvironment() },
    );
    assert.equal(groupArgs.status, 0, `admin system ${group} --dry 退出码异常：${groupArgs.stderr}`);
  }

  const unknownGroup = spawnSync(
    process.execPath,
    [path.join(ROOT, "tests", "run.mjs"), "admin", "system", "not-a-group"],
    { cwd: ROOT, encoding: "utf8", env: probeEnvironment() },
  );
  assert.equal(unknownGroup.status, 2, "未知 System Smoke 分组必须以 exit code 2 拒绝");
  assert.match(unknownGroup.stderr, /未知 System Smoke 分组/u);
  assert.match(unknownGroup.stderr, new RegExp(`可用分组：${groupNames.join(" \\| ")}`, "u"));
});
