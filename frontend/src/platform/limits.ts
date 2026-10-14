import { createApp, h, type App } from "vue";
import NxpModal from "../ui/primitives/NxpModal.vue";
import NxpButton from "../ui/primitives/NxpButton.vue";
import { t } from "./i18n";

const DISMISS_KEY = "nexus-limits-dismissed";

let limitWarnings: string[] = [];

/** shell 启动时的约束警告服务；警告文本由 `GET /api/limits` 提供。 */
export function setLimitWarnings(values: unknown): void {
  limitWarnings = Array.isArray(values) ? values.map(value => String(value ?? "")).filter(Boolean) : [];
}

export function limitWarningTexts(): string[] {
  return [...limitWarnings];
}

function warningHash(): string {
  return limitWarnings.join("|");
}

export function shouldShowWarning(): boolean {
  const hash = warningHash();
  if (!hash) return false;
  try {
    return localStorage.getItem(DISMISS_KEY) !== hash;
  } catch {
    return true;
  }
}

let warningApp: App | undefined;

export function showWarning(): void {
  if (!shouldShowWarning() || warningApp) return;
  const root = document.createElement("div");
  root.id = "limits-warning";
  document.body.append(root);
  warningApp = createApp({ render: () => h(NxpModal, {
    open: true, title: t("common.limits_warning_title"), closeLabel: t("common.close"),
    role: "alertdialog", onClose: hideWarning,
  }, {
    default: () => [h("p", t("common.limits_warning_copy")), h("ul", limitWarningTexts().map(item => h("li", item)))],
    footer: () => [h(NxpButton, { onClick: dismissWarningOnce }, () => t("common.acknowledge")),
      h(NxpButton, { variant: "ghost", onClick: dismissWarningForever }, () => t("common.do_not_remind_again"))],
  }) });
  warningApp.mount(root);
}

export function hideWarning(): void {
  warningApp?.unmount();
  warningApp = undefined;
  document.getElementById("limits-warning")?.remove();
}

export function dismissWarningOnce(): void { hideWarning(); }

export function dismissWarningForever(): void {
  try { localStorage.setItem(DISMISS_KEY, warningHash()); } catch { }
  hideWarning();
}
