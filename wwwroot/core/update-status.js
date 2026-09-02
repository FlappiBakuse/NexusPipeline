import { esc } from "./format.js";

/** 首次进入前端时按自动检查开关决定是否展示后台启动检查结果。 */
export function initialUpdateStatus(data = {}, autoCheckEnabled = false) {
  if (autoCheckEnabled) return data;
  const state = data.state || "idle";
  if (state !== "idle" && state !== "checking") return data;
  return {
    ...data,
    state: "idle",
    available: false,
    latest: null,
    prerelease: false,
    notes: "",
    error: "",
  };
}

/** 按更新状态机生成当前允许的动作按钮，避免非 Idle 状态重复发起检查。 */
export function updateActionsMarkup(data = {}) {
  const state = data.state || "idle";
  if (state === "idle") {
    let actions = '<button type="button" data-action="update-check" data-testid="update-check">检查更新</button>';
    if (data.available) {
      actions += ` <button type="button" class="primary" data-action="update-download" data-testid="update-download">下载更新 v${esc(data.latest)}</button>`;
    }
    return actions;
  }
  if (state === "downloading") {
    return '<button type="button" class="ghost" data-action="update-cancel" data-testid="update-cancel">取消下载</button>';
  }
  if (state === "ready") {
    return '<button type="button" class="primary" data-action="update-apply" data-testid="update-apply">立即更新</button> <button type="button" class="ghost" data-action="update-defer" data-testid="update-defer">下次启动更新</button>';
  }
  return "";
}
