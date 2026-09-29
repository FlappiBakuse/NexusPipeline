import { api } from "../platform/api";
import { loadLocale } from "../platform/i18n";
import { state } from "../platform/page-state";
import { initTheme } from "../platform/shell";
import { initTooltips } from "../platform/tooltip";
import { initAppearance } from "../platform/appearance";
import { setLimitWarnings, showWarning } from "../platform/limits";
import { initParticles } from "../platform/particles";
import { ensureAccessToken, installReauthEntry } from "../platform/auth";
import { initPluginRuntime } from "@bridge/index";

async function loadLimits(): Promise<void> {
  try {
    const data = await api<{ limits?: unknown; warnings?: unknown }>("GET", "/api/limits");
    state.limits = data.limits;
    setLimitWarnings(data.warnings);
  } catch {
    setLimitWarnings([]);
  }
}

/** 宿主 shell 启动编排：只组合平台服务与插件桥接 facade，不依赖桥接内部实现。 */
export async function bootstrapFrontend(): Promise<boolean> {
  await loadLocale();
  initTheme();
  installReauthEntry();
  if (!(await ensureAccessToken())) return false;
  initTooltips();
  await initAppearance();
  initParticles();
  await loadLimits();
  showWarning();
  await initPluginRuntime();
  return true;
}
