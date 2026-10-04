import { getLocale, t } from "../../../platform/i18n";
import { resolveTaskText } from "./taskText";
import type { TaskDiagnostic, TaskDisplaySnapshot, TaskReport } from "./taskTypes";

export function dailyTaskReport(report?: TaskReport): boolean {
  return report?.schemaVersion === 2 && report.semanticsVersion === 'daily-flow-v1';
}

export function taskStatusLabel(status: string, report?: TaskReport): string {
  return t(`tasks.${dailyTaskReport(report) ? 'daily_status' : 'status'}.${status}`, {}, t(`tasks.status.${status}`));
}

// Older frozen plans contain these official messages without localization references.
const diagnosticKeys = new Map<string, string>([
  ["A one-shot start cursor narrows this run; automatic retry is disabled.", "tasks.diagnostic.start_cursor"],
  ["Class aliases, resume cursors and nested Task.run calls require verified identity. Ambiguous precondition skips are unknown, not success.", "tasks.diagnostic.task_identity"],
  ["Configuration identity or schema could not be verified.", "tasks.diagnostic.config_identity"],
  ["Daily 4C requires Weekly Challenge or Boss Challenge teleport and a positive finite integer Repeat Farm Count. No settings were changed.", "tasks.diagnostic.farm_4c"],
  ["DailyTask has implicit steps without individual switches. Restarting it may repeat resource-consuming work; automatic retry is not qualified.", "tasks.diagnostic.retry_coupled"],
  ["Discovery includes upstream defaults. Selective retry requires every known item exactly once with an explicit boolean selection.", "tasks.diagnostic.explicit_selection"],
  ["Item results require an active daily scope and paired item logs. Launcher completion, framework completion and summary text are not item success.", "tasks.diagnostic.item_evidence"],
  ["MXU task callbacks cover outer tasks only. Duplicate labels, unsupported resources and silent inner branches remain unknown.", "tasks.diagnostic.mxu_coverage"],
  ["Official new release: only the base lifecycle is qualified. Task interpretation and selective retry are disabled.", "tasks.diagnostic.runtime_restricted"],
  ["Only exact registered outer application names are mapped. Group completion and nested operation failures do not imply application outcomes.", "tasks.diagnostic.application_coverage"],
  ["Outer completion does not prove custom scripts, resource consumption or nested actions succeeded.", "tasks.diagnostic.outer_completion"],
  ["Read-only projection of one_dragon_app.yml migration. Upstream creates _group.yml at launch; legacy selection is not patched for automatic retry.", "tasks.diagnostic.legacy_selection"],
  ["Reward wrapper completion and timestamps are not success evidence. Internal randomized daily tasks do not expand the frozen plan.", "tasks.diagnostic.reward_evidence"],
  ["Select a valid automatic-run instance in the bound user configuration. The active tab is not an automatic-run target.", "tasks.diagnostic.automatic_instance"],
  ["Stamina and nightmare execution depend on current daily progress. Click-only reward paths have no verified positive terminal evidence.", "tasks.diagnostic.conditional_steps"],
  ["The official runtime identity could not be established. See the channel/version-specific reason.", "tasks.diagnostic.runtime_identity"],
]);

export function taskDiagnosticLabel(item: TaskDiagnostic, snapshot?: TaskDisplaySnapshot): string {
  const message = resolveTaskText(item.reasonText, snapshot, getLocale(), item.message);
  const key = diagnosticKeys.get(message);
  if (key) return t(key, {}, message);
  const prefixes = [
    ["Unrecognized daily extra: ", "tasks.diagnostic.unknown_extra"],
    ["Unrecognized routine item: ", "tasks.diagnostic.unknown_routine"],
  ] as const;
  for (const [prefix, translation] of prefixes)
    if (message.startsWith(prefix)) return t(translation, { name: message.slice(prefix.length) }, message);
  return message;
}


/** Known task protocol outcomes only; preserve third-party and legacy diagnostic text. */
export function taskOutcomeLabel(value?: string, unknownCount?: number): string {
  if (!value) return "-";
  const code = value.replace(/^tasks\./, "");
  const known: Record<string, string> = { incomplete: "incomplete", tasks_unverified: "incomplete", all_satisfied: "satisfied", partial_failure: "partial", lifecycle_failed: "failed", all_failed: "failed", no_tasks: "empty", no_execution_required: "satisfied" };
  if ((code === "incomplete" || code === "tasks_unverified") && typeof unknownCount === "number" && unknownCount > 0)
    return t("tasks.outcome.unverified_count", { count: unknownCount });
  return known[code] ? t(`tasks.outcome.${known[code]}`) : value;
}
