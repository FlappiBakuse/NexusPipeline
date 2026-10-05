export interface SpecializedCapabilities {
  supportsEmulator: boolean;
  selfManagedPcLaunch: boolean;
  allowFreshConfig: boolean;
}

const EMULATOR = "emulator";
const SELF_MANAGED_PC_LAUNCH = "self-managed-pc-launch";
const NO_FRESH_CONFIG = "no-fresh-config";

export function projectSpecializedCapabilities(
  capabilities: unknown,
): SpecializedCapabilities {
  if (!Array.isArray(capabilities) || capabilities.some(value =>
    typeof value !== "string" || ![EMULATOR, SELF_MANAGED_PC_LAUNCH, NO_FRESH_CONFIG].includes(value))) {
    return {
      supportsEmulator: false,
      selfManagedPcLaunch: false,
      allowFreshConfig: false,
    };
  }
  const keys = new Set(capabilities);
  return {
    supportsEmulator: keys.has(EMULATOR),
    selfManagedPcLaunch: keys.has(SELF_MANAGED_PC_LAUNCH),
    allowFreshConfig: !keys.has(NO_FRESH_CONFIG),
  };
}
