import { t } from "../../../platform/i18n";

/** Known task protocol outcomes only; preserve third-party and legacy diagnostic text. */
export function taskOutcomeLabel(value?: string): string {
  if (!value) return "-";
  const code = value.replace(/^tasks\./, "");
  const known: Record<string, string> = { incomplete: "incomplete", tasks_unverified: "incomplete", all_satisfied: "satisfied", partial_failure: "partial", lifecycle_failed: "failed", all_failed: "failed", no_tasks: "empty", no_execution_required: "satisfied" };
  return known[code] ? t(`tasks.outcome.${known[code]}`) : value;
}
