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
    paths: [
      "frontend/src/**",
      "frontend/index.html",
      "frontend/package.json",
      "frontend/package-lock.json",
      "frontend/vite.config.ts",
      "frontend/vitest.config.ts",
      "frontend/tsconfig.json",
    ],
  },
  {
    key: "host",
    gate: "Gate B 宿主 Unit + Component",
    paths: [
      "src/**",
      "tests/NexusPipeline.Tests/**",
      "tests/fixtures/**",
      "*.csproj",
      "global.json",
      "build.cmd",
      "tests/run.mjs",
    ],
  },
  {
    key: "docs",
    gate: "Gate C 文档 + i18n",
    paths: [
      "docs/**",
      "README.md",
      "CHANGELOG.md",
      "frontend/public/i18n/**",
      "tests/documentation/**",
    ],
  },
  {
    key: "plugin",
    gate: "Gate D 插件契约",
    paths: [
      "src/NexusPipeline.Plugin.Abstractions/**",
      "src/Plugins/**",
      "frontend/src/plugin-bridge/**",
      "frontend/src/ui/**",
      "docs/PLUGIN_API.md",
      "tests/NexusPipeline.Tests/PluginExtensionContractTests.cs",
      "tests/NexusPipeline.Tests/PluginCapabilityTests.cs",
      "tests/NexusPipeline.Tests/PluginContributionRouteTests.cs",
      "tests/NexusPipeline.Tests/PluginAvailabilityPolicyTests.cs",
      "tests/NexusPipeline.Tests/PluginHostServicesTests.cs",
      "tests/NexusPipeline.Tests/PluginManagementParityTests.cs",
      "tests/NexusPipeline.Tests/PluginManagerTests.cs",
      "tests/NexusPipeline.Tests/PluginRepositoryTests.cs",
      "tests/NexusPipeline.Tests/PluginConfigValidatorTests.cs",
    ],
  },
  {
    key: "ui",
    gate: "Gate E 管理员 UI Smoke",
    paths: [
      "frontend/src/features/**",
      "frontend/src/app/**",
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
      "src/Services/SmtpSender.cs",
      "src/Services/TaskRegistration.cs",
      "src/Services/UserConfigManager.cs",
      "src/Services/WebhookSender.cs",
      "tests/system/mcp-smoke.mjs",
      "tests/system/runtime-smoke.mjs",
    ],
  },
  {
    key: "system_execution",
    gate: "Gate F System Execution",
    paths: [
      ...SYSTEM_SHARED_PATHS,
      "src/Services/DispatchCenter.cs",
      "src/Services/Execution/**",
      "src/Services/History/**",
      "src/Services/Judgement/**",
      "src/Services/LogMonitor.cs",
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
    paths: [
      ...SYSTEM_SHARED_PATHS,
      "src/Services/Update/**",
      "tests/system/startup-update-smoke.mjs",
      "tests/system/update-smoke.mjs",
    ],
  },
];

/**
 * 共享触发路径：命中时全部 Gate 都视为受影响。
 * CI 工作流与影响域清单本身的改动会改变所有 Gate 的判定，因此不做分组过滤。
 */
export const CI_SHARED_PATHS = [
  ".github/workflows/**",
  "tools/ci-changes.mjs",
  "tools/ci-domains.mjs",
  "tools/source-hash.mjs",
];
