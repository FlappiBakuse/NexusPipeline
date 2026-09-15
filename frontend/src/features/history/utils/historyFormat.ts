import { formatNumber, getLocale, t } from "../../../platform/i18n";
import type { HistoryRecord } from "./historyTypes";

export function formatDateTime(value?: string) {
  if (!value) return "-";
  const parsed = new Date(value);
  return Number.isNaN(parsed.getTime())
    ? value
    : parsed.toLocaleString(getLocale(), { dateStyle: "medium", timeStyle: "medium" });
}

export function formatDurationMs(value?: number | null) {
  if (value == null || !Number.isFinite(value) || value < 0) return "-";
  const milliseconds = Math.round(value);
  const number = (amount: number) => formatNumber(amount, { maximumFractionDigits: 1 });
  if (milliseconds < 1000) return t("history.duration.ms", { value: number(milliseconds) }, `${number(milliseconds)} ms`);
  const seconds = milliseconds / 1000;
  if (seconds < 60) return t("history.duration.seconds", { value: number(seconds) }, `${number(seconds)} s`);
  const minutes = seconds / 60;
  if (minutes < 60) return t("history.duration.minutes", { value: number(minutes) }, `${number(minutes)} min`);
  return t("history.duration.hours", { value: number(minutes / 60) }, `${number(minutes / 60)} h`);
}

/** 日期键（YYYY-MM-DD）按当前界面语言展示。 */
export function formatHistoryDate(value: string) {
  const parsed = new Date(`${value}T00:00:00`);
  return Number.isNaN(parsed.getTime()) ? value : parsed.toLocaleDateString(getLocale(), { year: "numeric", month: "short", day: "numeric" });
}

/** 用于紧凑趋势标签的日期键展示，不包含年份。 */
export function formatHistoryShortDate(value: string) {
  const parsed = new Date(`${value}T00:00:00`);
  return Number.isNaN(parsed.getTime()) ? value : parsed.toLocaleDateString(getLocale(), { month: "short", day: "numeric" });
}

export function historyTodayValue(now: Date = new Date()) {
  const pad = (value: number) => String(value).padStart(2, "0");
  return `${now.getFullYear()}-${pad(now.getMonth() + 1)}-${pad(now.getDate())}`;
}

export function historyMonthKey(value: string, now: Date = new Date()) {
  const pad = (value: number) => String(value).padStart(2, "0");
  const [year, month] = String(value || "").split("-").map(Number);
  return Number.isFinite(year) && Number.isFinite(month) ? `${year}-${pad(month)}` : `${now.getFullYear()}-${pad(now.getMonth() + 1)}`;
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
