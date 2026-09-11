import { renderIcon } from "./icons";
import { t } from "./i18n";
import { toast } from "./toast";
import { applyThemeValue, cycleThemeValue, initThemeValue, type ThemeValue } from "./appearance";

export function setTopbarTitle(title: string): void {
  const el = document.getElementById("topbar-title");
  if (el) el.textContent = title;
}

export function setNavOpen(open: boolean): void {
  document.body.classList.toggle("nav-open", open);
  const sidebar = document.getElementById("sidebar");
  if (sidebar) sidebar.setAttribute("aria-hidden", String(!open && window.innerWidth <= 820));
  document.querySelectorAll<HTMLElement>('[data-action="open-nav"]').forEach(button => {
    button.setAttribute("aria-expanded", String(open));
    button.setAttribute("aria-controls", "sidebar");
  });
}

function themeLabel(value: ThemeValue): string {
  return t(value === "system" ? "common.follow_system" : value === "light" ? "common.light" : "common.dark");
}

function syncThemeControls(value: ThemeValue): void {
  const iconName = value === "light" ? "sun" : value === "dark" ? "moon" : "system";
  document.querySelectorAll<HTMLElement>("[data-theme-icon], #theme-icon").forEach(element => {
    element.innerHTML = renderIcon(iconName);
  });
  document.querySelectorAll<HTMLElement>('[data-action="toggle-theme"]').forEach(toggle => {
    toggle.setAttribute("aria-label", t("common.theme.switch_hint", { theme: themeLabel(value) }));
  });
}

export function initTheme(): void {
  syncThemeControls(initThemeValue());
}

export function applyTheme(theme: string): void {
  syncThemeControls(applyThemeValue(theme));
}

export function cycleTheme(): void {
  const value = cycleThemeValue();
  syncThemeControls(value);
  toast(t("common.theme_value", { theme: themeLabel(value) }));
}
