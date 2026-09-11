import { api } from "../platform/api";
import { getLocale, loadLocale, t } from "../platform/i18n";
import { state } from "../platform/page-state";
import { initTheme } from "../platform/shell";
import { initTooltips } from "../platform/tooltip";
import { initAppearance } from "../platform/appearance";
import { setLimitWarnings, showWarning } from "../platform/limits";
import { initParticles } from "../platform/particles";
import { ensureAccessToken, installReauthEntry } from "../platform/auth";
import { initPluginRuntime } from "@bridge/index";

async function updateLocalAddress(): Promise<void> {
  const element = document.querySelector<HTMLElement>("#local-addr");
  if (!element) return;
  try {
    const response = await fetch("/api/status", { cache: "no-store", headers: { "X-Nexus-Locale": getLocale() } });
    const data = await response.json();
    const port = data.actualPort || data.webPort || "";
    element.textContent = port
      ? t("service.with_host", { host: location.hostname, port })
      : t("service.label");
  } catch {
    element.textContent = t("service.label");
  }
}

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
  void updateLocalAddress();
  initParticles();
  await loadLimits();
  showWarning();
  await initPluginRuntime();
  return true;
}
