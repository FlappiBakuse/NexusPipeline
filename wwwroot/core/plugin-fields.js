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
  const description = field?.description
    ? `<span class="muted plugin-field-description">${esc(field.description)}</span>`
    : "";
  const rootAttributes = ` data-plugin-field="${esc(field?.key || "")}" data-plugin-type="multi-select"${field?.required ? ' aria-required="true"' : ""}`;
  const control = selectControlMarkup(id, selected, options, disabled, field?.label || "", true, rootAttributes);
  return `<div class="field plugin-field"><label class="field-label" for="${esc(id)}-trigger">${esc(field?.label || "")}${required}</label>${control}${description}</div>`;
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
