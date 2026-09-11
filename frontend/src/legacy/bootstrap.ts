import { initParticles } from "@legacy/effects/particles.js";
import { initAppearance } from "@legacy/core/appearance.js";
import { loadLimits, showWarning } from "@legacy/core/limits.js";
import { initPluginRuntime } from "@legacy/core/plugin-runtime.js";
import { initTheme } from "@legacy/core/ui.js";
import { getLocale, loadLocale, t } from "@legacy/core/i18n.js";
import { ensureAccessToken } from "./auth";

async function updateLocalAddress() {
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

export async function bootstrapFrontend() {
  await loadLocale();
  initTheme();
  if (!(await ensureAccessToken())) return false;
  await initAppearance();
  updateLocalAddress();
  initParticles();
  await loadLimits();
  showWarning();
  await initPluginRuntime();
  return true;
}
