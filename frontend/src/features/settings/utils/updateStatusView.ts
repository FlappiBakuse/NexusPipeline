/** 更新状态展示：ready 时常驻备份提醒，离开 ready 自动消失。 */

export interface UpdateStatusView {
  state: string;
  readyConfirmKey: string;
  showBackupWarning: boolean;
}

/** 由更新状态推导「状态文案键 + 是否展示备份提醒」。 */
export function updateStatusView(state: unknown): UpdateStatusView {
  const normalized = String(state ?? "idle") || "idle";
  return {
    state: normalized,
    readyConfirmKey: normalized === "ready" ? "settings.update.ready_confirm" : "settings.update.latest",
    showBackupWarning: normalized === "ready",
  };
}
