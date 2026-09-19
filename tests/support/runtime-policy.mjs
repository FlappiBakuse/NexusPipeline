/**
 * Pure runtime choice for release gates.
 * Permission is observed for diagnostics only; it never selects or skips a suite.
 */
export const RELEASE_GROUPS = Object.freeze([
  "core",
  "frontend-contract",
  "ui-runtime",
  "execution-emulator",
  "update-acceptance",
]);

const PHASES = Object.freeze({
  "ui-runtime": ["default"],
  "execution-emulator": ["default"],
  "update-acceptance": ["accelerated", "update-realtime", "execution-realtime"],
  dev: ["default", "realtime"],
});

const REMOTE_CREDENTIALS = /^(?:GH_TOKEN|GITHUB_TOKEN|QUALIFICATION_.*(?:TOKEN|KEY)|PUBLISHER_.*(?:TOKEN|KEY))$/i;
const CONTROLLED_KEYS = new Set([
  "NEXUS_TEST_MODE",
  "NEXUS_TEST_HOST",
  "NEXUS_TEST_HOST_DIR",
  "NEXUS_TEST_HOST_EXIT_FILE",
  "NEXUS_SYSTEM_SMOKE",
  "NEXUS_SYSTEM_ACTION_DRYRUN",
  "NEXUS_TIME_SCALE",
  "NEXUS_TEST_RUN_ID",
]);

export class RuntimePolicyError extends Error {
  constructor(code, message) {
    super(message);
    this.name = "RuntimePolicyError";
    this.code = code;
  }
}

export function runtimePolicy({
  group,
  phase = "default",
  platform = process.platform,
  integrity = "Unknown",
  env = {},
  testHostDir,
  exitFile,
  runId,
} = {}) {
  if (!Object.hasOwn(PHASES, group) || !PHASES[group].includes(phase)) {
    throw new RuntimePolicyError("INVALID_SCOPE", `Invalid runtime scope: ${group}/${phase}`);
  }
  if (platform !== "win32") {
    throw new RuntimePolicyError("PLATFORM_UNAVAILABLE", "Windows runtime tests require Windows; no tests were executed.");
  }
  for (const [key, value] of Object.entries({ testHostDir, exitFile, runId })) {
    if (typeof value !== "string" || !value.trim() || value.includes("\0")) {
      throw new RuntimePolicyError("INVALID_CONFIGURATION", `Missing/invalid ${key}`);
    }
  }

  const scale = phase.includes("realtime") ? 1 : 10;
  const childEnv = Object.fromEntries(Object.entries(env)
    .filter(([key]) => !key.toUpperCase().startsWith("NEXUS_CI_"))
    .filter(([key]) => !CONTROLLED_KEYS.has(key.toUpperCase()))
    .filter(([key]) => !REMOTE_CREDENTIALS.test(key)));
  Object.assign(childEnv, {
    NEXUS_TEST_MODE: "test-host",
    NEXUS_TEST_HOST: "1",
    NEXUS_TEST_HOST_DIR: testHostDir,
    NEXUS_TEST_HOST_EXIT_FILE: exitFile,
    NEXUS_SYSTEM_SMOKE: "1",
    NEXUS_SYSTEM_ACTION_DRYRUN: "1",
    NEXUS_TIME_SCALE: String(scale),
    NEXUS_TEST_RUN_ID: runId,
  });
  return Object.freeze({
    artifactKind: "test-host",
    requestElevation: false,
    observedIntegrity: integrity,
    group,
    phase,
    timeScale: scale,
    env: Object.freeze(childEnv),
  });
}

export function gateSequence(group) {
  if (group === "all") return [...RELEASE_GROUPS];
  if (RELEASE_GROUPS.includes(group)) return [group];
  throw new RuntimePolicyError("INVALID_SCOPE", `Unknown release group: ${group}`);
}
