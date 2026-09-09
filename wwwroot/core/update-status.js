import { esc } from "./format.js";
import { t } from "./i18n.js";

/** 按更新状态机生成当前允许的动作按钮，避免非 Idle 状态重复发起检查。 */
export function updateActionsMarkup(data = {}) {
  const state = data.state || "idle";
  if (state === "idle") {
    let actions = `<button type="button" data-action="update-check" data-testid="update-check">${t("ui.check_for_updates")}</button>`;
    if (data.available) {
      actions += ` <button type="button" class="primary" data-action="update-download" data-testid="update-download">${t("ui.download_update")} v${esc(data.latest)}</button>`;
    }
    return actions;
  }
  if (state === "downloading") {
    return `<button type="button" class="ghost" data-action="update-cancel" data-testid="update-cancel">${t("ui.cancel_download")}</button>`;
  }
  if (state === "ready") {
    return `<button type="button" class="primary" data-action="update-apply" data-testid="update-apply">${t("ui.update_now")}</button> <button type="button" class="ghost" data-action="update-defer" data-testid="update-defer">${t("ui.update_on_next_startup")}</button>`;
  }
  return "";
}
