import { api } from "./api.js";
import { disposePluginSlot, queryContributions, renderFrontendSlots } from "./plugin-runtime.js";
import { toast } from "./ui.js";

export const pluginSlotNames = Object.freeze([
  "dashboard.cards", "dashboard.after-running", "users.list.badges", "users.binding.sections", "users.global.sections",
  "scripts.list.badges", "scripts.editor.sections", "queues.list.badges", "queues.editor.sections", "dispatch.cards",
  "dispatch.running.badges", "dispatch.run.sections", "history.list.badges", "history.detail.sections", "settings.sections", "settings.cards", "shell.nav",
]);

const validSlots = new Set(pluginSlotNames);

export function pluginSlotMarkup(slot, anchor = slot, className = "", context = {}) {
  if (!validSlots.has(slot)) throw new TypeError(`插件 UI slot 不受支持：${slot}`);
  const contextAttributes = ["mode", "primaryId", "secondaryId"]
    .filter(key => context[key] !== undefined && context[key] !== null)
    .map(key => ` data-plugin-${key.replace(/[A-Z]/g, letter => `-${letter.toLowerCase()}`)}="${escapeAttribute(context[key])}"`)
    .join("");
  return `<div class="plugin-slot ${className}" data-plugin-slot="${escapeAttribute(slot)}" data-plugin-anchor="${escapeAttribute(anchor)}"${contextAttributes} hidden></div>`;
}

function escapeAttribute(value) {
  return String(value || "").replace(/[&<>"']/g, character => ({ "&": "&amp;", "<": "&lt;", ">": "&gt;", '"': "&quot;", "'": "&#39;" }[character]));
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

function createInput(field, value) {
  const type = String(field.type || "text").toLowerCase();
  let input;
  if (type === "textarea") input = document.createElement("textarea");
  else if (type === "select" || type === "multi-select") input = document.createElement("select");
  else input = document.createElement("input");
  input.dataset.pluginFormField = field.key;
  input.dataset.pluginType = type;
  input.name = field.key;
  input.disabled = field.readOnly === true;
  if (field.required) input.required = true;
  if (field.placeholder) input.placeholder = field.placeholder;
  if (field.maxLength > 0) input.maxLength = field.maxLength;
  if (type === "number" || type === "range") {
    input.type = type;
    if (field.min != null) input.min = field.min;
    if (field.max != null) input.max = field.max;
    if (field.step != null) input.step = field.step;
  } else if (type === "color") input.type = "color";
  else if (type === "secret") input.type = "password";
  else if (type === "url") input.type = "url";
  else if (type === "switch") {
    input.type = "checkbox";
    input.checked = value === true;
  }
  if (type === "select" || type === "multi-select") {
    if (type === "multi-select") input.multiple = true;
    (Array.isArray(field.options) ? field.options : []).forEach(option => {
      const optionEl = document.createElement("option");
      optionEl.value = typeof option === "string" ? option : option.value;
      optionEl.textContent = typeof option === "string" ? option : option.label;
      optionEl.selected = type === "multi-select" ? Array.isArray(value) && value.map(String).includes(optionEl.value) : String(value ?? "") === optionEl.value;
      input.append(optionEl);
    });
  } else if (type !== "switch" && type !== "secret") {
    input.value = value == null ? "" : String(value);
  }
  if (type === "secret" && value?.configured === true && !input.placeholder) {
    input.placeholder = "已设置，留空保持不变";
  }
  return input;
}

function readFormValues(form) {
  const values = {};
  form.querySelectorAll("[data-plugin-form-field]").forEach(input => {
    const type = input.dataset.pluginType;
    if (type === "switch") values[input.dataset.pluginFormField] = input.checked;
    else if (type === "multi-select") values[input.dataset.pluginFormField] = Array.from(input.selectedOptions).map(option => option.value);
    else if (type === "number" || type === "range") values[input.dataset.pluginFormField] = Number(input.value);
    else if (type === "secret") values[input.dataset.pluginFormField] = input.value ? { action: "set", value: input.value } : { action: "keep" };
    else values[input.dataset.pluginFormField] = input.value;
  });
  return values;
}

function renderFormContribution(parent, contribution) {
  const form = document.createElement("form");
  form.className = "plugin-contribution-form";
  form.dataset.pluginForm = `${contribution.pluginName}/${contribution.id}`;
  const fields = Array.isArray(contribution.fields) ? contribution.fields : [];
  fields.forEach(field => {
    const wrapper = document.createElement("div");
    wrapper.className = "field plugin-field";
    const label = textElement("label", `${field.label || field.key}${field.required ? " *" : ""}`, "field-label");
    const input = createInput(field, contribution.values?.[field.key]);
    label.htmlFor = input.id = `plugin-${contribution.pluginName}-${contribution.id}-${field.key}`.replace(/[^a-zA-Z0-9_-]/g, "-");
    wrapper.append(label, input);
    if (field.description) wrapper.append(textElement("span", field.description, "muted plugin-field-description"));
    form.append(wrapper);
  });
  const footer = document.createElement("div");
  footer.className = "row-actions";
  const save = textElement("button", "保存");
  save.type = "submit";
  footer.append(save);
  form.append(footer);
  form.addEventListener("submit", async event => {
    event.preventDefault();
    if (save.disabled) return;
    save.disabled = true;
    try {
      await api("PUT", `/api/plugin-contributions/ui/${encodeURIComponent(contribution.pluginName)}/${encodeURIComponent(contribution.id)}`, {
        context: contribution.context,
        values: readFormValues(form),
      });
      toast("插件设置已保存");
    } catch (error) {
      toast(error.message, "error");
    } finally {
      save.disabled = false;
    }
  });
  parent.append(form);
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
    // 老服务端或单个插件故障不影响宿主页面；可信前端 renderer 仍会完成渲染。
  }
  await paintPluginSlot(container, slot, context, contributions);
}

export async function renderPluginSlots(root = document) {
  const groups = new Map();
  root.querySelectorAll("[data-plugin-slot]").forEach(container => {
    const slot = container.dataset.pluginSlot;
    if (!validSlots.has(slot)) return;
    const context = {
      mode: container.dataset.pluginMode || "",
      primaryId: container.dataset.pluginPrimaryId || "",
      secondaryId: container.dataset.pluginSecondaryId || "",
    };
    const group = groups.get(slot) || [];
    group.push({ container, context });
    groups.set(slot, group);
  });
  for (const [slot, entries] of groups) {
    let contributions = [];
    try {
      const payload = await queryContributions(slot, entries.map(entry => entry.context));
      contributions = Array.isArray(payload) ? payload : (payload?.contributions || []);
    } catch {
      // 批量查询失败时保留可信前端 slot 的渲染结果。
    }
    for (const entry of entries) {
      const matching = contributions.filter(contribution => {
        const candidate = contribution.context || {};
        return String(candidate.mode || "") === entry.context.mode
          && String(candidate.primaryId || "") === entry.context.primaryId
          && String(candidate.secondaryId || "") === entry.context.secondaryId;
      });
      await paintPluginSlot(entry.container, slot, entry.context, matching);
    }
  }
}
