import { t } from "./i18n";

export function esc(value: unknown): string {
  return String(value ?? "").replace(/[&<>"']/g, char => ({
    "&": "&amp;", "<": "&lt;", ">": "&gt;", '"': "&quot;", "'": "&#39;",
  }[char] as string));
}

/**
 * 将持久化的稳定结果码投影为当前浏览器语言的说明。
 * ResultDetail 仍作为旧历史记录和未知结果码的回退，参数只使用稳定值。
 */
export function resultDetail(record: Record<string, any>): string {
  const fallback = record?.resultDetail || "-";
  const args = record?.resultArgs || {};
  const reason = args.reason || record?.resultDetail || "-";
  switch (record?.resultCode) {
    case "run.running":
      return t("common.running");
    case "run.success":
      return fallback;
    case "run.partial":
      return fallback;
    case "run.cancelled":
      return fallback === "-" ? t("common.run_cancelled") : fallback;
    case "run.daily_cap":
      return t("common.status.daily_limit_skipped", args);
    case "run.user_unavailable":
      return t("common.error.user_unavailable", args);
    case "run.spec_failed":
      return fallback;
    case "run.config_selection_required":
      return t("common.configuration.multiple_select");
    case "run.user_config_load_failed":
      return t("common.error.user_config_load", { reason });
    case "run.retry_prepare_failed":
      return t("common.error.config_swap_failed", { reason });
    case "run.max_attempts":
      return t("common.error.attempts_exhausted", {
        maximum: args.maximum || record?.maxAttempts || "-",
        reason,
      });
    case "run.script_missing":
      return t("common.error.script_instance_deleted");
    case "run.no_enabled_users":
      return t("common.status.no_enabled_users");
    case "run.plugin_unavailable":
      return t("common.status.bound_skipped", { reason });
    case "run.host_error":
      return fallback;
    case "scheduler.trigger_failed":
      return fallback;
    default:
      return fallback;
  }
}
