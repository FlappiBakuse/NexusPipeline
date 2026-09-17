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
  "src/Application/**",
  "src/Extensibility/**",
  "src/Localization/**",
  "src/Models/**",
  "src/Persistence/**",
  "src/Plugins/**",
  "src/Utilities/**",
  "src/Web/**",
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
    paths: [
      "src/NexusPipeline.Plugin.Abstractions/**",
      "src/Plugins/**",
      "frontend/src/plugin-bridge/**",
      "frontend/src/ui/**",
      "tools/frontend-boundaries.mjs",
      "tests/tools/frontend-boundaries.test.mjs",
      "docs/PLUGIN_API.md",
      "tests/NexusPipeline.Tests/PluginExtensionContractTests.cs",
      "tests/NexusPipeline.Tests/PluginCapabilityTests.cs",
      "tests/NexusPipeline.Tests/PluginContributionRouteTests.cs",
      "tests/NexusPipeline.Tests/PluginAvailabilityPolicyTests.cs",
      "tests/NexusPipeline.Tests/PluginHostServicesTests.cs",
      "tests/NexusPipeline.Tests/PluginManagementParityTests.cs",
      "tests/NexusPipeline.Tests/PluginManagerTests.cs",
      "tests/NexusPipeline.Tests/PluginRepositoryCatalogTests.cs",
      "tests/NexusPipeline.Tests/PluginInstallRecoveryTests.cs",
      "tests/NexusPipeline.Tests/PluginConfigValidatorTests.cs",
    ],
  },
  {
    key: "ui",
    gate: "Gate E 管理员 UI Smoke",
    jobs: ["ui-smoke"],
    paths: [
      "frontend/src/features/**",
      "frontend/src/app/**",
      "frontend/src/router.ts",
      "frontend/src/platform/**",
      "frontend/src/plugin-bridge/**",
      "frontend/src/ui/**",
      "src/Web/**",
      "tests/e2e/**",
    ],
  },
  {
    key: "system_runtime",
    gate: "Gate F System Runtime/MCP",
    jobs: ["system-runtime-mcp"],
    paths: [
      ...SYSTEM_SHARED_PATHS,
      "src/Cli/**",
      "src/Mcp/**",
      "src/Services/AppearanceLegacyMigration.cs",
      "src/Services/Audit.cs",
      "src/Services/ConfigStoreMetadata.cs",
      "src/Services/ConfigSwap/**",
      "src/Services/ConfigSwapPaths.cs",
      "src/Services/ConfigSwapPrimitives.cs",
      "src/Services/ConfigSwapSession.cs",
      "src/Services/ConfigWorkDirMaintenance.cs",
      "src/Services/Configuration/**",
      "src/Services/Diagnostics/**",
      "src/Services/EntityNameRules.cs",
      "src/Services/FirewallRule.cs",
      "src/Services/HostInstance.cs",
      "src/Services/HostRestartCoordinator.cs",
      "src/Services/Limits.cs",
      "src/Services/NativePathPickerService.cs",
      "src/Services/NetInfo.cs",
      "src/Services/Networking/**",
      "src/Services/Notification/**",
      "src/Services/PluginAvailability.cs",
      "src/Services/Realtime/**",
      "src/Services/SmtpSender.cs",
      "src/Services/TaskRegistration.cs",
      "src/Services/UserConfigManager.cs",
      "src/Services/WebhookSender.cs",
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
    paths: [
      ...SYSTEM_SHARED_PATHS,
      "src/Services/DispatchCenter.cs",
      "src/Services/Execution/**",
      "src/Services/History/**",
      "src/Services/Judgement/**",
      "src/Services/LogMonitor.cs",
      "src/Services/Realtime/**",
      "src/Services/RunSession.cs",
      "src/Services/Scheduling/**",
      "src/Services/ScriptBindingCleanup.cs",
      "src/Services/UserBindingOverrideResolver.cs",
      "tests/system/execution-resilience.mjs",
      "tests/system/fixtures/**",
      "tests/system/judge-smoke.mjs",
    ],
  },
  {
    key: "system_emulator",
    gate: "Gate F System Emulator",
    jobs: ["system-emulator"],
    paths: [
      ...SYSTEM_SHARED_PATHS,
      "src/Services/EmulatorDrivers.cs",
      "src/Services/EmulatorSupport.cs",
      "tests/e2e/tests/fixtures/adb-stub.cmd",
      "tests/e2e/tests/fixtures/mumu-manager-stub.cmd",
      "tests/system/emulator-smoke.mjs",
    ],
  },
  {
    key: "system_update",
    gate: "Gate F System Update",
    jobs: ["system-update"],
    paths: [
      ...SYSTEM_SHARED_PATHS,
      "src/Services/Update/**",
      "update-policy.json",
      "tools/validate-update-policy-history.mjs",
      "tests/system/startup-update-smoke.mjs",
      "tests/system/update-smoke.mjs",
      "tests/tools/update-policy-history.test.mjs",
    ],
  },
];

/**
 * 测试入口共用的结构化注册表。
 *
 * CI_DOMAINS 负责工作流 Gate；下面的 Area、前端组、System 组和治理域负责 runner 的
 * 细分选择。它们放在同一模块中，路径归属、依赖方向和测试入口可以由工具与文档检查共同读取。
 */
export const HOST_TEST_AREAS = [
  { key: "core", paths: ["src/Models/**", "src/RuntimeContext.cs", "src/Utilities/**"], testPaths: ["RuntimeContextQueryTests.cs", "RuntimeEntityStateTests.cs", "RuntimeInitializationBoundaryTests.cs", "RuntimeTests.cs", "EntityNameRulesTests.cs", "LimitsTests.cs", "NexusVersionTests.cs", "HostInstanceTests.cs", "LocalizationTests.cs", "NativePathPickerServiceTests.cs", "RuleTests.cs"] },
  { key: "persistence", paths: ["src/Persistence/**", "src/Services/ConfigStoreMetadata.cs", "src/Services/ConfigSwap/**"], testPaths: ["JsonStoreTests.cs", "ScriptPersistenceTests.cs", "RuntimeStateLayoutTests.cs"] },
  { key: "config", paths: ["src/Services/Configuration/**", "src/Services/ConfigSwap*.cs", "src/Services/ConfigWorkDirMaintenance.cs"], testPaths: ["Config*.cs", "ExtraConfigSyncTests.cs", "UserIdRecoveryTests.cs", "BindingAndSchedulerTests.cs", "UserBindingOverrideTests.cs", "UserGlobalSettingsValidationTests.cs", "ScriptBindingCleanupTests.cs"] },
  { key: "execution", paths: ["src/Services/Execution/**", "src/Services/RunSession.cs", "src/Services/DispatchCenter.cs"], testPaths: ["Execution*.cs", "RunAttemptResultTests.cs", "RunScreenshotStoreTests.cs", "ProcessTreeTests.cs", "RecentScreenshotCacheTests.cs", "ParallelAdmissionTests.cs", "RegressionTests.cs", "SelfManagedPcLaunchTests.cs", "EmulatorSupportTests.cs"] },
  { key: "scheduling", paths: ["src/Services/Scheduling/**", "src/Services/TaskRegistration.cs"], testPaths: ["Scheduler*.cs", "RunDaysTests.cs"] },
  { key: "judgement", paths: ["src/Services/Judgement/**", "src/Services/KeywordRule.cs", "src/Services/Rule*.cs"], testPaths: ["Judge*.cs", "SessionJudgeTests.cs", "KeywordRuleTests.cs", "ResultCollectorTests.cs", "LogPatternTests.cs"] },
  { key: "plugins", paths: ["src/Plugins/**", "src/NexusPipeline.Plugin.Abstractions/**"], testPaths: ["Plugin*.cs", "DataSpecializedPlugin*.cs", "EmulatorPluginSupportTests.cs", "ManagedPluginTests.cs", "AppearanceLegacyMigrationTests.cs", "OfficialPluginSourcePathTests.cs"] },
  { key: "update", paths: ["src/Services/Update/**", "update-policy.json"], testPaths: ["Update*.cs", "StartupUpdate*.cs", "RestartCommandTests.cs"] },
  { key: "control", paths: ["src/Web/**", "src/Cli/**", "src/Mcp/**", "src/Services/Realtime/**"], testPaths: ["WebApiContractTests.cs", "CliControlContractTests.cs", "Mcp*.cs", "RealtimeEventBusTests.cs", "HostRestartCoordinatorTests.cs"] },
  { key: "observability", paths: ["src/Services/Diagnostics/**", "src/Services/History/**", "src/Services/Notification/**", "src/Services/LogMonitor.cs", "src/Utilities/Logger.cs"], testPaths: ["Diagnostic*.cs", "HistoryServiceTests.cs", "Notification*.cs", "LogMonitorTests.cs", "LoggerTests.cs", "WebhookSenderTests.cs", "ProxyConfigurationTests.cs", "SmtpSenderTests.cs", "NetworkUtilityTests.cs"] },
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
  { key: "i18n-functional", paths: ["frontend/public/i18n/**", "src/Localization/**"], testPaths: ["tests/documentation/i18n-*.mjs"] },
  { key: "architecture-boundaries", paths: ["tools/frontend-boundaries.mjs", "tests/tools/**"], testPaths: ["tests/tools/ci-domains.test.mjs", "tests/tools/frontend-boundaries.test.mjs"] },
  { key: "test-policy", paths: ["AGENTS.md", "tests/documentation/test-policy-consistency.mjs"], testPaths: ["tests/documentation/test-policy-consistency.mjs"] },
  { key: "ci-tooling", paths: [".github/workflows/**", "tools/ci-*.mjs", "tests/run.mjs"], testPaths: ["tests/tools/ci-domains.test.mjs", "tests/tools/ci-fingerprint.test.mjs"] },
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
