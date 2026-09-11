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

let warningReturnFocus: HTMLElement | null = null;

/** 约束警告层：role=alertdialog + aria-labelledby + 初始焦点 + 焦点陷阱 + Esc + 焦点恢复。 */
export function showWarning(): void {
  if (!shouldShowWarning()) return;
  if (document.getElementById("limits-warning")) return;
  warningReturnFocus = document.activeElement instanceof HTMLElement ? document.activeElement : null;
  const mask = document.createElement("div");
  mask.id = "limits-warning";
  mask.className = "limits-warning";
  mask.setAttribute("role", "alertdialog");
  mask.setAttribute("aria-modal", "true");
  mask.setAttribute("aria-labelledby", "limits-warning-title");
  const card = document.createElement("div");
  card.className = "limits-warning-card";
  const title = document.createElement("h3");
  title.className = "modal-title";
  title.id = "limits-warning-title";
  title.textContent = t("common.limits_warning_title");
  const copy = document.createElement("p");
  copy.className = "limits-warning-copy";
  copy.textContent = t("common.limits_warning_copy");
  const list = document.createElement("ul");
  list.className = "limits-warning-list";
  limitWarningTexts().forEach(item => {    const entry = document.createElement("li");
    entry.textContent = item;
    list.append(entry);
  });
  const footer = document.createElement("div");
  footer.className = "modal-footer";
  const acknowledge = document.createElement("button");
  acknowledge.type = "button";
  acknowledge.dataset.action = "limits-dismiss-once";
  acknowledge.textContent = t("common.acknowledge");
  const dismissForever = document.createElement("button");
  dismissForever.type = "button";
  dismissForever.className = "ghost";
  dismissForever.dataset.action = "limits-dismiss-forever";
  dismissForever.textContent = t("common.do_not_remind_again");
  footer.append(acknowledge, dismissForever);
  card.append(title, copy, list, footer);
  mask.append(card);
  document.body.appendChild(mask);
  // 焦点陷阱（Tab/Shift+Tab 限制在警告层内）与 Esc 关闭。
  mask.addEventListener("keydown", event => {
    if (event.key === "Escape") {
      event.preventDefault();
      hideWarning();
      return;
    }
    if (event.key !== "Tab") return;
    const focusable = Array.from(mask.querySelectorAll<HTMLElement>('button, input, select, textarea, a[href], [tabindex]:not([tabindex="-1"])'))
      .filter(element => !(element as HTMLButtonElement).disabled && element.offsetParent !== null);
    if (!focusable.length) return;
    const first = focusable[0];
    const last = focusable[focusable.length - 1];
    if (event.shiftKey && document.activeElement === first) {
      event.preventDefault();
      last.focus();
    } else if (!event.shiftKey && document.activeElement === last) {
      event.preventDefault();
      first.focus();
    }
  });
  acknowledge.focus();
}

export function hideWarning(): void {
  const mask = document.getElementById("limits-warning");
  if (!mask) return;
  mask.remove();
  if (warningReturnFocus && document.contains(warningReturnFocus)) warningReturnFocus.focus();
  warningReturnFocus = null;
}

export function dismissWarningOnce(): void {
  hideWarning();
}

export function dismissWarningForever(): void {
  try {
    localStorage.setItem(DISMISS_KEY, warningHash());
  } catch {
  }
  hideWarning();
}
