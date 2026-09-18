import test from "node:test";
import assert from "node:assert/strict";
import fs from "node:fs";
import path from "node:path";
import { spawnSync } from "node:child_process";
import { fileURLToPath } from "node:url";
import { CI_DOMAINS, FRONTEND_TEST_GROUPS, HOST_TEST_AREAS, SYSTEM_SHARED_PATHS, SYSTEM_TEST_GROUPS, TEST_DOMAIN_REGISTRY, validateTestDomainRegistry } from "../../tools/ci-domains.mjs";
import { createExecutionPlan, evaluateDomains, parseNameStatusZ, selectTestGroups } from "../../tools/ci-changes.mjs";
import { expectedTestFiles } from "../../tools/test-selection.mjs";

test('测试分组包含传递调用方并对未知路径全量兜底', () => {
  assert.deepEqual(selectTestGroups('frontend', ['frontend/src/ui/primitives/NxpButton.vue']), ['ui', 'bridge', 'platform', 'features']);
  assert.deepEqual(selectTestGroups('frontend', ['frontend/src/features/history/HistoryPage.vue']), ['features']);
  assert.deepEqual(selectTestGroups('host', ['src/Services/Update/UpdateService.cs']), ['update', 'control']);
  assert.deepEqual(selectTestGroups('host', ['src/new-subsystem/New.cs']), HOST_TEST_AREAS.map(area => area.key));
  assert.ok(selectTestGroups('host', ['src/Services/Judgement/Judge.cs']).includes('scheduling'));
});

test('宿主测试容器名称与分组执行过滤器一致', () => {
  const directory = fileURLToPath(new URL('../NexusPipeline.Tests/', import.meta.url));
  for (const name of fs.readdirSync(directory).filter(name => name.endsWith('Tests.cs'))) {
    const source = fs.readFileSync(path.join(directory, name), 'utf8');
    const containers = [...source.matchAll(/^public (?:sealed |abstract |partial )*class ([A-Za-z0-9_]+)/gm)].map(match => match[1]);
    assert.deepEqual(containers, [path.basename(name, '.cs')], name);
  }
});

/**
 * CI 影响域映射用例：四个 System 影响域必须按真实路径收敛到各自的门禁。
 *
 * 用例只调用 tools/ci-changes.mjs 的纯函数，改动集合使用仓库中真实存在的相对路径，
 * 不执行 git，也不依赖 process.cwd()。
 */

const repoRoot = path.resolve(path.dirname(fileURLToPath(import.meta.url)), "..", "..");
const SYSTEM_KEYS = ["system_runtime", "system_execution", "system_emulator", "system_update"];

function evaluate(changedFiles) {
  const result = evaluateDomains(changedFiles);
  const flags = {};
  for (const [key, value] of Object.entries(result.domains)) flags[key] = value.affected;
  return { result, flags };
}

function assertFlags(changedFiles, expected, label = changedFiles.join(", ")) {
  const { flags, result } = evaluate(changedFiles);
  for (const [key, value] of Object.entries(expected)) {
    assert.equal(flags[key], value, `${label}：影响域 ${key} 应为 ${value}`);
  }
  return result;
}

function assertRepoFiles(relativePaths) {
  for (const relativePath of relativePaths) {
    assert.ok(fs.existsSync(path.join(repoRoot, relativePath)), `用例引用的仓库路径不存在：${relativePath}`);
  }
}

function flagsOf(changedFiles) {
  const { flags } = evaluate(changedFiles);
  return flags;
}

function systemFlags(changedFiles) {
  const flags = flagsOf(changedFiles);
  return SYSTEM_KEYS.filter(key => flags[key]);
}

function escapeRegExp(value) {
  return value.replace(/[.*+?^${}()|[\]\\]/gu, "\\$&");
}

test("四个 System 域取代旧的 system 域并各带独立 Gate", () => {
  const keys = CI_DOMAINS.map(domain => domain.key);
  assert.deepEqual(keys, ["frontend", "host", "docs", "plugin", "ui", ...SYSTEM_KEYS]);
  const gates = Object.fromEntries(CI_DOMAINS.map(domain => [domain.key, domain.gate]));
  assert.deepEqual(
    SYSTEM_KEYS.map(key => gates[key]),
    ["Gate F System Runtime/MCP", "Gate F System Execution", "Gate F System Emulator", "Gate F System Update"],
  );
});

test("System 域不再使用 src/** 全域触发", () => {
  for (const domain of CI_DOMAINS.filter(item => item.key.startsWith("system_"))) {
    for (const pattern of ["src/**", "tests/system/**", "tests/**"]) {
      assert.ok(!domain.paths.includes(pattern), `${domain.key} 不应包含全域触发规则 ${pattern}`);
    }
  }
  assert.ok(CI_DOMAINS.find(domain => domain.key === "host").paths.includes("src/**"), "host 域保留宿主全量门禁");
});

test("纯文档改动只触发 docs", () => {
  const files = ["docs/DESIGN.md", "CHANGELOG.md"];
  assertRepoFiles(files);
  const result = assertFlags(files, {
    docs: true,
    frontend: false,
    host: false,
    plugin: false,
    ui: false,
    system_runtime: false,
    system_execution: false,
    system_emulator: false,
    system_update: false,
  });
  assert.equal(result.failOpen, false);
});

test("Vue 组件改动触发 frontend 与 ui", () => {
  const files = ["frontend/src/features/settings/SettingsPage.vue"];
  assertRepoFiles(files);
  assertFlags(files, { frontend: true, ui: true, ...Object.fromEntries(SYSTEM_KEYS.map(key => [key, false])) });
});

test("MuMu 驱动改动只触发 system_emulator 的 System 门禁", () => {
  const files = ["src/Services/EmulatorDrivers.cs"];
  assertRepoFiles(files);
  assertFlags(files, { host: true, system_emulator: true, system_update: false, system_execution: false, system_runtime: false });
});

test("UpdateService 改动只触发 system_update 的 System 门禁", () => {
  const files = ["src/Services/Update/UpdateService.cs"];
  assertRepoFiles(files);
  assertFlags(files, { host: true, system_update: true, system_emulator: false, system_execution: false, system_runtime: false });
});

test("update-policy.json 改动触发 host 与 system_update 门禁", () => {
  const files = ["update-policy.json"];
  assertRepoFiles(files);
  assertFlags(files, {
    frontend: false,
    host: true,
    docs: false,
    plugin: false,
    ui: false,
    system_runtime: false,
    system_execution: false,
    system_emulator: false,
    system_update: true,
  });
});

test("ExecutionService 改动只触发 system_execution 的 System 门禁", () => {
  const files = ["src/Services/Execution/ExecutionCoordinator.cs"];
  assertRepoFiles(files);
  assertFlags(files, { host: true, system_execution: true, system_emulator: false, system_update: false, system_runtime: false });
});

test("MCP 改动只触发 system_runtime 的 System 门禁", () => {
  const files = ["src/Mcp/McpHost.cs"];
  assertRepoFiles(files);
  assertFlags(files, { host: true, system_runtime: true, system_update: false, system_execution: false, system_emulator: false });
});

test("Plugin API 改动触发 host 与 plugin", () => {
  const files = ["src/NexusPipeline.Plugin.Abstractions/PluginApi.cs"];
  assertRepoFiles(files);
  assertFlags(files, { host: true, plugin: true, ...Object.fromEntries(SYSTEM_KEYS.map(key => [key, false])) });
});

test("tests/run.mjs 改动触发四个 System 域", () => {
  const files = ["tests/run.mjs"];
  assertRepoFiles(files);
  assertFlags(files, Object.fromEntries(SYSTEM_KEYS.map(key => [key, true])));
});

test("共享路径改动按全量门禁处理", () => {
  for (const file of [".github/workflows/ci.yml", "tools/ci-domains.mjs"]) {
    assertRepoFiles([file]);
    const result = assertFlags([file], Object.fromEntries(CI_DOMAINS.map(domain => [domain.key, true])), file);
    assert.equal(result.failOpen, true, `${file}：应进入 fail-open`);
    assert.match(result.reason, /共享路径/u, `${file}：应说明共享路径原因`);
  }
});

test("未命中任何域的改动按全量门禁兜底", () => {
  const files = ["LICENSE", "SECURITY.md"];
  assertRepoFiles(files);
  const result = assertFlags(files, Object.fromEntries(CI_DOMAINS.map(domain => [domain.key, true])));
  assert.equal(result.failOpen, true);
  assert.match(result.reason, /未命中任何影响域/u);
});

test("Windows 分隔符的改动路径与正斜杠等价", () => {
  const expected = flagsOf(["src/Services/Update/UpdateService.cs"]);
  assert.deepEqual(flagsOf(["src\\Services\\Update\\UpdateService.cs"]), expected);
  assert.deepEqual(flagsOf([".\\src/Services/Update/UpdateService.cs"]), expected);
});

test("已命中路径与未知路径混合时按全量门禁处理", () => {
  const result = assertFlags(
    ["README.md", "future/unknown-file.txt"],
    Object.fromEntries(CI_DOMAINS.map(domain => [domain.key, true])),
  );
  assert.equal(result.failOpen, true);
  assert.match(result.reason, /未命中任何影响域/u);
  assert.deepEqual(result.unknown.map(item => item.file), ["future/unknown-file.txt"]);
});

test("重命名差异保留旧路径和新路径", () => {
  assert.deepEqual(
    parseNameStatusZ("R100\0docs/old.md\0docs/new.md\0M\0README.md\0D\0docs/removed.md\0"),
    ["docs/old.md", "docs/new.md", "README.md", "docs/removed.md"],
  );
});

test("路径穿越和绝对路径进入未知集合", () => {
  const result = evaluateDomains(["docs/../secret.txt", "C:\\outside.txt", "/outside.txt", "README.md"]);
  assert.equal(result.failOpen, true);
  assert.equal(result.unknown.length, 3);
  assert.equal(result.domains.docs.affected, true);
});

test("结构化测试域注册表包含宿主十 Area、前端四组和 System 八组", () => {
  assert.equal(HOST_TEST_AREAS.length, 10);
  assert.equal(FRONTEND_TEST_GROUPS.length, 4);
  assert.equal(SYSTEM_TEST_GROUPS.length, 8);
  assert.equal(validateTestDomainRegistry(TEST_DOMAIN_REGISTRY), true);
});

test("runner 分域并集完整且每个活动测试文件只有一个主要归属", () => {
  const result = spawnSync(process.execPath, ["tests/run.mjs", "list", "--json"], { cwd: repoRoot, encoding: "utf8" });
  assert.equal(result.status, 0, result.stderr);
  const plan = JSON.parse(result.stdout);
  for (const [directory, suffix, groups] of [
    ["tests/NexusPipeline.Tests", "Tests.cs", plan.hostAreas],
    ["frontend/src", ".test.ts", plan.frontendGroups],
  ]) {
    const files = fs.readdirSync(path.join(repoRoot, directory), { recursive: true })
      .filter(file => file.endsWith(suffix) && !/(?:^|[\\/])(?:bin|obj|node_modules)[\\/]/u.test(file))
      .map(file => `${directory}/${file.replaceAll("\\", "/")}`).sort();
    const assigned = groups.flatMap(group => group.tests).sort();
    assert.deepEqual(assigned, files, `${directory} 测试归属必须覆盖一次`);
  }
});

test("执行计划包含 SHA、精确文件、原因和 job 映射", () => {
  const plan = createExecutionPlan(["docs/DESIGN.md", "unknown.txt"], {
    base: "base-sha",
    head: "head-sha",
  });
  assert.equal(plan.base, "base-sha");
  assert.equal(plan.head, "head-sha");
  assert.equal(plan.failOpen, true);
  assert.deepEqual(plan.domains.docs.files, ["docs/DESIGN.md"]);
  assert.deepEqual(plan.domains.docs.jobs, ["docs-i18n"]);
  assert.deepEqual(plan.unknown.map(item => item.file), ["unknown.txt"]);
});

test("插件契约在缺少官方插件 checkout 时保留跨仓库测试文件身份", () => {
  const pluginDomain = CI_DOMAINS.find(domain => domain.key === "plugin");
  const missingCheckoutRoot = path.join(repoRoot, "tests", "fixtures", "missing-official-plugins-checkout");
  const expectedFiles = expectedTestFiles("domain", pluginDomain, { root: missingCheckoutRoot });
  assert.ok(
    expectedFiles.includes("NexusPipeline-Plugins/tools/Test-FrontendPlugins.mjs"),
    "插件契约的跨仓库测试文件必须参与计划校验",
  );
});

test("全量计划把全部逻辑域绑定到唯一 full-regression 物理 job", () => {
  const plan = createExecutionPlan([], {
    base: "",
    head: "manual-head",
    failOpen: true,
    reason: "按全量门禁执行",
    all: true,
  });
  for (const domain of Object.values(plan.domains)) {
    assert.equal(domain.affected, true);
    assert.deepEqual(domain.jobs, ["full-regression"]);
  }
});

test("dry-run 不写 GITHUB_OUTPUT 哨兵文件", () => {
  const outputPath = path.join(repoRoot, `.ci-dry-run-${process.pid}-${Date.now()}`);
  const script = path.join(repoRoot, "tools", "ci-changes.mjs");
  const result = spawnSync(process.execPath, [script, "--all", "--dry-run", "--github-output", outputPath], {
    cwd: repoRoot,
    encoding: "utf8",
  });
  try {
    assert.equal(result.status, 0, result.stderr);
    assert.match(result.stdout, /不写入输出文件/u);
    assert.equal(fs.existsSync(outputPath), false);
  } finally {
    if (fs.existsSync(outputPath)) fs.unlinkSync(outputPath);
  }
});

test("横切路径逐条命中四个 System 域", () => {
  const samples = {
    "src/*.cs": "src/Bootstrap.cs",
    "src/*.manifest": "src/app.manifest",
    "src/NexusPipeline.ico": "src/NexusPipeline.ico",
    "src/Application/**": "src/Application/ApplicationHost.cs",
    "src/Extensibility/**": "src/Extensibility/PluginContracts.cs",
    "src/Localization/**": "src/Localization/HostLocalization.cs",
    "src/Models/**": "src/Models/AppSettings.cs",
    "src/Persistence/**": "src/Persistence/ConfigStore.cs",
    "src/Plugins/**": "src/Plugins/Runtime/PluginManager.cs",
    "src/Utilities/**": "src/Utilities/Logger.cs",
    "src/Web/**": "src/Web/WebServer.cs",
    "*.csproj": "NexusPipeline.csproj",
    "src/**/*.csproj": "src/NexusPipeline.csproj",
    "global.json": "global.json",
    "build.cmd": "build.cmd",
    "tests/run.mjs": "tests/run.mjs",
    "tests/support/**": "tests/support/test-runtime.mjs",
    "tests/system/run-system.cmd": "tests/system/run-system.cmd",
    "tests/system/runtime-helper.mjs": "tests/system/runtime-helper.mjs",
  };
  assert.deepEqual(Object.keys(samples), [...SYSTEM_SHARED_PATHS], "横切路径清单与用例样例必须同步");
  // 仓库根目录没有 csproj 与 global.json，这两项只用于匹配断言。
  const absent = new Set(["*.csproj", "global.json"]);
  for (const [pattern, sample] of Object.entries(samples)) {
    if (!absent.has(pattern)) assertRepoFiles([sample]);
    assert.deepEqual(systemFlags([sample]), SYSTEM_KEYS, `${pattern} 的样例 ${sample} 应命中四个 System 域`);
  }
});

/** 宿主源码必须落在具体 System 域里：未映射的改动会触发全量门禁，需要在影响域清单里显式归类。 */
test("宿主源码逐文件命中至少一个 System 域", () => {
  // 插件 SDK 程序集只由 Gate B 与 Gate D 覆盖：它不改变宿主运行时行为，无需额外启动 System 作业。
  const uncovered = new Set(["src/NexusPipeline.Plugin.Abstractions"]);
  const files = [];
  const walk = directory => {
    for (const entry of fs.readdirSync(directory, { withFileTypes: true })) {
      const fullPath = path.join(directory, entry.name);
      if (entry.isDirectory()) {
        if (entry.name === "bin" || entry.name === "obj") continue;
        walk(fullPath);
        continue;
      }
      if (!entry.name.endsWith(".cs")) continue;
      const relative = path.relative(repoRoot, fullPath).replaceAll("\\", "/");
      if ([...uncovered].some(prefix => relative.startsWith(`${prefix}/`))) continue;
      files.push(relative);
    }
  };
  walk(path.join(repoRoot, "src"));
  assert.ok(files.length > 100, `宿主源码文件数量异常：${files.length}`);
  const missing = files.filter(file => systemFlags([file]).length === 0);
  assert.deepEqual(missing, [], `以下宿主源码未映射到任何 System 域：${missing.join(", ")}`);
});

test("System suite 文件归属各自的域", () => {
  const owners = [
    ["tests/system/mcp-smoke.mjs", ["system_runtime"]],
    ["tests/system/runtime-smoke.mjs", ["system_runtime"]],
    ["tests/system/config-smoke.mjs", ["system_runtime"]],
    ["tests/system/plugin-smoke.mjs", ["system_runtime"]],
    ["tests/system/judge-smoke.mjs", ["system_execution"]],
    ["tests/system/execution-resilience.mjs", ["system_execution"]],
    ["tests/system/fixtures/long-lived-child.mjs", ["system_execution"]],
    ["tests/system/emulator-smoke.mjs", ["system_emulator"]],
    ["tests/system/startup-update-smoke.mjs", ["system_update"]],
    ["tests/system/update-smoke.mjs", ["system_update"]],
    ["tests/e2e/tests/fixtures/adb-stub.cmd", ["system_emulator"]],
    ["tests/e2e/tests/fixtures/mumu-manager-stub.cmd", ["system_emulator"]],
    ["tests/system/runtime-helper.mjs", SYSTEM_KEYS],
    ["tests/system/run-system.cmd", SYSTEM_KEYS],
  ];
  for (const [file, expected] of owners) {
    assertRepoFiles([file]);
    assert.deepEqual(systemFlags([file]), expected, `${file} 的 System 归属`);
  }
});

test("工作流输出与四个 System 域 key 对齐", () => {
  const workflow = fs.readFileSync(path.join(repoRoot, ".github", "workflows", "ci.yml"), "utf8");
  assert.doesNotMatch(workflow, /outputs\.system\s*==/u, "工作流不应再判断旧的 system 输出");
  assert.doesNotMatch(workflow, /^\s+system:\s+\$\{\{/mu, "changes 输出不应再声明旧的 system key");
  for (const domain of CI_DOMAINS.filter(item => item.key.startsWith("system_"))) {
    assert.match(
      workflow,
      new RegExp(`^\\s+${domain.key}: \\$\\{\\{ steps\\.domains\\.outputs\\.${domain.key} \\}\\}$`, "mu"),
      `changes 输出应声明 ${domain.key}`,
    );
    assert.match(
      workflow,
      new RegExp(`needs\\.changes\\.outputs\\.${domain.key} == 'true'`, "u"),
      `System 作业应判断 ${domain.key}`,
    );
    assert.match(workflow, new RegExp(`^\\s+name: ${escapeRegExp(domain.gate)}$`, "mu"), `作业名应与 ${domain.key} 的 gate 一致`);
  }
  assert.match(workflow, /^ {2}full-regression:$/mu, "保留全量回归作业");
});

test("required-summary 显式等待所有物理 job 并消费 execution-plan", () => {
  const workflow = fs.readFileSync(path.join(repoRoot, ".github", "workflows", "ci.yml"), "utf8");
  const requiredSummary = workflow.slice(workflow.indexOf("  required-summary:"));
  const requiredJobs = [
    "changes",
    "frontend-unit",
    "host-core",
    "docs-i18n",
    "plugin-contract",
    "ui-smoke",
    "system-runtime-mcp",
    "system-execution",
    "system-emulator",
    "system-update",
    "full-regression",
  ];
  assert.match(workflow, /^ {2}required-summary:$/mu);
  assert.match(workflow, /^    if: \$\{\{ always\(\) \}\}$/mu);
  for (const job of requiredJobs) {
    assert.match(workflow, new RegExp(`^      - ${escapeRegExp(job)}$`, "mu"), `required-summary 缺少 ${job}`);
  }
  assert.match(workflow, /node tools\/ci-summary\.mjs --plan/u);
  assert.ok(workflow.includes('--headSha "$GITHUB_SHA"'), "required-summary 应校验当前提交 SHA");
  assert.match(workflow, /github\.event_name.*-eq 'schedule'.*github\.event_name.*-eq 'workflow_dispatch'/s);
  assert.match(workflow, /github\.event_name.*-eq 'schedule'.*workflow_dispatch'[\s\S]*node tools\/ci-changes\.mjs --all/s);
  assert.match(workflow, /full-regression:[\s\S]*if:.*github\.event_name == 'schedule'.*github\.event_name == 'workflow_dispatch'/s);
  assert.match(requiredSummary, /uses: actions\/download-artifact@v4/u, "required-summary 应从 artifact 读取物理 Job manifest");
  assert.match(requiredSummary, /pattern: nexus-ci-manifest-\*/u, "required-summary 应下载物理 Job manifest 集合");
  assert.doesNotMatch(requiredSummary, /(?:FRONTEND|HOST|DOCS|PLUGIN|UI|SYSTEM_RUNTIME|SYSTEM_EXECUTION|SYSTEM_EMULATOR|SYSTEM_UPDATE|FULL)_MANIFEST/u, "required-summary 不应把完整 manifest 放入 Job 环境变量");
  const fullRegression = workflow.slice(workflow.indexOf("  full-regression:"), workflow.indexOf("  required-summary:"));
  assert.match(fullRegression, /NEXUS_PLUGIN_REPO_ROOT:\s*\$\{\{\s*github\.workspace\s*\}\}\/NexusPipeline-Plugins/u, "全量回归应显式传递插件仓库路径");
});

test("workflow_dispatch 的插件候选 SHA 进入 changes execution-plan", () => {
  const workflow = fs.readFileSync(path.join(repoRoot, ".github", "workflows", "ci.yml"), "utf8");
  const changesBlock = workflow.slice(workflow.indexOf("  changes:"), workflow.indexOf("  # Gate A:"));
  assert.match(changesBlock, /NEXUS_CANDIDATE_REF:\s*\$\{\{\s*inputs\.plugins_ref\s*\}\}/u);
  assert.match(changesBlock, /node tools\/ci-changes\.mjs --all/u);
});

test("v0.16.6 专属验收分别保留 Test Host 与管理员真实计时模式", () => {
  const workflow = fs.readFileSync(path.join(repoRoot, ".github", "workflows", "ci.yml"), "utf8");
  assert.match(workflow, /test_host_acceptance:[\s\S]*type: boolean/u);
  assert.match(workflow, /admin_execution_acceptance:[\s\S]*type: boolean/u);

  const testHost = workflow.slice(workflow.indexOf("  version-acceptance-test-host:"), workflow.indexOf("  version-acceptance-admin-execution:"));
  assert.match(testHost, /NEXUS_CI_MODE: test-host/u);
  assert.match(testHost, /NEXUS_TIME_SCALE: "1"/u);
  assert.match(testHost, /node tests\\run\.mjs codex system --group update --realtime/u);

  const adminExecution = workflow.slice(workflow.indexOf("  version-acceptance-admin-execution:"), workflow.indexOf("  # 每周计划与手动触发执行完整管理员门禁"));
  assert.match(adminExecution, /NEXUS_CI_MODE: admin/u);
  assert.match(adminExecution, /NEXUS_TIME_SCALE: "1"/u);
  assert.match(adminExecution, /管理员完整性自检/u);
  assert.match(adminExecution, /node tests\\run\.mjs admin system --group execution --realtime/u);
});
