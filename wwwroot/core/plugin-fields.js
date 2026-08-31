import { esc } from "./format.js";
import { selectControlMarkup } from "./controls.js";

function normalizedOptions(field) {
  return (Array.isArray(field?.options) ? field.options : [])
    .map(option => {
      const value = typeof option === "string" ? option : option?.value;
      const label = typeof option === "string" ? option : option?.label;
      return {
        value: String(value ?? ""),
        label: String(label ?? value ?? ""),
      };
    })
    .filter(option => option.value.length > 0);
}

function selectedValues(value) {
  return new Set(Array.isArray(value) ? value.map(item => String(item ?? "")) : []);
}

/** 声明式插件 multi-select 字段：复用宿主统一的多选选择器和隐藏值载体。 */
export function pluginMultiSelectMarkup(id, field, value) {
  const options = normalizedOptions(field);
  const selected = [...selectedValues(value)];
  const required = field?.required ? ' <span class="req">*</span>' : "";
  const disabled = field?.readOnly ? " disabled" : "";
  const help = field?.description ? ` data-help="${esc(field.description)}"` : "";
  const rootAttributes = ` data-plugin-field="${esc(field?.key || "")}" data-plugin-type="multi-select"${field?.required ? ' aria-required="true"' : ""}`;
  const control = selectControlMarkup(id, selected, options, disabled, field?.label || "", true, rootAttributes);
  return `<div class="field plugin-field"${help}><label class="field-label" for="${esc(id)}-trigger">${esc(field?.label || "")}${required}</label>${control}</div>`;
}

export function selectedPluginMultiSelectValues(element) {
  if (!element) return [];
  const carrier = element.querySelector?.("[data-nxp-select-value]");
  if (carrier?.dataset.nxpSelectMultiple) {
    try {
      const values = JSON.parse(carrier.value || "[]");
      return Array.isArray(values) ? values.map(value => String(value)) : [];
    } catch {
      return [];
    }
  }
  return Array.from(element.querySelectorAll('[data-plugin-multi-option][aria-selected="true"]'))
    .map(option => option.dataset.value || "");
}

function selectedValuesFromCarrier(element) {
  const carrier = element?.matches?.("[data-nxp-select-value]")
    ? element
    : element?.querySelector?.("[data-nxp-select-value]");
  if (!carrier) return [];
  if (!carrier.dataset.nxpSelectMultiple) return carrier.value ? [String(carrier.value)] : [];
  try {
    const values = JSON.parse(carrier.value || "[]");
    return Array.isArray(values) ? values.map(value => String(value)) : [];
  } catch {
    return [];
  }
}

function requiredPluginFieldIsEmpty(field, element, initialValue) {
  const type = String(field?.type || "text").toLowerCase();
  if (type === "switch" || type === "status") return false;
  if (type === "multi-select") return selectedValuesFromCarrier(element).length === 0;
  if (type === "secret" && initialValue?.configured === true) return false;
  return !String(element?.value ?? "").trim();
}

/** 返回声明式插件必填字段状态；调用方负责应用统一的字段标红样式。 */
export function validateRequiredPluginFields(container, fields, initialValues = {}, attribute = "data-plugin-field", onInvalid = () => {}, onValid = () => {}) {
  let valid = true;
  for (const field of Array.isArray(fields) ? fields : []) {
    if (!field?.required || field.readOnly || String(field.type || "").toLowerCase() === "status") continue;
    const key = String(field.key || "");
    if (!key || !container) continue;
    const selector = `[${attribute}="${CSS.escape(key)}"]`;
    const element = container.querySelector(selector);
    if (!element) continue;
    const input = element.matches("[data-nxp-select-value]")
      ? element
      : String(field.type || "").toLowerCase() === "multi-select"
        ? element.querySelector("[data-nxp-select-value]") || element
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

export function syncPluginMultiSelect(element) {
  if (!element) return;
  const selectedInputs = element.querySelectorAll('[data-plugin-multi-option]');
  const selectedLabels = [];
  selectedInputs.forEach(option => {
    const checked = option.getAttribute("aria-selected") === "true";
    option?.setAttribute("aria-selected", checked ? "true" : "false");
    const check = option.querySelector(".plugin-multi-select-check");
    if (check) check.textContent = checked ? "✓" : "";
    if (checked) selectedLabels.push(option.querySelector("span")?.textContent || option.dataset.value || "");
  });
  const summary = selectedLabels.length ? selectedLabels.join("、") : "请选择";
  const summaryElement = element.querySelector(".plugin-multi-select-summary");
  if (summaryElement) {
    summaryElement.textContent = summary;
    summaryElement.title = summary;
  }
}
