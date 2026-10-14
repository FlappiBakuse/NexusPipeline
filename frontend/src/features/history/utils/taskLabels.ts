import { getLocale, t } from "../../../platform/i18n";
import { resolveTaskText } from "./taskText";
import type { TaskDiagnostic, TaskDisplaySnapshot, TaskReport } from "./taskTypes";

export function dailyTaskReport(report?: TaskReport): boolean {
  return report?.schemaVersion === 2 && report.semanticsVersion === 'daily-flow-v1';
}

export function providerTaskReport(report?: TaskReport): boolean {
  return report?.schemaVersion === 1 && report.semanticsVersion === 'provider-execution-v1';
}

export function currentTaskReport(report?: TaskReport): boolean {
  return dailyTaskReport(report) || providerTaskReport(report);
}

export function taskStatusLabel(status: string, report?: TaskReport): string {
  if (report && !currentTaskReport(report)) return t('tasks.unsupported_report');
  if (report?.resultBasis === 'framework-task-v1' && providerTaskReport(report)) {
    const labels: Record<string, [string, string]> = { succeeded: ['任务完成（框架结果）', 'Task completed (framework result)'], failed: ['框架任务失败', 'Framework task failed'], pending: ['未执行', 'Not started'] };
    if (labels[status]) return labels[status][getLocale().startsWith('zh') ? 0 : 1];
  }
  return t(`tasks.${dailyTaskReport(report) ? 'daily_status' : 'status'}.${status}`, {}, t(`tasks.status.${status}`));
}

export function taskDiagnosticLabel(item: TaskDiagnostic, snapshot?: TaskDisplaySnapshot): string {
  return resolveTaskText(item.reasonText, snapshot, getLocale(), item.message);
}

/** Third-party diagnostic text remains literal. */
export function taskOutcomeLabel(value?: string, unknownCount?: number, report?: TaskReport, reasonCode?: string): string {
  if (!value) return "-";
  if (!currentTaskReport(report)) return value;
  const source = reasonCode?.startsWith("tasks.") ? reasonCode : value;
  const code = source.replace(/^tasks\./, "");
  const known: Record<string, string> = { incomplete: "incomplete", all_satisfied: "satisfied", partial_failure: "partial", lifecycle_failed: "failed", all_failed: "failed", no_tasks: "empty" };
  if ((code === "incomplete" || code === "tasks_unverified") && typeof unknownCount === "number" && unknownCount > 0)
    return t("tasks.outcome.unverified_count", { count: unknownCount });
  if (source.startsWith("tasks.")) return known[code] ? t(`tasks.outcome.${known[code]}`) : t(`tasks.reason.${source}`, {}, value);
  if (dailyTaskReport(report)) return t(`tasks.daily_status.${code}`, {}, value);
  return known[code] ? t(`tasks.outcome.${known[code]}`) : value;
}
