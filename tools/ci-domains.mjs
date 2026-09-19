import crypto from "node:crypto";
export { globToRegExp } from "./path-glob.mjs";

/**
 * CI 影响域清单：门禁分组与各自的触发路径。
 *
 * 这张表是 CI 影响域拆分的唯一来源：`.github/workflows/ci.yml` 的 changes 步骤调用
 * `node tools/ci-changes.mjs` 计算本次改动命中哪些分组，再据此决定各个 Gate 是否执行。
 * 路径使用仓库根目录相对路径，`**` 跨越任意层级，`*` 只匹配单层内的字符。
 * System 按 suite 影响域拆成四个独立分组，横切文件在多个分组的 `paths` 里重复出现。
 */

/**
 * 四个 System 影响域的共用横切路径。
 *
 * 这些文件位于每个 System suite 都会走到的运行时路径上：宿主启动与入口、配置与数据持久化、
 * Web 控制面、进程与日志工具、插件加载、构建与测试入口。改动其中任意一条都会同时影响
 * 四个 System 域，因此在四个 System 域的 `paths` 里显式列出。
 */
export const SYSTEM_SHARED_PATHS = [
  "src/*.cs",
  "src/*.manifest",
  "src/NexusPipeline.ico",
  "src/Host/**",
  "src/Modules/Plugins/**",
  "src/Platform/**",
  "src/Shared/**",
  "*.csproj",
  "src/**/*.csproj",
  "global.json",
  "build.cmd",
  "tests/run.mjs",
  "tests/support/**",
  "tests/system/run-system.cmd",
  "tests/system/runtime-helper.mjs",
];

export const CI_DOMAINS = [
  {
    key: "frontend",
    gate: "Gate A 前端 Unit + Build",
    jobs: ["frontend-unit"],
    testPaths: ["tests/run.mjs frontend", "frontend/src/**/*.test.ts"],
    paths: [
      "frontend/src/**",
      "frontend/index.html",
      "frontend/package.json",
      "frontend/package-lock.json",
      "frontend/vite.config.ts",
      "frontend/vitest.config.ts",
      "frontend/tsconfig.json",
      "tools/frontend-boundaries.mjs",
      "tests/tools/frontend-boundaries.test.mjs",
    ],
  },
  {
    key: "host",
    gate: "Gate B 宿主 Unit + Component",
    jobs: ["host-core"],
    testPaths: ["tests/run.mjs unit", "tests/NexusPipeline.Tests/**/*.cs"],
    paths: [
      "src/**",
      "tests/NexusPipeline.Tests/**",
      "tests/fixtures/**",
      "update-policy.json",
      "*.csproj",
      "global.json",
      "build.cmd",
      "tests/run.mjs",
    ],
  },
  {
    key: "docs",
    gate: "Gate C 文档 + i18n",
    jobs: ["docs-i18n"],
    testPaths: [
      "tests/documentation/documentation-consistency.mjs",
      "tests/documentation/i18n-consistency.mjs",
      "tests/documentation/i18n-semantic-consistency.mjs",
      "tests/documentation/i18n-audit-consistency.mjs",
      "tests/documentation/backend-i18n-audit.mjs",
      "tests/documentation/native-scrollbar-audit.mjs",
      "tests/documentation/test-policy-consistency.mjs",
      "tests/tools/docs-index.test.mjs",
    ],
    paths: [
      "docs/**",
      "README.md",
      "CHANGELOG.md",
      "frontend/public/i18n/**",
      "frontend/src/router.ts",
      "tests/documentation/**",
      "tools/docs-index.mjs",
      "tests/tools/docs-index.test.mjs",
    ],
  },
  {
    key: "plugin",
    gate: "Gate D 插件契约",
    jobs: ["plugin-contract"],
    testPaths: [
      "frontend/contracts/official-plugins.test.ts",
      "NexusPipeline-Plugins/tools/Test-FrontendPlugins.mjs",
      "tests/tools/plugin-source-layout.test.mjs",
    ],
    paths: [
      "src/NexusPipeline.Plugin.Abstractions/**",
      "src/Modules/Plugins/**",
      "frontend/src/plugin-bridge/**",
      "frontend/src/ui/**",
      "tools/frontend-boundaries.mjs",
      "tests/tools/frontend-boundaries.test.mjs",
      "docs/PLUGIN_API.md",
      "tests/NexusPipeline.Tests/Plugins/PluginExtensionContractTests.cs",
      "tests/NexusPipeline.Tests/Plugins/PluginCapabilityTests.cs",
      "tests/NexusPipeline.Tests/ControlPlane/PluginContributionRouteTests.cs",
      "tests/NexusPipeline.Tests/Plugins/PluginHostServicesTests.cs",
      "tests/NexusPipeline.Tests/Plugins/PluginManagementParityTests.cs",
      "tests/NexusPipeline.Tests/Plugins/PluginManagerTests.cs",
      "tests/NexusPipeline.Tests/Plugins/PluginRepositoryCatalogTests.cs",
      "tests/NexusPipeline.Tests/Plugins/PluginInstallRecoveryTests.cs",
      "tests/NexusPipeline.Tests/Plugins/PluginConfigValidatorTests.cs",
    ],
  },
  {
    key: "ui",
    gate: "Gate E 管理员 UI Smoke",
    jobs: ["ui-smoke"],
    testPaths: ["tests/run.mjs admin ui", "tests/e2e/tests/**/*.smoke.spec.mjs"],
    paths: [
      "frontend/src/features/**",
      "frontend/src/app/**",
      "frontend/src/router.ts",
      "frontend/src/platform/**",
      "frontend/src/plugin-bridge/**",
      "frontend/src/ui/**",
      "src/ControlPlane/**",
      "tests/e2e/**",
    ],
  },
  {
    key: "system_runtime",
    gate: "Gate F System Runtime/MCP",
    jobs: ["system-runtime-mcp"],
    testPaths: ["tests/run.mjs admin system runtime control config plugins"],
    paths: [
      ...SYSTEM_SHARED_PATHS,
      "src/ControlPlane/**",
      "src/Modules/Configuration/**",
      "src/Modules/Diagnostics/**",
      "src/Modules/Notifications/**",
      "src/Modules/Queues/**",
      "src/Modules/Scripts/**",
      "src/Modules/Settings/**",
      "src/Modules/Users/**",
      "tests/system/mcp-smoke.mjs",
      "tests/system/runtime-smoke.mjs",
      "tests/system/config-smoke.mjs",
      "tests/system/plugin-smoke.mjs",
    ],
  },
  {
    key: "system_execution",
    gate: "Gate F System Execution",
    jobs: ["system-execution"],
    testPaths: ["tests/run.mjs admin system execution judge"],
    paths: [
      ...SYSTEM_SHARED_PATHS,
      "src/Modules/Execution/*.cs",
      "src/Modules/Execution/Contracts/**",
      "src/Modules/Execution/Judgement/**",
      "src/Modules/Execution/Monitoring/**",
      "src/Modules/Execution/Realtime/**",
      "src/Modules/Execution/Runtime/**",
      "src/Modules/History/**",
      "src/Modules/Scheduling/**",
      "tests/system/execution-resilience.mjs",
      "tests/system/fixtures/**",
      "tests/system/judge-smoke.mjs",
    ],
  },
  {
    key: "system_emulator",
    gate: "Gate F System Emulator",
    jobs: ["system-emulator"],
    testPaths: ["tests/run.mjs admin system emulator"],
    paths: [
      ...SYSTEM_SHARED_PATHS,
      "src/Modules/Execution/Targets/**",
      "tests/e2e/tests/fixtures/adb-stub.cmd",
      "tests/e2e/tests/fixtures/mumu-manager-stub.cmd",
      "tests/system/emulator-smoke.mjs",
    ],
  },
  {
    key: "system_update",
    gate: "Gate F System Update",
    jobs: ["system-update"],
    testPaths: ["tests/run.mjs admin system update"],
    paths: [
      ...SYSTEM_SHARED_PATHS,
      "src/Modules/Updates/**",
      "update-policy.json",
      "tools/validate-update-policy-history.mjs",
      "tests/system/startup-update-smoke.mjs",
      "tests/system/update-smoke.mjs",
      "tests/tools/update-policy-history.test.mjs",
    ],
  },
];

/** 当前执行计划协议版本。影响域注册表版本与执行计划版本彼此独立。 */
export const CI_EXECUTION_PLAN_SCHEMA_VERSION = 2;

/**
 * 物理 Job 的运行契约。逻辑组只能绑定到这里登记过的 Job，汇总器据此校验模式和完整性。
 * `ci` 表示普通 CI 反馈环境；`admin` 表示生产管理员门禁；`full-regression` 仍由 admin all 承载。
 */
export const CI_JOB_CONTRACTS = Object.freeze({
  "frontend-unit": Object.freeze({ mode: "ci", requiredIntegrity: "none", requiredChecks: ["typecheck", "tests", "build"] }),
  "host-core": Object.freeze({ mode: "ci", requiredIntegrity: "none", requiredChecks: ["build", "tests"] }),
  "docs-i18n": Object.freeze({ mode: "ci", requiredIntegrity: "none", requiredChecks: ["tests"] }),
  "plugin-contract": Object.freeze({ mode: "ci", requiredIntegrity: "none", requiredChecks: ["build", "tests", "contract"] }),
  "ui-smoke": Object.freeze({ mode: "admin", requiredIntegrity: "high-or-system", requiredChecks: ["build", "tests"] }),
  "system-runtime-mcp": Object.freeze({ mode: "admin", requiredIntegrity: "high-or-system", requiredChecks: ["build", "tests"] }),
  "system-execution": Object.freeze({ mode: "admin", requiredIntegrity: "high-or-system", requiredChecks: ["build", "tests"] }),
  "system-emulator": Object.freeze({ mode: "admin", requiredIntegrity: "high-or-system", requiredChecks: ["build", "tests"] }),
  "system-update": Object.freeze({ mode: "admin", requiredIntegrity: "high-or-system", requiredChecks: ["build", "tests"] }),
  "full-regression": Object.freeze({ mode: "admin", requiredIntegrity: "high-or-system", requiredChecks: ["build", "tests"] }),
});

/**
 * Plugin Contract Job 在对应 Gate 未被单独选中时承接的现役契约测试。
 * 这些回退归属必须同时进入 execution-plan，runner 才能把实际执行与 Summary 对账。
 */
export const PLUGIN_CONTRACT_FALLBACKS = Object.freeze({
  host: Object.freeze(["plugins"]),
  frontend: Object.freeze(["ui", "bridge"]),
});

/** 逻辑组 ID 是计划、runner manifest 和 Required Summary 之间的稳定连接键。 */
export function logicalGroupId(kind, key) {
  return `${String(kind)}:${String(key)}`;
}

/**
 * 选择身份只绑定注册表定义，不绑定本机绝对路径或当前测试计数。
 * 实际测试引擎结果仍需由 runner manifest 单独提交。
 */
export function testSelectionIdentity(kind, group) {
  const definition = {
    kind,
    key: group?.key || "",
    paths: group?.paths || [],
    testPaths: group?.testPaths || [],
    suitePaths: group?.suitePaths || [],
    runtimeNames: group?.runtimeNames || [],
  };
  return crypto.createHash("sha256").update(JSON.stringify(definition), "utf8").digest("hex");
}

/**
 * 计划中的测试选择器必须描述实际会被 runner/CI 步骤选中的相对路径。
 * 宿主 Area 的注册表为了便于作者维护使用测试文件名/通配符，这里统一补上测试工程根。
 */
export function expectedTestSelectors(kind, group) {
  const selectors = group?.testPaths || group?.suitePaths || [];
  if (kind === "host") return selectors.map(selector => `tests/NexusPipeline.Tests/${selector}`);
  return [...selectors];
}

/** 当前模式下由计划明确声明的合法专属排除。 */
export function plannedExclusions(kind, key, mode) {
  if (kind === "system" && key === "update" && mode === "admin") {
    return [{
      testId: "update:swap-ready",
      reason: "SwapReady 故障注入需要 Codex/Test Host 阶段注入，管理员生产模式不启用该开关",
      mode: "test-host",
      alternativeMode: "test-host",
    }];
  }
  return [];
}

/**
 * 测试入口共用的结构化注册表。
 *
 * CI_DOMAINS 负责工作流 Gate；下面的 Area、前端组、System 组和治理域负责 runner 的
 * 细分选择。它们放在同一模块中，路径归属、依赖方向和测试入口可以由工具与文档检查共同读取。
 */
export const HOST_TEST_AREAS = [
  { key: "core", paths: ["src/Host/**", "src/Shared/**", "src/Platform/**"], testPaths: ["Host/RuntimeContextQueryTests.cs", "Host/RuntimeEntityStateTests.cs", "Host/RuntimeInitializationBoundaryTests.cs", "Host/HostInstanceTests.cs", "Host/HostLifecycleLocalizationTests.cs", "Platform/NativePathPickerServiceTests.cs", "Platform/RuntimeGuardsTests.cs", "Platform/ProcessCleanupResultTests.cs", "Shared/EntityNameRulesTests.cs", "Shared/LocalizationTests.cs", "Shared/NexusVersionTests.cs"] },
  { key: "persistence", paths: ["src/Modules/**/Persistence/**", "src/Modules/Configuration/Snapshots/**"], testPaths: ["Platform/JsonStoreTests.cs", "Platform/RuntimeStateLayoutTests.cs", "Scripts/ScriptPersistenceTests.cs"] },
  { key: "config", paths: ["src/Modules/Configuration/**", "src/Modules/Settings/**", "src/Modules/Users/**", "src/Modules/Scripts/**", "src/Modules/Queues/**"], testPaths: ["Configuration/**/*.cs", "Settings/**/*.cs", "Users/User*.cs", "Users/ScriptBindingCleanupTests.cs", "Queues/**/*.cs", "Scripts/ScriptInstanceTests.cs", "Scripts/ScriptPluginAvailabilityTests.cs", "Scripts/ScriptSpecInputOverrideTests.cs"] },
  { key: "execution", paths: ["src/Modules/Execution/**"], testPaths: ["Execution/AdbEndpointPolicyTests.cs", "Execution/AdmissionFailurePolicyTests.cs", "Execution/AttemptHookPolicyTests.cs", "Execution/EmulatorSupportTests.cs", "Execution/Execution*.cs", "Execution/HostMaintenanceLeaseTests.cs", "Execution/LogCandidateStartPolicyTests.cs", "Execution/ParallelAdmissionTests.cs", "Execution/PendingSystemActionTests.cs", "Execution/PerUserExecutionPlanTests.cs", "Platform/ProcessTreeTests.cs", "Execution/RecentScreenshotCacheTests.cs", "Execution/RunAttemptResultTests.cs", "Execution/RunScreenshotStoreTests.cs", "Execution/RuntimeWorkerTests.cs", "Execution/SelfManagedPcLaunchTests.cs", "Execution/UnavailablePluginExecutionTests.cs"] },
  { key: "scheduling", paths: ["src/Modules/Scheduling/**"], testPaths: ["Scheduling/**/*.cs", "Users/RunDaysTests.cs"] },
  { key: "judgement", paths: ["src/Modules/Execution/Judgement/**", "src/Modules/Execution/Monitoring/**"], testPaths: ["Execution/Judge*.cs", "Execution/SessionJudgeTests.cs", "Execution/KeywordRuleTests.cs", "Execution/ResultCollectorTests.cs", "Execution/LogPatternTests.cs"] },
  { key: "plugins", paths: ["src/Modules/Plugins/**", "src/NexusPipeline.Plugin.Abstractions/**"], testPaths: ["Plugins/**/*.cs", "Execution/EmulatorPluginSupportTests.cs"] },
  { key: "update", paths: ["src/Modules/Updates/**", "update-policy.json"], testPaths: ["Updates/**/*.cs", "Host/RestartCommandTests.cs"] },
  { key: "control", paths: ["src/ControlPlane/**", "src/Modules/Execution/Realtime/**"], testPaths: ["ControlPlane/**/*.cs", "Execution/RealtimeEventBusTests.cs", "Host/HostRestartCoordinatorTests.cs"] },
  { key: "observability", paths: ["src/Modules/Diagnostics/**", "src/Modules/History/**", "src/Modules/Notifications/**", "src/Modules/Execution/Monitoring/**", "src/Platform/**"], testPaths: ["Diagnostics/**/*.cs", "History/**/*.cs", "Notifications/**/*.cs", "Execution/LogMonitorTests.cs", "Platform/NetworkUtilityTests.cs", "Platform/ProxyConfigurationTests.cs", "Shared/LoggerTests.cs"] },
];

export const FRONTEND_TEST_GROUPS = [
  { key: "ui", paths: ["frontend/src/ui/**"], testPaths: ["frontend/src/ui/**.test.ts", "frontend/src/ui/**/*.test.ts"] },
  { key: "bridge", paths: ["frontend/src/plugin-bridge/**"], testPaths: ["frontend/src/plugin-bridge/**/*.test.ts"] },
  { key: "platform", paths: ["frontend/src/platform/**", "frontend/src/stores/**", "frontend/src/router.ts"], testPaths: ["frontend/src/platform/**/*.test.ts", "frontend/src/stores/**/*.test.ts", "frontend/src/router*.test.ts"] },
  { key: "features", paths: ["frontend/src/features/**", "frontend/src/app/**"], testPaths: ["frontend/src/features/**/*.test.ts", "frontend/src/app/**/*.test.ts"] },
];

// 一个领域变化后必须重新验证的现役调用方，按传递闭包选择测试。
export const TEST_DOMAIN_CONSUMERS = {
  host: {
    core: HOST_TEST_AREAS.filter(area => area.key !== "core").map(area => area.key),
    persistence: ["config", "execution", "scheduling", "plugins", "update", "control", "observability"],
    config: ["execution", "scheduling", "plugins", "control"],
    execution: ["scheduling", "control", "observability"],
    scheduling: ["control"],
    judgement: ["execution"],
    plugins: ["config", "execution", "control", "observability"],
    update: ["control"],
    control: [],
    observability: ["execution", "control"],
  },
  frontend: { ui: ["bridge", "platform", "features"], bridge: ["features"], platform: ["bridge", "features"], features: [] },
};

export const SYSTEM_TEST_GROUPS = [
  { key: "runtime", ciDomain: "system_runtime", suitePaths: ["tests/system/runtime-smoke.mjs"], runtimeNames: ["runtime-runtime"] },
  { key: "control", ciDomain: "system_runtime", suitePaths: ["tests/system/mcp-smoke.mjs"], runtimeNames: ["runtime-mcp"] },
  { key: "config", ciDomain: "system_runtime", suitePaths: ["tests/system/config-smoke.mjs"], runtimeNames: ["runtime-config"] },
  { key: "execution", ciDomain: "system_execution", suitePaths: ["tests/system/execution-resilience.mjs"], runtimeNames: ["runtime-execution-resilience"] },
  { key: "judge", ciDomain: "system_execution", suitePaths: ["tests/system/judge-smoke.mjs"], runtimeNames: ["runtime-judge"] },
  { key: "emulator", ciDomain: "system_emulator", suitePaths: ["tests/system/emulator-smoke.mjs"], runtimeNames: ["runtime-emulator"] },
  { key: "plugins", ciDomain: "system_runtime", suitePaths: ["tests/system/plugin-smoke.mjs"], runtimeNames: ["runtime-plugins"] },
  { key: "update", ciDomain: "system_update", suitePaths: ["tests/system/startup-update-smoke.mjs", "tests/system/update-smoke.mjs"], runtimeNames: ["runtime-startup-update", "runtime-update"] },
];

export const GOVERNANCE_DOMAINS = [
  { key: "docs-links-contracts", paths: ["docs/**", "README.md", "CHANGELOG.md", "tools/docs-index.mjs", "tests/tools/docs-index.test.mjs"], testPaths: ["tests/documentation/documentation-consistency.mjs", "tests/tools/docs-index.test.mjs"] },
  { key: "i18n-functional", paths: ["frontend/public/i18n/**", "src/Shared/Localization/**"], testPaths: ["tests/documentation/i18n-*.mjs"] },
  { key: "architecture-boundaries", paths: ["tools/frontend-boundaries.mjs", "tests/tools/**"], testPaths: ["tests/tools/ci-domains.test.mjs", "tests/tools/frontend-boundaries.test.mjs"] },
  { key: "test-policy", paths: ["AGENTS.md", "tests/documentation/test-policy-consistency.mjs"], testPaths: ["tests/documentation/test-policy-consistency.mjs"] },
  { key: "ci-tooling", paths: [".github/workflows/**", "tools/ci-*.mjs", "tools/artifact-manifest.mjs", "tools/test-selection.mjs", "tools/test-results.mjs", "tests/run.mjs", "tests/tools/ci-summary.test.mjs", "tests/tools/artifact-manifest.test.mjs", "tests/tools/test-results.test.mjs", "tests/tools/runner-manifest.test.mjs"], testPaths: ["tests/tools/ci-domains.test.mjs", "tests/tools/ci-fingerprint.test.mjs", "tests/tools/ci-summary.test.mjs", "tests/tools/artifact-manifest.test.mjs", "tests/tools/test-results.test.mjs", "tests/tools/runner-manifest.test.mjs"] },
];

export const TEST_DOMAIN_REGISTRY = Object.freeze({
  schemaVersion: 1,
  hostAreas: HOST_TEST_AREAS,
  frontendGroups: FRONTEND_TEST_GROUPS,
  systemGroups: SYSTEM_TEST_GROUPS,
  governanceDomains: GOVERNANCE_DOMAINS,
});

export function validateTestDomainRegistry(registry = TEST_DOMAIN_REGISTRY) {
  const collections = [
    ["hostAreas", registry.hostAreas ?? []],
    ["frontendGroups", registry.frontendGroups ?? []],
    ["systemGroups", registry.systemGroups ?? []],
    ["governanceDomains", registry.governanceDomains ?? []],
  ];
  for (const [collectionName, groups] of collections) {
    const keys = groups.map(group => group.key);
    const duplicates = keys.filter((key, index) => keys.indexOf(key) !== index);
    if (duplicates.length > 0) throw new Error(`${collectionName} 存在重复测试域：${[...new Set(duplicates)].join(", ")}`);
    for (const group of groups) {
      if (!group.key || (!Array.isArray(group.paths) && !Array.isArray(group.suitePaths))) {
        throw new Error(`测试域缺少 key 或路径：${JSON.stringify(group)}`);
      }
      if (Array.isArray(group.suitePaths) && group.suitePaths.length === 0) {
        throw new Error(`System 测试域不得注册空 suite：${group.key}`);
      }
      if (Array.isArray(group.suitePaths)
        && (!Array.isArray(group.runtimeNames) || group.runtimeNames.length !== group.suitePaths.length)) {
        throw new Error(`System 测试域 runtimeName 与 suite 数量不一致：${group.key}`);
      }
      if (group.ciDomain && !CI_DOMAINS.some(domain => domain.key === group.ciDomain)) {
        throw new Error(`System 测试域引用了未知 CI 域：${group.key} -> ${group.ciDomain}`);
      }
      if (Array.isArray(group.jobs) && group.jobs.some(job => typeof job !== "string" || !job)) {
        throw new Error(`测试域包含无效 job：${group.key}`);
      }
    }
  }
  return true;
}

validateTestDomainRegistry();

/**
 * 共享触发路径：命中时全部 Gate 都视为受影响。
 * CI 工作流与影响域清单本身的改动会改变所有 Gate 的判定，因此不做分组过滤。
 */
export const CI_SHARED_PATHS = [
  ".github/workflows/**",
  "tools/ci-changes.mjs",
  "tools/ci-domains.mjs",
  "tools/source-hash.mjs",
  "tools/ci-fingerprint.mjs",
  "tests/tools/ci-fingerprint.test.mjs",
];
