/**
 * Local test registry.
 *
 * This file describes what the runner actually executes. It intentionally has
 * no CI job ids, change plans, permissions or result-proof fields.
 */
export const HOST_TEST_AREAS = Object.freeze([
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
  { key: "update", suitePaths: ["tests/system/startup-update-smoke.mjs", "tests/system/update-smoke.mjs"], runtimeNames: ["runtime-startup-update", "runtime-update"] },
]);

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
  assertUnique(GOVERNANCE_DOMAINS, "Governance");
  for (const group of SYSTEM_TEST_GROUPS) {
    if (group.suitePaths.length === 0 || group.suitePaths.length !== group.runtimeNames.length) {
      throw new Error(`System 分组缺少 suite/runtimeName：${group.key}`);
    }
  }
  return true;
}

validateRegistry();
