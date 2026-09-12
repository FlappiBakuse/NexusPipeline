import test from "node:test";
import assert from "node:assert/strict";
import fs from "node:fs";
import path from "node:path";
import { fileURLToPath } from "node:url";
import { CI_DOMAINS, SYSTEM_SHARED_PATHS } from "../../tools/ci-domains.mjs";
import { evaluateDomains } from "../../tools/ci-changes.mjs";

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
    "src/Plugins/**": "src/Plugins/PluginManager.cs",
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
