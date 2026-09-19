/** Host 已验证的 data-specialized capability typed 投影。
 *
 * 专项插件只提供声明数据；浏览器行为仍由 Host feature 实现。非法 capability
 * 应在后端被拒绝，这里只对缺省/兼容输入提供安全的 false/true 默认值。
 */
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
  legacyProjection?: {
    supportsEmulator?: boolean;
    selfManagedPcLaunch?: boolean;
    noFreshConfig?: boolean;
  },
): SpecializedCapabilities {
  if (!Array.isArray(capabilities)) {
    return {
      supportsEmulator: legacyProjection?.supportsEmulator === true,
      selfManagedPcLaunch: legacyProjection?.selfManagedPcLaunch === true,
      allowFreshConfig: legacyProjection?.noFreshConfig !== true,
    };
  }
  const keys = new Set(capabilities.map(value => String(value || "").trim().toLowerCase()));
  return {
    supportsEmulator: keys.has(EMULATOR),
    selfManagedPcLaunch: keys.has(SELF_MANAGED_PC_LAUNCH),
    allowFreshConfig: !keys.has(NO_FRESH_CONFIG),
  };
}
