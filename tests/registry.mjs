/**
 * Local test registry.
 *
 * This file describes what the runner actually executes. It intentionally has
 * no CI job ids, change plans, permissions or result-proof fields.
 */
import { globToRegExp } from "../tools/path-glob.mjs";

export const HOST_TEST_AREAS = Object.freeze([
  { key: "core", paths: ["src/Host/**", "src/Shared/**", "src/Platform/**"], testPaths: ["Host/RuntimeContextQueryTests.cs", "Host/RuntimeEntityStateTests.cs", "Host/RuntimeInitializationBoundaryTests.cs", "Host/HostInstanceTests.cs", "Host/HostLifecycleLocalizationTests.cs", "Platform/NativePathPickerServiceTests.cs", "Platform/RuntimeGuardsTests.cs", "Platform/ProcessCleanupResultTests.cs", "Shared/EntityNameRulesTests.cs", "Shared/LocalizationTests.cs", "Shared/NexusVersionTests.cs"] },
  { key: "persistence", paths: ["src/Modules/**/Persistence/**", "src/Modules/Configuration/Snapshots/**"], testPaths: ["Platform/JsonStoreTests.cs", "Platform/RuntimeStateLayoutTests.cs", "Scripts/ScriptPersistenceTests.cs"] },
  { key: "config", paths: ["src/Modules/Configuration/**", "src/Modules/Settings/**", "src/Modules/Users/**", "src/Modules/Scripts/**", "src/Modules/Queues/**"], testPaths: ["Configuration/**/*.cs", "Settings/**/*.cs", "Users/User*.cs", "Users/ScriptBindingCleanupTests.cs", "Queues/**/*.cs", "Scripts/ScriptInstanceTests.cs", "Scripts/ScriptPluginAvailabilityTests.cs", "Scripts/ScriptSpecInputOverrideTests.cs"] },
  { key: "execution", paths: ["src/Modules/Execution/**"], testPaths: ["Execution/**/*.cs", "Platform/ProcessTreeTests.cs", "Platform/JobProcessIdReaderTests.cs", "Platform/OkRuntimeActivityProbeTests.cs", "Platform/WindowPollingTests.cs", "Configuration/ProviderRunJournalTests.cs", "Scripts/ExecutionProviderScriptStorageTests.cs"] },
  { key: "scheduling", paths: ["src/Modules/Scheduling/**"], testPaths: ["Scheduling/**/*.cs", "Users/RunDaysTests.cs"] },
  { key: "judgement", paths: ["src/Modules/Execution/Judgement/**", "src/Modules/Execution/Monitoring/**"], testPaths: ["Execution/Judge*.cs", "Execution/SessionJudgeTests.cs", "Execution/KeywordRuleTests.cs", "Execution/ResultCollectorTests.cs", "Execution/LogPatternTests.cs"] },
  { key: "plugins", paths: ["src/Modules/Plugins/**", "src/NexusPipeline.Plugin.Abstractions/**"], testPaths: ["Plugins/**/*.cs", "Execution/EmulatorPluginSupportTests.cs"] },
  { key: "update", paths: ["src/Modules/Updates/**", "update-policy.json"], testPaths: ["Updates/**/*.cs", "Host/RestartCommandTests.cs"] },
  { key: "control", paths: ["src/ControlPlane/**", "src/Modules/Execution/Realtime/**"], testPaths: ["ControlPlane/**/*.cs", "Execution/RealtimeEventBusTests.cs", "Host/HostRestartCoordinatorTests.cs"] },
  { key: "observability", paths: ["src/Modules/Diagnostics/**", "src/Modules/History/**", "src/Modules/Notifications/**", "src/Modules/Execution/Monitoring/**", "src/Platform/**"], testPaths: ["Diagnostics/**/*.cs", "History/**/*.cs", "Notifications/**/*.cs", "Execution/LogMonitorTests.cs", "Platform/NetworkUtilityTests.cs", "Platform/ProxyConfigurationTests.cs", "Shared/LoggerTests.cs"] },
]);

export const FRONTEND_TEST_GROUPS = Object.freeze([
  { key: "ui", paths: ["frontend/src/ui/**"], testPaths: ["frontend/src/ui/**.test.ts", "frontend/src/ui/**/*.test.ts"] },
  { key: "bridge", paths: ["frontend/src/plugin-bridge/**"], testPaths: ["frontend/src/plugin-bridge/**/*.test.ts"] },
  { key: "platform", paths: ["frontend/src/platform/**", "frontend/src/stores/**", "frontend/src/router.ts"], testPaths: ["frontend/src/platform/**/*.test.ts", "frontend/src/stores/**/*.test.ts", "frontend/src/router*.test.ts"] },
  { key: "features", paths: ["frontend/src/features/**", "frontend/src/app/**"], testPaths: ["frontend/src/features/**/*.test.ts", "frontend/src/app/**/*.test.ts"] },
]);

export const SYSTEM_TEST_GROUPS = Object.freeze([
  { key: "runtime", suitePaths: ["tests/system/runtime-smoke.mjs"], runtimeNames: ["runtime-runtime"] },
  { key: "control", suitePaths: ["tests/system/mcp-smoke.mjs"], runtimeNames: ["runtime-mcp"] },
  { key: "config", suitePaths: ["tests/system/config-smoke.mjs"], runtimeNames: ["runtime-config"] },
  { key: "execution", suitePaths: ["tests/system/execution-resilience.mjs"], runtimeNames: ["runtime-execution-resilience"] },
  { key: "judge", suitePaths: ["tests/system/judge-smoke.mjs"], runtimeNames: ["runtime-judge"] },
  { key: "emulator", suitePaths: ["tests/system/emulator-smoke.mjs"], runtimeNames: ["runtime-emulator"] },
  { key: "plugins", suitePaths: ["tests/system/plugin-smoke.mjs"], runtimeNames: ["runtime-plugins"] },
  { key: "maa", suitePaths: ["tests/system/maa-provider-smoke.mjs"], runtimeNames: ["runtime-maa"] },
  { key: "update", suitePaths: ["tests/system/startup-update-smoke.mjs", "tests/system/update-smoke.mjs"], runtimeNames: ["runtime-startup-update", "runtime-update"] },
]);

/**
 * Real-clock acceptance deliberately selects only scenarios whose assertions
 * depend on elapsed wall time. The accelerated system pass already covers the
 * remaining scenarios, so repeating whole files here would duplicate work.
 */
export const TIMING_TESTS = Object.freeze([
  {
    key: "update",
    suitePath: "tests/system/update-smoke.mjs",
    runtimeName: "runtime-update-timing",
    namePattern: "apply-update|defer 标记",
    caseIds: ["apply-update", "defer 标记"],
  },
  {
    key: "execution",
    suitePath: "tests/system/execution-resilience.mjs",
    runtimeName: "runtime-execution-timing",
    namePattern: "ER07|ER10|ER14|EX01",
    caseIds: ["ER07", "ER10", "ER14 stdout only", "ER14 stderr only", "EX01"],
  },
]);

/** Apply the same file patterns used by runUnit to a concrete candidate list. */
export function selectHostTestFiles(groups, candidates) {
  const patterns = HOST_TEST_AREAS.filter(area => groups.includes(area.key))
    .flatMap(area => area.testPaths)
    .map(pattern => globToRegExp(`tests/NexusPipeline.Tests/${pattern}`));
  return [...new Set(candidates.filter(file => patterns.some(pattern => pattern.test(file))))].sort();
}

/** 同一次 H5 的加速与真实计时段必须使用不同运行目录，避免旧 PID 标记误指新进程。 */
export function systemRuntimeName(baseName, phase) {
  return phase === "update-realtime" || phase === "execution-realtime"
    ? `${baseName}-${phase}`
    : baseName;
}

export const GOVERNANCE_DOMAINS = Object.freeze([
  { key: "docs-links-contracts", paths: ["docs/**", "README.md", "CHANGELOG.md"], testPaths: ["tests/documentation/documentation-consistency.mjs", "tests/tools/docs-index.test.mjs"] },
  { key: "i18n-functional", paths: ["frontend/public/i18n/**", "src/Shared/Localization/**"], testPaths: ["tests/documentation/i18n-*.mjs"] },
  { key: "architecture-boundaries", paths: ["tools/frontend-boundaries.mjs", "tests/tools/**"], testPaths: ["tests/tools/frontend-boundaries.test.mjs", "tests/tools/runtime-policy.test.mjs"] },
  { key: "test-policy", paths: ["AGENTS.md", "tests/documentation/test-policy-consistency.mjs"], testPaths: ["tests/documentation/test-policy-consistency.mjs"] },
  { key: "tooling", paths: [".github/workflows/**", "tests/run.mjs", "tests/registry.mjs", "tools/**"], testPaths: ["tests/tools/*.test.mjs"] },
]);

export const TEST_DOMAIN_REGISTRY = Object.freeze({
  schemaVersion: 1,
  hostAreas: HOST_TEST_AREAS,
  frontendGroups: FRONTEND_TEST_GROUPS,
  systemGroups: SYSTEM_TEST_GROUPS,
  timingTests: TIMING_TESTS,
  governanceDomains: GOVERNANCE_DOMAINS,
});

function assertUnique(collection, label) {
  const keys = collection.map(item => item.key);
  const duplicate = keys.find((key, index) => keys.indexOf(key) !== index);
  if (duplicate) throw new Error(`${label} 存在重复分组：${duplicate}`);
}

export function validateRegistry() {
  assertUnique(HOST_TEST_AREAS, "Host");
  assertUnique(FRONTEND_TEST_GROUPS, "Frontend");
  assertUnique(SYSTEM_TEST_GROUPS, "System");
  assertUnique(TIMING_TESTS, "Timing");
  assertUnique(GOVERNANCE_DOMAINS, "Governance");
  for (const group of SYSTEM_TEST_GROUPS) {
    if (group.suitePaths.length === 0 || group.suitePaths.length !== group.runtimeNames.length) {
      throw new Error(`System 分组缺少 suite/runtimeName：${group.key}`);
    }
  }
  for (const timing of TIMING_TESTS) {
    if (!timing.suitePath || !timing.runtimeName || !timing.namePattern
      || !Array.isArray(timing.caseIds) || timing.caseIds.length === 0
      || new Set(timing.caseIds).size !== timing.caseIds.length) {
      throw new Error(`Timing 分组缺少 suite/runtimeName/namePattern/caseIds：${timing.key}`);
    }
  }
  return true;
}

validateRegistry();
