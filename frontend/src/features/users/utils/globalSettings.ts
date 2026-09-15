export interface GlobalField {
  key: string;
  label: string;
  type?: string;
  description?: string;
  placeholder?: string;
  options?: Array<string | { value?: string; label?: string }>;
  required?: boolean;
  readOnly?: boolean;
  maxLength?: number;
  pattern?: string;
}

export interface Contribution {
  pluginName?: string;
  pluginDisplayName?: string;
  id?: string;
  title?: string;
  description?: string;
  fields?: GlobalField[];
  values?: Record<string, unknown>;
}

export type GlobalSecretAction = "keep" | "set" | "clear";

export function globalContributionFieldKey(contribution: Contribution, field: GlobalField): string {
  return `${contribution.pluginName || "plugin"}::${contribution.id || "settings"}::${field.key}`;
}

/** 将全局插件表单转换为宿主保存契约，过滤只读字段并保留未编辑的密钥。 */
export function globalContributionValuesForSave(
  contribution: Contribution,
  secretActions: Record<string, GlobalSecretAction>,
): Record<string, unknown> {
  const values: Record<string, unknown> = {};
  for (const field of contribution.fields || []) {
    const type = String(field.type || "text").toLowerCase();
    if (field.readOnly || type === "status") continue;

    const current = contribution.values?.[field.key];
    if (type === "secret") {
      const action = secretActions[globalContributionFieldKey(contribution, field)] || "keep";
      values[field.key] = action === "set"
        ? { action, value: typeof current === "string" ? current : "" }
        : { action };
    } else if (type === "multi-select") {
      values[field.key] = Array.isArray(current) ? current.map(String) : [];
    } else {
      values[field.key] = current;
    }
  }
  return values;
}

export interface GlobalSettings {
  general: { syncEnabled: boolean; enabled: boolean; runDays: number; maxSuccessfulRunsPerDay: number };
  notification: { syncEnabled: boolean; notifyEnabled: boolean; smtpTo: string };
  advanced: { syncEnabled: boolean; preRunScript: string; preRunOnceOnly: boolean; postRunScript: string; postRunOnFinalOnly: boolean };
}

export const PRE_ONLY_MARKER = "%FIRST%";
export const POST_FINAL_MARKER = "%LAST%";

export function encodePrePost(marker: string, onceOnly: unknown, value: unknown) {
  return `${onceOnly === true ? `${marker} ` : ""}${String(value || "")}`;
}

export function splitPrePost(marker: string, value: unknown) {
  const text = String(value || "").trim();
  return {
    onceOnly: text.startsWith(marker),
    value: text.replace(new RegExp(`^${marker}\\s*`), ""),
  };
}

export function normalizeGlobalSettings(value: any): GlobalSettings {
  const general = value?.general || {};
  const notification = value?.notification || {};
  const advanced = value?.advanced || {};
  return {
    general: {
      syncEnabled: general.syncEnabled === true,
      enabled: general.enabled !== false,
      runDays: typeof general.runDays === "number" ? general.runDays : -1,
      maxSuccessfulRunsPerDay:
        typeof general.maxSuccessfulRunsPerDay === "number" ? general.maxSuccessfulRunsPerDay : -1,
    },
    notification: {
      syncEnabled: notification.syncEnabled === true,
      notifyEnabled: notification.notifyEnabled !== false,
      smtpTo: String(notification.smtpTo || ""),
    },
    advanced: {
      syncEnabled: advanced.syncEnabled === true,
      preRunScript: String(advanced.preRunScript || ""),
      preRunOnceOnly: advanced.preRunOnceOnly === true,
      postRunScript: String(advanced.postRunScript || ""),
      postRunOnFinalOnly: advanced.postRunOnFinalOnly === true,
    },
  };
}
