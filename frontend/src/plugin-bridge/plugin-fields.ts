import type { PluginField } from "./types";

function selectedValuesFromCarrier(element: Element | null): string[] {
  const carrier = element?.matches?.("[data-nxp-select-value]")
    ? (element as HTMLInputElement)
    : element?.querySelector?.<HTMLInputElement>("[data-nxp-select-value]");
  if (!carrier) return [];
  if (!carrier.dataset.nxpSelectMultiple) return carrier.value ? [String(carrier.value)] : [];
  try {
    const values = JSON.parse(carrier.value || "[]");
    return Array.isArray(values) ? values.map(value => String(value)) : [];
  } catch {
    return [];
  }
}

function requiredPluginFieldIsEmpty(field: PluginField, element: Element | null, initialValue: unknown): boolean {
  const type = String(field?.type || "text").toLowerCase();
  if (type === "switch" || type === "status") return false;
  if (type === "multi-select") return selectedValuesFromCarrier(element).length === 0;
  if (type === "secret" && (initialValue as { configured?: boolean } | undefined)?.configured === true) return false;
  return !String((element as HTMLInputElement | null)?.value ?? "").trim();
}

/** 返回声明式插件必填字段状态；调用方负责应用统一的字段标红样式。 */
export function validateRequiredPluginFields(
  container: Element | null,
  fields: PluginField[],
  initialValues: Record<string, unknown> = {},
  attribute = "data-plugin-field",
  onInvalid: (input: HTMLElement) => void = () => {},
  onValid: (input: HTMLElement) => void = () => {},
): boolean {
  let valid = true;
  for (const field of Array.isArray(fields) ? fields : []) {
    if (!field?.required || field.readOnly || String(field.type || "").toLowerCase() === "status") continue;
    const key = String(field.key || "");
    if (!key || !container) continue;
    const selector = `[${attribute}="${CSS.escape(key)}"]`;
    const element = container.querySelector<HTMLElement>(selector);
    if (!element) continue;
    const input = element.matches("[data-nxp-select-value]")
      ? element
      : String(field.type || "").toLowerCase() === "multi-select"
        ? element.querySelector<HTMLElement>("[data-nxp-select-value]") || element
        : element;
    if (!input?.id) continue;
    if (requiredPluginFieldIsEmpty(field, element, initialValues?.[key])) {
      onInvalid(input);
      valid = false;
    } else {
      onValid(input);
    }
  }
  return valid;
}
