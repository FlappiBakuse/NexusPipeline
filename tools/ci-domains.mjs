/**
 * CI 影响域清单：门禁分组与各自的触发路径。
 *
 * 这张表是 CI 影响域拆分的唯一来源：`.github/workflows/ci.yml` 的 changes 步骤调用
 * `node tools/ci-changes.mjs` 计算本次改动命中哪些分组，再据此决定各个 Gate 是否执行。
 * 路径使用仓库根目录相对路径，`**` 跨越任意层级，`*` 只匹配单层内的字符。
 */
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
    key: "system",
    gate: "Gate F System Runtime",
    paths: [
      "src/**",
      "tests/system/**",
      "tests/support/**",
      "tests/e2e/tests/fixtures/**",
      "build.cmd",
      "tests/run.mjs",
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
