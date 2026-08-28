import { esc } from "./format.js";
import { icon } from "./icons.js";

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

function summaryText(options, selected) {
  const labels = options
    .filter(option => selected.has(option.value))
    .map(option => option.label);
  return labels.length ? labels.join("、") : "请选择";
}

/** 声明式插件 multi-select 字段：视觉上是下拉菜单，选项使用复选框保持多选语义。 */
export function pluginMultiSelectMarkup(id, field, value) {
  const options = normalizedOptions(field);
  const selected = selectedValues(value);
  const triggerId = `${id}-trigger`;
  const menuId = `${id}-menu`;
  const summary = summaryText(options, selected);
  const required = field?.required ? ' <span class="req">*</span>' : "";
  const disabled = field?.readOnly ? " disabled" : "";
  const requiredAttribute = field?.required ? ' aria-required="true"' : "";
  const optionMarkup = options.map(option => {
    const checked = selected.has(option.value);
    return `<label class="plugin-multi-select-option" role="option" aria-selected="${checked ? "true" : "false"}"><input type="checkbox" value="${esc(option.value)}" data-action="sync-plugin-multi-select-option" data-plugin-multi-option="true"${checked ? " checked" : ""}${disabled}><span>${esc(option.label)}</span></label>`;
  }).join("");
  const description = field?.description
    ? `<span class="muted plugin-field-description">${esc(field.description)}</span>`
    : "";
  return `<div class="field plugin-field"><label class="field-label" for="${esc(triggerId)}">${esc(field?.label || "")}${required}</label><div class="plugin-multi-select" data-plugin-field="${esc(field?.key || "")}" data-plugin-type="multi-select"><button id="${esc(triggerId)}" class="plugin-multi-select-trigger" type="button" data-action="toggle-plugin-multi-select" data-plugin-field="${esc(field?.key || "")}" aria-haspopup="listbox" aria-expanded="false" aria-controls="${esc(menuId)}" aria-label="${esc(field?.label || "")}"${requiredAttribute}${disabled}><span class="plugin-multi-select-summary" title="${esc(summary)}">${esc(summary)}</span>${icon("chevronDown", "icon plugin-multi-select-arrow")}</button><div id="${esc(menuId)}" class="plugin-multi-select-menu" role="listbox" aria-label="${esc(field?.label || "")}" aria-multiselectable="true" hidden>${optionMarkup}</div></div>${description}</div>`;
}

export function selectedPluginMultiSelectValues(element) {
  if (!element) return [];
  return Array.from(element.querySelectorAll('input[data-plugin-multi-option]:checked'))
    .map(input => input.value);
}

export function syncPluginMultiSelect(element) {
  if (!element) return;
  const selectedInputs = element.querySelectorAll('input[data-plugin-multi-option]');
  const selectedLabels = [];
  selectedInputs.forEach(input => {
    const option = input.closest(".plugin-multi-select-option");
    const checked = input.checked === true;
    option?.setAttribute("aria-selected", checked ? "true" : "false");
    if (checked) {
      selectedLabels.push(option?.querySelector("span")?.textContent || input.value);
    }
  });
  const summary = selectedLabels.length ? selectedLabels.join("、") : "请选择";
  const summaryElement = element.querySelector(".plugin-multi-select-summary");
  if (summaryElement) {
    summaryElement.textContent = summary;
    summaryElement.title = summary;
  }
}
