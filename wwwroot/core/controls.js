import { esc } from "./format.js";
import { icon } from "./icons.js";

function normalizedOptions(options) {
  return (Array.isArray(options) ? options : []).map(option => {
    const value = typeof option === "string" ? option : option?.value;
    const label = typeof option === "string" ? option : option?.label;
    return {
      value: String(value ?? ""),
      label: String(label ?? value ?? ""),
      disabled: typeof option === "object" && option?.disabled === true,
      title: typeof option === "object" ? String(option?.title || "") : "",
    };
  });
}

function safeId(value) {
  return String(value || "control").replace(/[^a-zA-Z0-9_-]/g, "-");
}

function hasDisabledAttribute(extra) {
  return /(?:^|\s)disabled(?:\s|=|$)/u.test(String(extra || ""));
}

function selectedValues(value) {
  if (Array.isArray(value)) return value.map(item => String(item ?? ""));
  return String(value ?? "").length ? [String(value)] : [];
}

/** 返回带键盘/ARIA 支持的自定义下拉控件；隐藏 input 保留现有表单读取接口。 */
export function selectControlMarkup(id, value, options, extra = "", ariaLabel = "", multiple = false, rootExtra = "") {
  const normalized = normalizedOptions(options);
  const selected = selectedValues(value);
  const current = multiple ? selected : [selected[0] || ""];
  const selectedOption = normalized.find(option => option.value === current[0]);
  const selectedLabels = normalized.filter(option => current.includes(option.value)).map(option => option.label);
  const summary = selectedLabels.length ? selectedLabels.join("、") : "请选择";
  const controlId = safeId(id);
  const triggerId = `${controlId}-trigger`;
  const menuId = `${controlId}-menu`;
  const storedValue = multiple ? JSON.stringify(current.filter(Boolean)) : current[0];
  const optionMarkup = normalized.map((option, index) => {
    const selectedState = current.includes(option.value);
    return `<button type="button" id="${menuId}-option-${index}" class="nxp-select-option${selectedState ? " is-selected" : ""}" role="option" data-nxp-select-option data-value="${esc(option.value)}" aria-selected="${selectedState ? "true" : "false"}"${option.disabled ? " disabled" : ""}${option.title ? ` title="${esc(option.title)}"` : ""}><span>${esc(option.label)}</span>${selectedState ? '<span class="nxp-select-check" aria-hidden="true">✓</span>' : ""}</button>`;
  }).join("");
  const hiddenAttributes = multiple ? ' data-nxp-select-multiple="true"' : "";
  const disabled = hasDisabledAttribute(extra) ? " disabled" : "";
  return `<div class="nxp-select" data-nxp-select${multiple ? ' data-nxp-select-multiple="true"' : ""}${rootExtra}>
    <input id="${controlId}" type="hidden" value="${esc(storedValue)}" data-nxp-select-value${hiddenAttributes} ${extra}>
    <button id="${triggerId}" class="nxp-select-trigger" type="button" data-nxp-select-trigger aria-haspopup="listbox" aria-expanded="false" aria-controls="${menuId}" aria-label="${esc(ariaLabel || selectedOption?.label || "请选择")}"${disabled}><span data-nxp-select-label>${esc(summary)}</span><span class="nxp-select-chevron" aria-hidden="true">⌄</span></button>
    <div id="${menuId}" class="nxp-select-menu secondary-surface" data-nxp-select-menu role="listbox"${multiple ? ' aria-multiselectable="true"' : ""} hidden>${optionMarkup}</div>
  </div>`;
}

/** 自定义数字步进框：文本输入承担可编辑值，按钮提供可访问的步进操作。 */
export function numberControlMarkup(id, value, extra = "", ariaLabel = "") {
  const controlId = safeId(id);
  const disabled = hasDisabledAttribute(extra) ? " disabled" : "";
  return `<div class="nxp-number" data-nxp-number><input id="${controlId}" class="nxp-number-input" type="text" inputmode="decimal" value="${esc(value)}" aria-label="${esc(ariaLabel || id)}" data-nxp-number-value ${extra}><span class="nxp-number-actions"><button type="button" class="nxp-number-step" data-nxp-step="increment" aria-label="增加"${disabled}>＋</button><button type="button" class="nxp-number-step" data-nxp-step="decrement" aria-label="减少"${disabled}>－</button></span></div>`;
}

/** 本机路径字段：文本框保持完全可编辑，右侧仅显示自绘文件 SVG 图标。 */
export function pathControlMarkup(id, value, kind = "file", extra = "", ariaLabel = "", filter = "", triggerExtra = "") {
  const controlId = safeId(id);
  const normalizedKind = ["file", "folder", "file-or-folder"].includes(String(kind)) ? String(kind) : "file";
  const disabled = hasDisabledAttribute(extra) ? " disabled" : "";
  const pickerFilter = filter ? ` data-path-filter="${esc(filter)}"` : "";
  return `<div class="nxp-path" data-nxp-path data-path-kind="${normalizedKind}">
    <input id="${controlId}" class="nxp-path-input" type="text" value="${esc(value)}" aria-label="${esc(ariaLabel || id)}" data-nxp-path-value ${extra}>
    <button type="button" class="nxp-path-trigger" data-action="pick-path" data-path-trigger data-path-target="${controlId}" data-path-kind="${normalizedKind}" data-path-title="${esc(ariaLabel || "路径")}" aria-label="选择路径" data-testid="path-picker"${pickerFilter}${disabled}${triggerExtra ? ` ${triggerExtra}` : ""}>${icon("file")}</button>
  </div>`;
}

/** 保留 range 的键盘/语义模型，完全由 CSS 绘制轨道与滑块。 */
export function rangeControlMarkup(id, value, extra = "", ariaLabel = "") {
  return `<input id="${safeId(id)}" class="nxp-range" type="range" value="${esc(value)}" aria-label="${esc(ariaLabel || id)}" data-nxp-range ${extra}>`;
}

function normalizedTimePart(value, limit) {
  const parsed = Number.parseInt(String(value ?? ""), 10);
  if (!Number.isFinite(parsed)) return 0;
  return ((parsed % limit) + limit) % limit;
}

function timeText(value) {
  return String(value).padStart(2, "0");
}

function timeWheelMarkup(unit, current, limit, label) {
  const attribute = unit === "hour" ? "data-nxp-time-hour" : "data-nxp-time-minute";
  const previous = (current - 1 + limit) % limit;
  const next = (current + 1) % limit;
  const options = [previous, current, next].map((value, index) => `<button type="button" class="nxp-time-option${index === 1 ? " is-current" : ""}" ${attribute}="${timeText(value)}" aria-selected="${index === 1 ? "true" : "false"}">${timeText(value)}</button>`).join("");
  return `<div class="nxp-time-wheel" data-nxp-time-wheel="${unit}" role="group" aria-label="${esc(label)}"><button type="button" class="nxp-time-step" data-nxp-time-adjust="${unit}:-1" aria-label="${esc(label)}减一">⌃</button><div class="nxp-time-viewport" role="listbox" aria-label="${esc(label)}">${options}</div><button type="button" class="nxp-time-step" data-nxp-time-adjust="${unit}:1" aria-label="${esc(label)}加一">⌄</button></div>`;
}

function timeWheelsMarkup(hour, minute) {
  return `${timeWheelMarkup("hour", normalizedTimePart(hour, 24), 24, "小时")}${timeWheelMarkup("minute", normalizedTimePart(minute, 60), 60, "分钟")}`;
}

/** 自定义时间选择器：输入值仍为 HH:mm，弹层内使用小时/分钟滚轮。 */
export function timeControlMarkup(id, value, extra = "", ariaLabel = "") {
  const controlId = safeId(id);
  const popupId = `${controlId}-popover`;
  const [hour, minute] = String(value || "").split(":");
  const disabled = hasDisabledAttribute(extra) ? " disabled" : "";
  return `<div class="nxp-time" data-nxp-time data-nxp-time-hour="${esc(hour || "")}" data-nxp-time-minute="${esc(minute || "")}" data-nxp-time-disabled="${disabled ? "true" : "false"}">
    <div class="nxp-time-input-wrap"><input id="${controlId}" class="nxp-time-value" type="text" value="${esc(value)}" aria-label="${esc(ariaLabel || id)}" readonly aria-haspopup="dialog" aria-expanded="false" aria-controls="${popupId}" data-nxp-time-value ${extra}><button type="button" class="nxp-time-trigger" data-nxp-time-trigger aria-label="打开时间选择器"${disabled}>⌄</button></div>
    <div id="${popupId}" class="nxp-time-popover secondary-surface" data-nxp-time-popover role="dialog" aria-label="${esc(ariaLabel || "选择时间")}" hidden><div class="nxp-time-columns" data-nxp-time-wheels>${timeWheelsMarkup(hour, minute)}</div></div>
  </div>`;
}

/** 文件选择只保留隐藏的安全载体，界面按钮和文件名展示完全自定义。 */
export function fileControlMarkup(id, extra = "", accept = "", multiple = false, label = "选择文件") {
  const controlId = safeId(id);
  return `<span class="nxp-file" data-nxp-file><button type="button" class="ghost nxp-file-trigger" data-nxp-file-trigger>${esc(label)}</button><span class="nxp-file-name" data-nxp-file-name>未选择文件</span><input id="${controlId}" class="sr-only" type="file"${accept ? ` accept="${esc(accept)}"` : ""}${multiple ? " multiple" : ""} data-nxp-file-input ${extra}></span>`;
}

function normalizedColor(value) {
  const raw = String(value || "").trim();
  if (/^#[0-9a-f]{6}$/iu.test(raw)) return raw.toLowerCase();
  if (/^#[0-9a-f]{3}$/iu.test(raw)) return `#${raw.slice(1).split("").map(part => `${part}${part}`).join("")}`.toLowerCase();
  return "#000000";
}

/** 自定义颜色字段：可见文本输入和色块负责交互，隐藏 color carrier 负责调用系统取色器。 */
export function colorControlMarkup(id, value, extra = "", ariaLabel = "") {
  const controlId = safeId(id);
  const pickerId = `${controlId}-picker`;
  const color = normalizedColor(value);
  const disabled = hasDisabledAttribute(extra) ? " disabled" : "";
  return `<div class="nxp-color" data-nxp-color data-nxp-color-value="${color}"><div class="nxp-color-row"><input id="${controlId}" class="nxp-color-value" type="text" inputmode="text" value="${esc(value || color)}" aria-label="${esc(ariaLabel || id)}" data-nxp-color-text ${extra}><button type="button" class="nxp-color-trigger" data-nxp-color-trigger aria-label="打开颜色选择器"${disabled}><span class="nxp-color-swatch" data-nxp-color-swatch aria-hidden="true"></span><span>选择颜色</span></button></div><input id="${pickerId}" class="sr-only" type="color" value="${color}" data-nxp-color-picker${disabled}></div>`;
}

/** 动态插件表单使用的 DOM 工厂，与静态表单共享同一事件和视觉层。 */
export function createSelectControl({ id, value = "", options = [], multiple = false, disabled = false, ariaLabel = "" } = {}) {
  const template = document.createElement("template");
  template.innerHTML = selectControlMarkup(id, value, options, disabled ? "disabled" : "", ariaLabel, multiple).trim();
  return template.content.firstElementChild;
}

export function createNumberControl({ id, value = "", min, max, step, disabled = false, ariaLabel = "" } = {}) {
  const attrs = [
    min == null ? "" : `min="${esc(min)}"`,
    max == null ? "" : `max="${esc(max)}"`,
    step == null ? "" : `step="${esc(step)}"`,
    disabled ? "disabled" : "",
  ].filter(Boolean).join(" ");
  const template = document.createElement("template");
  template.innerHTML = numberControlMarkup(id, value, attrs, ariaLabel).trim();
  return template.content.firstElementChild;
}

export function createColorControl({ id, value = "", disabled = false, ariaLabel = "" } = {}) {
  const template = document.createElement("template");
  template.innerHTML = colorControlMarkup(id, value, disabled ? "disabled" : "", ariaLabel).trim();
  const element = template.content.firstElementChild;
  syncColor(element, value);
  return element;
}

function selectValue(input) {
  if (!input?.dataset.nxpSelectMultiple) return [String(input?.value ?? "")].filter(Boolean);
  try {
    const parsed = JSON.parse(input.value || "[]");
    return Array.isArray(parsed) ? parsed.map(item => String(item)) : [];
  } catch {
    return [];
  }
}

function updateSelect(root) {
  const input = root?.querySelector("[data-nxp-select-value]");
  const label = root?.querySelector("[data-nxp-select-label]");
  if (!input || !label) return;
  const values = selectValue(input);
  const options = Array.from(root.querySelectorAll("[data-nxp-select-option]"));
  const labels = [];
  options.forEach(option => {
    const selected = values.includes(String(option.dataset.value || ""));
    option.setAttribute("aria-selected", selected ? "true" : "false");
    option.classList.toggle("is-selected", selected);
    const check = option.querySelector(".nxp-select-check");
    if (selected && !check) option.insertAdjacentHTML("beforeend", '<span class="nxp-select-check" aria-hidden="true">✓</span>');
    if (!selected && check) check.remove();
    if (selected) labels.push(option.querySelector("span")?.textContent || option.dataset.value || "");
  });
  label.textContent = labels.length ? labels.join("、") : "请选择";
  const trigger = root.querySelector("[data-nxp-select-trigger]");
  if (trigger) trigger.setAttribute("aria-label", labels.join("、") || "请选择");
}

function syncColor(root, value = null, dispatch = false) {
  const text = root?.querySelector("[data-nxp-color-text]");
  const picker = root?.querySelector("[data-nxp-color-picker]");
  const swatch = root?.querySelector("[data-nxp-color-swatch]");
  if (!text || !picker) return;
  const color = normalizedColor(value ?? text.value);
  root.dataset.nxpColorValue = color;
  picker.value = color;
  if (swatch) swatch.style.backgroundColor = color;
  if (dispatch) {
    dispatchValueEvent(text, "input");
    dispatchValueEvent(text, "change");
  }
}

function closePopovers(except = null) {
  document.querySelectorAll("[data-nxp-select], [data-nxp-time]").forEach(root => {
    if (root === except || root.contains(except)) return;
    const menu = root.querySelector("[data-nxp-select-menu], [data-nxp-time-popover]");
    const trigger = root.querySelector("[data-nxp-select-trigger], [data-nxp-time-trigger], [data-nxp-time-value]");
    if (menu) {
      if (root.matches("[data-nxp-time]") && menu.hidden === false) commitTime(root);
      menu.hidden = true;
      menu.removeAttribute("data-open");
      menu.removeAttribute("style");
    }
    if (trigger) trigger.setAttribute("aria-expanded", "false");
  });
}

function popoverAnchor(root) {
  if (root?.matches("[data-nxp-time]")) return root.querySelector(".nxp-time-input-wrap");
  return root?.querySelector("[data-nxp-select-trigger]");
}

function positionPopover(menu, trigger) {
  if (!menu || !trigger) return;
  const margin = 8;
  const gap = 6;
  const triggerRect = trigger.getBoundingClientRect();
  if (!triggerRect.width || !triggerRect.height) return;
  const availableWidth = Math.max(0, window.innerWidth - margin * 2);
  const preferredWidth = Math.min(triggerRect.width, availableWidth);
  menu.style.position = "fixed";
  menu.style.right = "auto";
  menu.style.width = `${Math.round(preferredWidth)}px`;
  const left = Math.min(
    Math.max(margin, triggerRect.left),
    Math.max(margin, window.innerWidth - preferredWidth - margin),
  );
  menu.style.left = `${Math.round(left)}px`;
  menu.style.top = `${Math.round(triggerRect.bottom + gap)}px`;
  const menuHeight = menu.offsetHeight;
  let top = triggerRect.bottom + gap;
  if (top + menuHeight > window.innerHeight - margin && triggerRect.top - menuHeight - gap >= margin) {
    top = triggerRect.top - menuHeight - gap;
  }
  top = Math.min(Math.max(margin, top), Math.max(margin, window.innerHeight - menuHeight - margin));
  menu.style.top = `${Math.round(top)}px`;
}

let popoverPositionBound = false;
function bindPopoverPositioning() {
  if (popoverPositionBound) return;
  popoverPositionBound = true;
  const reposition = () => {
    document.querySelectorAll("[data-nxp-select] [data-nxp-select-menu][data-open], [data-nxp-time] [data-nxp-time-popover][data-open]").forEach(menu => {
      const root = menu.closest("[data-nxp-select], [data-nxp-time]");
    const trigger = popoverAnchor(root);
    positionPopover(menu, trigger);
    });
  };
  document.addEventListener("scroll", reposition, true);
  window.addEventListener("resize", reposition);
}

function setPopoverOpen(root, open) {
  closePopovers(open ? root : null);
  const menu = root?.querySelector("[data-nxp-select-menu], [data-nxp-time-popover]");
  const trigger = root?.querySelector("[data-nxp-select-trigger], [data-nxp-time-trigger], [data-nxp-time-value]");
  if (!menu || !trigger) return;
  menu.hidden = !open;
  if (open) {
    menu.dataset.open = "true";
    bindPopoverPositioning();
    positionPopover(menu, popoverAnchor(root));
  } else {
    menu.removeAttribute("data-open");
    menu.removeAttribute("style");
  }
  trigger.setAttribute("aria-expanded", open ? "true" : "false");
  if (open && root.matches("[data-nxp-select]")) {
    const current = root.querySelector('[data-nxp-select-option][aria-selected="true"]:not(:disabled)')
      || root.querySelector("[data-nxp-select-option]:not(:disabled)");
    current?.focus();
  } else if (open && root.matches("[data-nxp-time]")) {
    root.querySelector('.nxp-time-option[aria-selected="true"]')?.focus();
  }
}

function dispatchValueEvent(input, type = "change") {
  input?.dispatchEvent(new Event(type, { bubbles: true }));
}

function parseNumberStep(input) {
  const value = Number(input?.value);
  const step = Number(input?.step) || 1;
  const base = Number.isFinite(value) ? value : Number(input?.min) || 0;
  const next = base + step;
  return Number.isInteger(step) ? Math.round(next) : Number(next.toFixed(8));
}

function updateTimeButtons(root) {
  if (!root) return;
  const values = [
    { unit: "hour", current: normalizedTimePart(root.dataset.nxpTimeHour, 24), limit: 24, attribute: "data-nxp-time-hour" },
    { unit: "minute", current: normalizedTimePart(root.dataset.nxpTimeMinute, 60), limit: 60, attribute: "data-nxp-time-minute" },
  ];
  values.forEach(({ unit, current, limit, attribute }) => {
    const wheel = root.querySelector(`[data-nxp-time-wheel="${unit}"]`);
    if (!wheel) return;
    const options = Array.from(wheel.querySelectorAll(".nxp-time-option"));
    [current - 1, current, current + 1].forEach((value, index) => {
      const option = options[index];
      if (!option) return;
      const normalized = (value + limit) % limit;
      option.setAttribute(attribute, timeText(normalized));
      option.textContent = timeText(normalized);
      option.classList.toggle("is-current", index === 1);
      option.setAttribute("aria-selected", index === 1 ? "true" : "false");
    });
  });
}

function updateTimeValue(root, dispatch = true) {
  const input = root.querySelector("[data-nxp-time-value]");
  const hour = root.dataset.nxpTimeHour;
  const minute = root.dataset.nxpTimeMinute;
  if (!input || !hour || !minute) return;
  input.value = `${String(hour).padStart(2, "0")}:${String(minute).padStart(2, "0")}`;
  if (dispatch) {
    dispatchValueEvent(input, "input");
    dispatchValueEvent(input, "change");
  }
}

function commitTime(root) {
  if (!root?.dataset.nxpTimeDirty) return;
  if (root.dataset.nxpTimeHour && root.dataset.nxpTimeMinute) updateTimeValue(root);
  delete root.dataset.nxpTimeDirty;
}

function installControlEvents() {
  if (typeof document === "undefined" || document.documentElement.dataset.nxpControlsBound === "true") return;
  document.documentElement.dataset.nxpControlsBound = "true";
  document.addEventListener("pointerdown", event => {
    const target = event.target instanceof Element ? event.target : null;
    if (!target?.closest("[data-nxp-select], [data-nxp-time]")) closePopovers();
  });
  document.addEventListener("click", event => {
    const target = event.target instanceof Element ? event.target : null;
    if (!target) return;
    const selectTrigger = target.closest("[data-nxp-select-trigger]");
    if (selectTrigger) {
      const root = selectTrigger.closest("[data-nxp-select]");
      if (!selectTrigger.disabled) setPopoverOpen(root, root.querySelector("[data-nxp-select-menu]")?.hidden !== false);
      return;
    }
    const switchControl = target.closest("[data-nxp-switch]");
    if (switchControl) {
      if (!switchControl.disabled) {
        const pressed = switchControl.getAttribute("aria-pressed") === "true";
        switchControl.setAttribute("aria-pressed", pressed ? "false" : "true");
        switchControl.dataset.state = pressed ? "off" : "on";
        dispatchValueEvent(switchControl);
      }
      return;
    }
    const colorTrigger = target.closest("[data-nxp-color-trigger]");
    if (colorTrigger) {
      if (!colorTrigger.disabled) colorTrigger.closest("[data-nxp-color]")?.querySelector("[data-nxp-color-picker]")?.click();
      return;
    }
    const selectOption = target.closest("[data-nxp-select-option]");
    if (selectOption && !selectOption.disabled) {
      const root = selectOption.closest("[data-nxp-select]");
      const input = root?.querySelector("[data-nxp-select-value]");
      if (!root || !input) return;
      const value = String(selectOption.dataset.value || "");
      if (input.dataset.nxpSelectMultiple) {
        const values = selectValue(input);
        const next = values.includes(value) ? values.filter(item => item !== value) : [...values, value];
        input.value = JSON.stringify(next);
        updateSelect(root);
        dispatchValueEvent(input);
      } else {
        input.value = value;
        updateSelect(root);
        dispatchValueEvent(input);
        setPopoverOpen(root, false);
        root.querySelector("[data-nxp-select-trigger]")?.focus();
      }
      return;
    }
    const timeTrigger = target.closest("[data-nxp-time-trigger], [data-nxp-time-value]");
    if (timeTrigger) {
      const root = timeTrigger.closest("[data-nxp-time]");
      if (!timeTrigger.disabled && root) setPopoverOpen(root, root.querySelector("[data-nxp-time-popover]")?.hidden !== false);
      return;
    }
    const timeAdjust = target.closest("[data-nxp-time-adjust]");
    if (timeAdjust && !timeAdjust.disabled) {
      const root = timeAdjust.closest("[data-nxp-time]");
      const [unit, directionText] = String(timeAdjust.dataset.nxpTimeAdjust || "").split(":");
      const direction = Number(directionText);
      if (!root || !["hour", "minute"].includes(unit) || !Number.isFinite(direction)) return;
      const limit = unit === "hour" ? 24 : 60;
      const dataKey = unit === "hour" ? "nxpTimeHour" : "nxpTimeMinute";
      const current = normalizedTimePart(root.dataset[dataKey], limit);
      root.dataset[dataKey] = timeText((current + direction + limit) % limit);
      updateTimeValue(root, false);
      root.dataset.nxpTimeDirty = "true";
      updateTimeButtons(root);
      return;
    }
    const timeHour = target.closest(".nxp-time-option[data-nxp-time-hour]");
    const timeMinute = target.closest(".nxp-time-option[data-nxp-time-minute]");
    if (timeHour || timeMinute) {
      const button = timeHour || timeMinute;
      const root = button.closest("[data-nxp-time]");
      if (!root) return;
      if (timeHour) root.dataset.nxpTimeHour = button.dataset.nxpTimeHour || "";
      if (timeMinute) root.dataset.nxpTimeMinute = button.dataset.nxpTimeMinute || "";
      updateTimeValue(root, false);
      root.dataset.nxpTimeDirty = "true";
      updateTimeButtons(root);
      return;
    }
    const step = target.closest("[data-nxp-step]");
    if (step && !step.disabled) {
      const input = step.closest("[data-nxp-number]")?.querySelector("[data-nxp-number-value]");
      if (!input) return;
      const current = Number(input.value);
      const amount = Number(input.step) || 1;
      let next = Number.isFinite(current) ? current : (Number(input.min) || 0);
      next += step.dataset.nxpStep === "decrement" ? -amount : amount;
      const min = Number(input.min);
      const max = Number(input.max);
      if (Number.isFinite(min)) next = Math.max(min, next);
      if (Number.isFinite(max)) next = Math.min(max, next);
      input.value = Number.isInteger(amount) ? String(Math.round(next)) : String(Number(next.toFixed(8)));
      dispatchValueEvent(input, "input");
      dispatchValueEvent(input);
      return;
    }
    const fileTrigger = target.closest("[data-nxp-file-trigger]");
    if (fileTrigger) {
      fileTrigger.closest("[data-nxp-file]")?.querySelector("[data-nxp-file-input]")?.click();
    }
  });
  document.addEventListener("change", event => {
    const target = event.target instanceof Element ? event.target : null;
    if (!target) return;
    const selectRoot = target.closest("[data-nxp-select]");
    if (selectRoot && target.matches("[data-nxp-select-value]")) updateSelect(selectRoot);
    if (target.matches("[data-nxp-file-input]")) {
      const root = target.closest("[data-nxp-file]");
      const files = Array.from(target.files || []);
      const label = root?.querySelector("[data-nxp-file-name]");
      if (label) label.textContent = files.length ? files.map(file => file.name).join("、") : "未选择文件";
    }
    if (target.matches("[data-nxp-color-picker]")) {
      syncColor(target.closest("[data-nxp-color]"), target.value, true);
    }
    if (target.matches("[data-nxp-color-text]")) syncColor(target.closest("[data-nxp-color]"), target.value);
  });
  document.addEventListener("focusout", event => {
    const target = event.target instanceof Element ? event.target : null;
    const root = target?.closest("[data-nxp-time]");
    if (!root || (event.relatedTarget instanceof Node && root.contains(event.relatedTarget))) return;
    commitTime(root);
    setPopoverOpen(root, false);
  });
  document.addEventListener("keydown", event => {
    const target = event.target instanceof Element ? event.target : null;
    if (!target) return;
    if (event.key === "Escape") {
      const root = target.closest("[data-nxp-select], [data-nxp-time]");
      if (root && root.querySelector("[data-nxp-select-menu]:not([hidden]), [data-nxp-time-popover]:not([hidden])")) {
        event.preventDefault();
        setPopoverOpen(root, false);
      }
      return;
    }
    const trigger = target.closest("[data-nxp-select-trigger], [data-nxp-time-trigger], [data-nxp-time-value]");
    if (trigger && ["Enter", " ", "ArrowDown", "ArrowUp"].includes(event.key)) {
      event.preventDefault();
      const root = trigger.closest("[data-nxp-select], [data-nxp-time]");
      const menu = root?.querySelector("[data-nxp-select-menu], [data-nxp-time-popover]");
      if (menu?.hidden !== false) setPopoverOpen(root, true);
      else if (event.key === "ArrowDown" || event.key === "ArrowUp") {
        const options = Array.from(root.querySelectorAll(root.matches("[data-nxp-time]") ? ".nxp-time-option" : "[data-nxp-select-option]:not(:disabled)"));
        const current = options.indexOf(document.activeElement);
        const next = options[(current + (event.key === "ArrowDown" ? 1 : -1) + options.length) % options.length];
        next?.focus();
      }
    }
    const option = target.closest("[data-nxp-select-option]");
    if (option && ["Enter", " "].includes(event.key)) {
      event.preventDefault();
      option.click();
    }
    const timeOption = target.closest(".nxp-time-option");
    if (timeOption && ["Enter", " "].includes(event.key)) {
      event.preventDefault();
      timeOption.click();
    } else if (timeOption && ["ArrowUp", "ArrowDown"].includes(event.key)) {
      event.preventDefault();
      const root = timeOption.closest("[data-nxp-time]");
      const unit = timeOption.closest("[data-nxp-time-wheel]")?.dataset.nxpTimeWheel;
      const direction = event.key === "ArrowDown" ? 1 : -1;
      root?.querySelector(`[data-nxp-time-adjust="${unit}:${direction}"]`)?.click();
    }
  });
}

installControlEvents();
