// @ts-nocheck
/**
 * 插件声明式表单控件工厂。
 *
 * 控件本身就是宿主公开的 `nxp-*` Native Custom Elements：DOM、键盘、ARIA、浮层、图标与视觉
 * 全部由公开组件实现，桥接层只负责 schema → 属性、值收集、改动同步与校验错误投影。
 * 这里不拼装任何控件 HTML，也不维护第二套 widget 实现。
 */
import { t } from "./host-adapter";
import type { PluginField, PluginFieldOption } from "./types";

/**
 * 声明式字段类型 → 公开元素与值载体。
 * 字段类型集合与宿主接受的插件 UI 字段一致；`ariaAsProperty` 表示组件通过 `ariaLabel` 属性承接无障碍名称。
 */
const FIELD_SPECS = {
  text: { tag: "nxp-text-input", carrier: "input", ariaAsProperty: false },
  url: { tag: "nxp-text-input", carrier: "input", ariaAsProperty: false, inputType: "url" },
  secret: { tag: "nxp-text-input", carrier: "input", ariaAsProperty: false, inputType: "password" },
  status: { tag: "nxp-text-input", carrier: "input", ariaAsProperty: false, readOnly: true },
  textarea: { tag: "nxp-text-area", carrier: "textarea", ariaAsProperty: false },
  number: { tag: "nxp-number-input", carrier: "[data-nxp-number-value]", ariaAsProperty: true, placeholder: true },
  range: { tag: "nxp-range", carrier: "input", ariaAsProperty: true },
  color: { tag: "nxp-color-picker", carrier: ".nxp-color-value", ariaAsProperty: true },
  switch: { tag: "nxp-switch", carrier: "button", ariaAsProperty: true },
  select: { tag: "nxp-select", carrier: "[data-nxp-select-value]", ariaAsProperty: true, placeholder: true, popover: true },
  "multi-select": { tag: "nxp-select", carrier: "[data-nxp-select-value]", ariaAsProperty: true, placeholder: true, popover: true },
};

export interface PluginFieldControl {
  /** 公开元素宿主节点；插入表单的就是它。 */
  element: HTMLElement;
  /** 字段类型（与插件声明一致）。 */
  type: string;
  /** label 的 for 目标：下拉控件指向可聚焦的触发器，其余指向值载体本身。 */
  labelFor: string;
  /** 值载体元素：字段错误高亮与焦点定位使用它。 */
  carrier: HTMLElement;
  /** 当前值，形态与插件保存载荷一致。 */
  value(): unknown;
  destroy(): void;
}

function normalizedOptions(options: Array<PluginFieldOption | string> | undefined) {
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

function selectedValues(value: unknown): string[] {
  if (Array.isArray(value)) return value.map(item => String(item ?? ""));
  return String(value ?? "").length ? [String(value)] : [];
}

function parseNumber(value: unknown): number {
  const parsed = Number(value);
  return Number.isFinite(parsed) ? parsed : 0;
}

function optionalNumber(value: unknown): number | undefined {
  if (value === undefined || value === null || value === "") return undefined;
  const parsed = Number(value);
  return Number.isFinite(parsed) ? parsed : undefined;
}

function initialValue(type: string, value: unknown): unknown {
  switch (type) {
    case "switch":
      return value === true;
    case "select":
      return selectedValues(value)[0] || "";
    case "multi-select":
      return selectedValues(value);
    case "number":
    case "range":
      return value == null ? "" : parseNumber(value);
    case "secret":
      return "";
    default:
      return value == null ? "" : String(value);
  }
}

function readFieldValue(type: string, raw: unknown): unknown {
  switch (type) {
    case "switch":
      return raw === true;
    case "multi-select":
      return selectedValues(raw);
    case "number":
    case "range":
      return parseNumber(raw);
    case "secret": {
      const text = String(raw ?? "");
      return text.length ? { action: "set", value: text } : { action: "keep" };
    }
    default:
      return raw == null ? "" : String(raw);
  }
}

/** 创建并挂载控件；元素进入文档后公开组件才完成渲染，因此 id 与无障碍属性在挂载后落到值载体上。 */
function createFieldElement(type: string, field: PluginField, value: unknown, container: HTMLElement): HTMLElement {
  const spec = FIELD_SPECS[type];
  const element = document.createElement(spec.tag);
  const multiple = type === "multi-select";
  const readOnly = field.readOnly === true || spec.readOnly === true;
  const props: Record<string, unknown> = {
    modelValue: initialValue(type, value),
    disabled: readOnly ? true : undefined,
    multiple: multiple ? true : undefined,
    options: type === "select" || type === "multi-select" ? normalizedOptions(field.options) : undefined,
    type: spec.inputType,
    ariaLabel: spec.ariaAsProperty ? (field.label || field.key) : undefined,
    placeholder: spec.placeholder ? (field.placeholder || undefined) : undefined,
    min: optionalNumber(field.min),
    max: optionalNumber(field.max),
    step: optionalNumber(field.step),
  };
  for (const [key, property] of Object.entries(props)) {
    if (property === undefined) continue;
    element[key] = property;
  }
  if (type === "secret" && value?.configured === true && !field.placeholder) {
    element.placeholder = t("common.configuration.keep_existing_hint");
  }
  container.append(element);
  return element;
}

/**
 * 创建插件表单控件：公开元素挂到容器后接管值载体，并把用户改动同步回自身受控属性。
 *
 * 公开元素是受控组件，桥接层负责把 change 事件解析出的值写回元素，保持显示与读取一致。
 */
export function createPluginFieldControl(
  field: PluginField,
  value: unknown,
  id: string,
  container: HTMLElement): PluginFieldControl {
  const type = String(field?.type || "text").toLowerCase();
  const spec = FIELD_SPECS[type];
  if (!spec) throw new Error(t("common.plugin_field_render_failed", { key: field.key }));
  const element = createFieldElement(type, field, value, container);
  const carrier = element.querySelector(spec.carrier);
  if (!carrier) {
    element.remove();
    throw new Error(t("common.plugin_field_render_failed", { key: field.key }));
  }
  const trigger = spec.popover ? element.querySelector(".nxp-select-trigger") : null;
  if (trigger) trigger.id = `${id}-trigger`;
  carrier.id = id;
  carrier.dataset.pluginFormField = field.key;
  carrier.dataset.pluginType = type;
  carrier.name = field.key;
  if (field.required) carrier.setAttribute("required", "");
  if (field.maxLength > 0) carrier.setAttribute("maxlength", String(field.maxLength));
  if (!spec.ariaAsProperty) carrier.setAttribute("aria-label", field.label || field.key);

  let current = type === "secret" ? { action: "keep" } : readFieldValue(type, initialValue(type, value));
  const onChange = (event: Event) => {
    const detail = (event as CustomEvent).detail;
    const next = Array.isArray(detail) ? detail[0] : (event.target as HTMLInputElement | null)?.value;
    current = readFieldValue(type, next);
    if (type === "secret") return;
    element.modelValue = type === "switch"
      ? next === true
      : type === "number" || type === "range"
        ? parseNumber(next)
        : next;
  };
  element.addEventListener("change", onChange);
  return {
    element,
    type,
    carrier,
    labelFor: trigger ? trigger.id : id,
    value: () => current,
    destroy() {
      element.removeEventListener("change", onChange);
      element.remove();
    },
  };
}
