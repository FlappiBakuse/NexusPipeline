import type { PluginFieldControl } from "./controls";
import type { PluginField } from "./types";

/** 必填字段是否仍为空值；switch 与 status 不参与必填校验。 */
function requiredFieldIsEmpty(field: PluginField, control: PluginFieldControl, initialValue: unknown): boolean {
  const type = String(field?.type || "text").toLowerCase();
  if (type === "switch" || type === "status") return false;
  if (type === "secret") {
    // 已配置的密钥保持原值时视为已填写，只有显式清空后才要求重新输入。
    if ((initialValue as { configured?: boolean } | undefined)?.configured === true) return false;
    const value = control.value() as { action?: string } | null;
    return value?.action !== "set";
  }
  if (type === "multi-select") {
    const value = control.value();
    return !Array.isArray(value) || value.length === 0;
  }
  return !String(control.value() ?? "").trim();
}

/**
 * 校验声明式插件表单的必填字段；调用方负责应用统一的字段标红样式。
 * 取值来自桥接层的控件对象，不读取控件内部 DOM。
 */
export function validateRequiredPluginFields(
  controls: Map<string, PluginFieldControl>,
  fields: PluginField[],
  initialValues: Record<string, unknown> = {},
  onInvalid: (control: PluginFieldControl) => void = () => {},
  onValid: (control: PluginFieldControl) => void = () => {},
): boolean {
  let valid = true;
  for (const field of Array.isArray(fields) ? fields : []) {
    if (!field?.required || field.readOnly) continue;
    if (String(field.type || "").toLowerCase() === "status") continue;
    const key = String(field.key || "");
    const control = key ? controls.get(key) : null;
    if (!control) continue;
    if (requiredFieldIsEmpty(field, control, initialValues?.[key])) {
      onInvalid(control);
      valid = false;
    } else {
      onValid(control);
    }
  }
  return valid;
}
