import { getLocale, t } from "../../../platform/i18n";
import type { HistoryRecord } from "./historyTypes";

export function formatDateTime(value?: string) {
  if (!value) return "-";
  const parsed = new Date(value);
  return Number.isNaN(parsed.getTime())
    ? value
    : parsed.toLocaleString(getLocale(), { dateStyle: "medium", timeStyle: "medium" });
}

export function statusTone(value?: string): "ok" | "warn" | "bad" | "blue" | "muted" {
  if (value === "success") return "ok";
  if (value === "partial" || value === "cancelled" || value === "skipped") return "warn";
  if (value === "running") return "blue";
  if (value === "failed") return "bad";
  return "muted";
}

export function statusLabel(value?: string) {
  if (value === "success") return `✓ ${t("common.complete")}`;
  if (value === "partial") return `⚠ ${t("history.partially_complete")}`;
  if (value === "cancelled") return t("common.cancelled");
  if (value === "skipped") return t("common.skipped");
  if (value === "failed") return `✕ ${t("common.failed")}`;
  return value || t("common.unknown");
}

export function badgeTone(value?: string): "ok" | "warn" | "bad" | "blue" | "muted" {
  return ["ok", "warn", "bad", "blue"].includes(String(value || ""))
    ? (value as "ok" | "warn" | "bad" | "blue")
    : "muted";
}

export function historyBadges(record: HistoryRecord) {
  return (record.pluginHistory || [])
    .flatMap((item, itemIndex) =>
      (item.badges || []).map((badge, badgeIndex) => ({
        key: `${item.id || item.pluginName || item.title || itemIndex}-${badge.label || badgeIndex}`,
        label: badge.label || "",
        tone: badgeTone(badge.tone),
        title: badge.title || item.pluginDisplayName || item.pluginName || "",
      })),
    )
    .filter((item) => item.label);
}
