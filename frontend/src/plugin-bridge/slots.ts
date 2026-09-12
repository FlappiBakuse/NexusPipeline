// @ts-nocheck
/**
 * 稳定 slot 名称、批量贡献查询、Form/Badge/Card 通用渲染和清理。
 * 宿主平台依赖只通过 host-adapter 获取。
 */
import {
  api,
  clearFieldError,
  isAbortError,
  setRequiredFieldError,
  t,
  toast,
} from "./host-adapter";
import { createPluginFieldControl, type PluginFieldControl } from "./controls";
import { validateRequiredPluginFields } from "./plugin-fields";
import { PLUGIN_SLOT_NAMES, disposePluginSlot, queryContributions, renderFrontendSlots } from "./runtime";

export const pluginSlotNames = PLUGIN_SLOT_NAMES;

const validSlots = new Set(pluginSlotNames);

/** 槽位内声明的表单控件：重新渲染槽位时统一注销，避免遗留事件监听与浮层。 */
const slotFormControls = new WeakMap<Element, PluginFieldControl[]>();

function disposeSlotForms(container) {
  const controls = slotFormControls.get(container) || [];
  slotFormControls.delete(container);
  controls.forEach(control => {
    try {
      control.destroy();
    } catch {
      // 单个控件注销失败不影响槽位重建。
    }
  });
}

function textElement(tag, text, className = "") {
  const element = document.createElement(tag);
  if (className) element.className = className;
  element.textContent = text == null ? "" : String(text);
  return element;
}

function payloadText(value) {
  if (value == null) return "";
  if (typeof value === "object") {
    try { return JSON.stringify(value); } catch { return ""; }
  }
  return String(value);
}

function toneClass(value) {
  const tone = String(value || "muted").toLowerCase();
  return ["muted", "blue", "ok", "warn", "bad"].includes(tone) ? tone : "muted";
}

function renderBadges(parent, values) {
  const badges = Array.isArray(values?.badges) ? values.badges : (values?.label ? [values] : []);
  badges.forEach(badge => {
    const span = textElement("span", badge.label || "", `badge ${toneClass(badge.tone)}`);
    if (badge.title) span.title = badge.title;
    parent.append(span);
  });
}

function renderFields(parent, values) {
  const fields = Array.isArray(values?.fields) ? values.fields : [];
  fields.forEach(field => {
    const row = document.createElement("div");
    row.className = "plugin-display-field";
    row.append(textElement("span", field.label || "", "muted"), textElement("strong", field.value ?? ""));
    parent.append(row);
  });
}

/** 收集表单当前值：形态与插件保存载荷一致，读取来源是桥接层控件而不是控件内部 DOM。 */
function readFormValues(controls) {
  const values = {};
  controls.forEach((control, key) => {
    if (key) values[key] = control.value();
  });
  return values;
}

function renderFormContribution(parent, contribution) {
  const form = document.createElement("form");
  form.className = "plugin-contribution-form";
  form.noValidate = true;
  form.dataset.pluginForm = `${contribution.pluginName}/${contribution.id}`;
  // 先挂载表单：公开元素接入文档后才渲染出内部控件，字段控件必须在其之后创建。
  parent.append(form);
  const fields = Array.isArray(contribution.fields) ? contribution.fields : [];
  const controls = new Map();
  try {
    fields.forEach(field => {
      const wrapper = document.createElement("div");
      wrapper.className = "field plugin-field";
      if (field.description) wrapper.dataset.help = field.description;
      const label = textElement("label", `${field.label || field.key}${field.required ? " *" : ""}`, "field-label");
      wrapper.append(label);
      form.append(wrapper);
      const controlId = `plugin-${contribution.pluginName}-${contribution.id}-${field.key}`.replace(/[^a-zA-Z0-9_-]/g, "-");
      const control = createPluginFieldControl(field, contribution.values?.[field.key], controlId, wrapper);
      label.htmlFor = control.labelFor;
      controls.set(String(field.key || ""), control);
    });
  } catch (error) {
    disposeSlotForms(form);
    form.remove();
    throw error;
  }
  const footer = document.createElement("div");
  footer.className = "row-actions";
  const save = textElement("button", "Save");
  save.type = "submit";
  footer.append(save);
  form.append(footer);
  slotFormControls.set(parent, [...(slotFormControls.get(parent) || []), ...controls.values()]);
  form.addEventListener("submit", async event => {
    event.preventDefault();
    if (save.disabled) return;
    const markRequired = control => setRequiredFieldError(control.carrier.id);
    const clearRequired = control => clearFieldError(control.carrier.id);
    if (!validateRequiredPluginFields(controls, fields, contribution.values || {}, markRequired, clearRequired)) {
      toast(t("common.plugin.settings_required"), "error");
      return;
    }
    save.disabled = true;
    try {
      await api("PUT", `/api/plugin-contributions/ui/${encodeURIComponent(contribution.pluginName)}/${encodeURIComponent(contribution.id)}`, {
        context: contribution.context,
        values: readFormValues(controls),
      });
      toast(t("common.plugin_settings_saved"));
    } catch (error) {
      if (!isAbortError(error)) toast(error.message, "error");
    } finally {
      save.disabled = false;
    }
  });
}

function renderDeclarativeContribution(parent, contribution) {
  const kind = String(contribution.kind || "card").toLowerCase();
  if (kind === "badge") {
    const wrap = document.createElement("span");
    wrap.className = "plugin-contribution-badge";
    renderBadges(wrap, contribution.values || {});
    if (!wrap.childElementCount) wrap.append(textElement("span", payloadText(contribution.values), "badge muted"));
    parent.append(wrap);
    return;
  }
  if (kind === "form") {
    renderFormContribution(parent, contribution);
    return;
  }
  const card = document.createElement("article");
  card.className = "plugin-contribution-card card section-surface";
  const heading = document.createElement("div");
  heading.className = "section-heading";
  heading.append(textElement("h3", contribution.title || contribution.id), textElement("span", contribution.pluginDisplayName || contribution.pluginName, "muted"));
  card.append(heading);
  if (contribution.description) card.append(textElement("p", contribution.description, "muted"));
  renderBadges(card, contribution.values || {});
  renderFields(card, contribution.values || {});
  if (!card.querySelector(".badge, .plugin-display-field") && contribution.values && Object.keys(contribution.values).length) {
    card.append(textElement("p", payloadText(contribution.values), "muted"));
  }
  parent.append(card);
}

async function paintPluginSlot(container, slot, context, contributions = null) {
  if (!container || !validSlots.has(slot)) return;
  await disposePluginSlot(container);
  disposeSlotForms(container);
  container.replaceChildren();
  let rendered = await renderFrontendSlots(container, slot, context);
  (contributions || [])
    .slice()
    .sort((left, right) => (Number(left.order) || 0) - (Number(right.order) || 0))
    .forEach(contribution => {
      renderDeclarativeContribution(container, contribution);
      rendered++;
    });
  container.hidden = rendered === 0;
}

export async function renderPluginSlot(container, slot, context = {}) {
  if (!container || !validSlots.has(slot)) return;
  let contributions = [];
  try {
    const payload = await queryContributions(slot, [{
      mode: context.mode || "",
      primaryId: context.primaryId || "",
      secondaryId: context.secondaryId || "",
    }]);
    contributions = Array.isArray(payload) ? payload : (payload?.contributions || []);
  } catch {
    // 老服务端或单个插件故障不影响宿主页面；插件 renderer 仍会完成渲染。
  }
  await paintPluginSlot(container, slot, context, contributions);
}
