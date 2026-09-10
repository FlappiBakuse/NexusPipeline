import { api, hydrateIcons } from "../../core/api.js";
import { esc } from "../../core/format.js";
import { pathField, selectField, switchControl, valueField } from "../../core/forms.js";
import { pluginMultiSelectMarkup, selectedPluginMultiSelectValues, syncPluginMultiSelect, validateRequiredPluginFields } from "../../core/plugin-fields.js";
import { closeModal, modalShell, showModal } from "../../core/modal.js";
import { clearFieldError, setRequiredFieldError, toast, withBusy } from "../../core/ui.js";
import { pluginSlotMarkup, renderPluginSlots } from "../../core/plugin-slots.js";
import { PRE_ONLY_MARKER, POST_FINAL_MARKER, encodePrePost, splitPrePost } from "../../core/prepost.js";
import { state } from "../../core/state.js";
import { reloadUsers, syncManagementSwitch, userById } from "./shared.js";
import { t } from "../../core/i18n.js";

let globalManagementDraft = null;

function globalFieldId(prefix, key) {
  return prefix + String(key || "field").replace(/[^a-zA-Z0-9_-]/g, "-");
}

function globalManagementHostMarkup(settings) {
  const general = settings.general || {};
  const notification = settings.notification || {};
  const advanced = settings.advanced || {};
  const maxRunDays = state.limits?.maxRunDays ?? 365;
  const maxSuccessfulRuns = state.limits?.maxSuccessfulRunsPerDay ?? 10;
  const pre = encodePrePost(PRE_ONLY_MARKER, advanced.preRunOnceOnly, advanced.preRunScript);
  const post = encodePrePost(POST_FINAL_MARKER, advanced.postRunOnFinalOnly, advanced.postRunScript);
  return '<div class="global-management-grid">' +
    `<section class="global-management-card"><div class="section-heading"><div><h3>${t("common.general")}</h3><p class="muted">${t("users.global_general_copy")}</p></div></div><div class="settings-list">` +
      switchControl("gm-general-sync", t("users.sync_general_settings"), t("users.global.general_override_help"), general.syncEnabled === true, "toggle-global-management-switch", 'data-global-field="general.syncEnabled"') +
      switchControl("gm-general-enabled", t("common.enabled"), t("users.global.disabled_help"), general.enabled !== false, "toggle-global-management-switch", 'data-global-field="general.enabled"') +
    '</div>' +
      valueField("gm-general-run-days", t("common.run_days"), typeof general.runDays === "number" ? general.runDays : -1, "number", `data-global-field="general.runDays" min="-1" max="${esc(maxRunDays)}" step="1" placeholder="${t("users.run_days_placeholder")}"`, t("users.run_days_help")) +
      valueField("gm-general-max-success", t("users.maximum_successful_runs"), typeof general.maxSuccessfulRunsPerDay === "number" ? general.maxSuccessfulRunsPerDay : -1, "number", `data-global-field="general.maxSuccessfulRunsPerDay" min="-1" max="${esc(maxSuccessfulRuns)}" step="1" placeholder="${t("users.editor.daily_limit.placeholder")}"`, t("users.binding.daily_limit.help")) +
    '</section>' +
    `<section class="global-management-card"><div class="section-heading"><div><h3>${t("common.notifications")}</h3><p class="muted">${t("users.global.notification_help")}</p></div></div><div class="settings-list">` +
      switchControl("gm-notification-sync", t("users.binding.sync_notifications"), t("users.global.notification_override_help"), notification.syncEnabled === true, "toggle-global-management-switch", 'data-global-field="notification.syncEnabled"') +
      switchControl("gm-notification-enabled", t("users.enable_notifications"), t("users.global_notification_copy"), notification.notifyEnabled !== false, "toggle-global-management-switch", 'data-global-field="notification.notifyEnabled"') +
    '</div>' +
      valueField("gm-notification-smtp", t("users.smtp_recipients"), notification.smtpTo || "", "text", `data-global-field="notification.smtpTo" placeholder="${t("users.binding.smtp_inherit_help")}"`, t("users.global_smtp_help")) +
    '</section>' +
    `<section class="global-management-card global-management-card-wide"><div class="section-heading"><div><h3>${t("users.advanced")}</h3><p class="muted">${t("users.global.lifecycle_help")}</p></div></div><div class="settings-list">` +
      switchControl("gm-advanced-sync", t("users.sync_advanced_settings"), t("users.global.advanced_override_help"), advanced.syncEnabled === true, "toggle-global-management-switch", 'data-global-field="advanced.syncEnabled"') +
    '</div>' +
      pathField("gm-advanced-pre", t("users.before_task_script_path"), pre, "file", `data-global-field="advanced.preRunScript" placeholder="${t("users.pre_task_placeholder")}"`, t("common.executable_file_filter"), "", t("users.pre_task_help")) +
      pathField("gm-advanced-post", t("users.after_task_script_path"), post, "file", `data-global-field="advanced.postRunScript" placeholder="${t("users.post_task_placeholder")}"`, t("common.executable_file_filter"), "", t("users.post_task_help")) +
    '</section>' +
  '</div>';
}

function pluginFieldMarkup(contribution, field) {
  const prefix = "gm-plugin-" + String(contribution.pluginName || "plugin").replace(/[^a-zA-Z0-9_-]/g, "-") + "-" + String(contribution.id || "settings").replace(/[^a-zA-Z0-9_-]/g, "-") + "-";
  const id = globalFieldId(prefix, field.key);
  const type = String(field.type || "text").toLowerCase();
  const value = contribution.values?.[field.key];
  const help = field.description ? ' data-help="' + esc(field.description) + '"' : "";
  const required = field.required ? ' <span class="req">*</span>' : "";
  const readOnly = field.readOnly ? " disabled" : "";
  if (type === "switch") {
    return switchControl(id, esc(field.label) + required, field.description || "", value === true, "toggle-global-plugin-switch", 'data-plugin-field="' + esc(field.key) + '" data-plugin-type="switch"' + readOnly, field.label);
  }
  if (type === "textarea") {
    return '<div class="field plugin-field"' + help + '><label class="field-label" for="' + esc(id) + '">' + esc(field.label) + required + '</label><textarea id="' + esc(id) + '" class="form-textarea" data-plugin-field="' + esc(field.key) + '" data-plugin-type="textarea"' + (field.maxLength > 0 ? ' maxlength="' + esc(field.maxLength) + '"' : "") + ' placeholder="' + esc(field.placeholder || "") + '"' + readOnly + '>' + esc(typeof value === "string" ? value : "") + '</textarea></div>';
  }
  if (type === "select") {
    const options = Array.isArray(field.options) ? field.options : [];
    return selectField(id, esc(field.label) + required, typeof value === "string" ? value : "", options, 'data-plugin-field="' + esc(field.key) + '" data-plugin-type="select"' + (field.readOnly ? " disabled" : ""), field.description || "");
  }
  if (type === "multi-select") {
    return pluginMultiSelectMarkup(id, field, value);
  }
  if (type === "secret") {
    const configured = value?.configured === true;
    return '<div class="field plugin-field plugin-secret-field"' + help + '><label class="field-label" for="' + esc(id) + '">' + esc(field.label) + required + '</label><div class="plugin-secret-row"><input id="' + esc(id) + '" type="password" data-plugin-field="' + esc(field.key) + '" data-plugin-type="secret" data-secret-action="keep" maxlength="' + esc(field.maxLength > 0 ? field.maxLength : 16384) + '" placeholder="' + esc(configured ? t("users.secret.configured_placeholder", { set: t("common.set"), leaveBlank: t("common.leave_blank_to_keep").toLowerCase() }) : (field.placeholder || t("users.editor.secret.placeholder"))) + '"' + readOnly + '>' + (configured && !field.readOnly ? '<button class="tertiary" type="button" data-action="clear-plugin-secret" data-plugin-field="' + esc(field.key) + '">' + t("users.clear") + '</button>' : "") + '</div></div>';
  }
  if (type === "status") {
    return '<div class="field plugin-field"' + help + '><span class="field-label">' + esc(field.label) + '</span><span class="plugin-status-value" data-plugin-field="' + esc(field.key) + '" data-plugin-type="status">' + esc(typeof value === "string" ? value : t("users.no_status")) + '</span></div>';
  }
  return valueField(id, esc(field.label) + required, typeof value === "string" ? value : "", "text", 'data-plugin-field="' + esc(field.key) + '" data-plugin-type="text" maxlength="' + esc(field.maxLength > 0 ? field.maxLength : 65536) + '" placeholder="' + esc(field.placeholder || "") + '"' + readOnly, field.description || "");
}

function globalManagementPluginMarkup(contributions) {
  if (!Array.isArray(contributions) || !contributions.length) return "";
  const cards = contributions.map(contribution => {
    const displayName = contribution.pluginDisplayName || contribution.pluginName || "";
    const title = contribution.title && String(contribution.title).trim() !== String(displayName).trim()
      ? '<strong>' + esc(contribution.title) + '</strong>'
      : "";
    return '<article class="global-management-plugin" data-plugin-name="' + esc(contribution.pluginName) + '" data-plugin-contribution-id="' + esc(contribution.id) + '"><div class="section-heading"><div><h4>' + esc(displayName) + '</h4>' + title + (contribution.description ? '<p class="muted">' + esc(contribution.description) + '</p>' : "") + '</div></div><div class="plugin-contribution-fields">' + (contribution.fields || []).map(field => pluginFieldMarkup(contribution, field)).join("") + '</div></article>';
  }).join("");
  return `<section class="global-management-plugins"><div class="section-heading"><div><h3>${t("common.plugin_settings")}</h3><p class="muted">${t("users.global.plugin_help")}</p></div></div>` + cards + '</section>';
}

function renderGlobalManagementModal() {
  if (!globalManagementDraft) return;
  const draft = globalManagementDraft;
  const body = globalManagementHostMarkup(draft.settings) + globalManagementPluginMarkup(draft.contributions) + pluginSlotMarkup("users.global.sections", "users.global.sections", "global-management-plugin-slot", { mode: "user", primaryId: draft.userId });
  const footer = `<button class="primary" type="button" data-action="save-global-management">${t("common.save")}</button><button class="ghost" type="button" data-action="close-modal">${t("common.cancel")}</button>`;
  showModal(modalShell(t("users.global_management"), body, footer), true, true, true);
  void renderPluginSlots(document);
  hydrateIcons(document);
}

export async function openGlobalManagement(userId) {
  if (!userById(userId)) return;
  try {
    const [settings, contributions] = await Promise.all([
      api("GET", "/api/users/" + encodeURIComponent(userId) + "/global-settings"),
      api("GET", "/api/plugin-contributions/user-global/" + encodeURIComponent(userId)),
    ]);
    globalManagementDraft = { userId, settings: settings || {}, contributions: contributions || [] };
    renderGlobalManagementModal();
  } catch (error) {
    toast(error.message, "error");
  }
}

function readGlobalManagementSettings() {
  const pressed = id => document.getElementById(id)?.getAttribute("aria-pressed") === "true";
  const value = id => document.getElementById(id)?.value || "";
  const runDays = parseInt(value("gm-general-run-days"), 10);
  const maxSuccessfulRuns = parseInt(value("gm-general-max-success"), 10);
  const pre = splitPrePost(PRE_ONLY_MARKER, value("gm-advanced-pre"));
  const post = splitPrePost(POST_FINAL_MARKER, value("gm-advanced-post"));
  return {
    general: {
      syncEnabled: pressed("gm-general-sync"),
      enabled: pressed("gm-general-enabled"),
      runDays: Number.isNaN(runDays) ? -1 : runDays,
      maxSuccessfulRunsPerDay: Number.isNaN(maxSuccessfulRuns) ? -1 : maxSuccessfulRuns,
    },
    notification: { syncEnabled: pressed("gm-notification-sync"), notifyEnabled: pressed("gm-notification-enabled"), smtpTo: value("gm-notification-smtp").trim() },
    advanced: {
      syncEnabled: pressed("gm-advanced-sync"),
      preRunScript: pre.value,
      preRunOnceOnly: pre.onceOnly,
      postRunScript: post.value,
      postRunOnFinalOnly: post.onceOnly,
    },
  };
}

function readPluginContributionValues(contribution) {
  const values = {};
  const container = document.querySelector('[data-plugin-name="' + CSS.escape(contribution.pluginName || "") + '"][data-plugin-contribution-id="' + CSS.escape(contribution.id || "") + '"]');
  for (const field of contribution.fields || []) {
    const type = String(field.type || "text").toLowerCase();
    const element = container?.querySelector('[data-plugin-field="' + CSS.escape(field.key) + '"]');
    if (!element || field.readOnly || type === "status") continue;
    if (type === "switch") {
      values[field.key] = element.getAttribute("aria-pressed") === "true";
    } else if (type === "multi-select") {
      values[field.key] = selectedPluginMultiSelectValues(element);
    } else if (type === "secret") {
      const action = element.dataset.secretAction === "clear"
        ? "clear"
        : element.value ? "set" : "keep";
      values[field.key] = action === "set" ? { action, value: element.value } : { action };
    } else {
      values[field.key] = element.value || "";
    }
  }
  return values;
}

function validateGlobalPluginContributions(contributions) {
  let valid = true;
  const markRequired = input => setRequiredFieldError(input.id);
  const clearRequired = input => clearFieldError(input.id);
  for (const contribution of contributions || []) {
    const container = document.querySelector('[data-plugin-name="' + CSS.escape(contribution.pluginName || "") + '"][data-plugin-contribution-id="' + CSS.escape(contribution.id || "") + '"]');
    if (!validateRequiredPluginFields(container, contribution.fields || [], contribution.values || {}, "data-plugin-field", markRequired, clearRequired)) valid = false;
  }
  return valid;
}

export async function saveGlobalManagement() {
  if (!globalManagementDraft) return;
  const draft = globalManagementDraft;
  const settings = readGlobalManagementSettings();
  if (!validateGlobalPluginContributions(draft.contributions)) {
    toast(t("common.plugin.settings_required"), "error");
    return;
  }
  try {
    const saved = await api("PUT", "/api/users/" + encodeURIComponent(draft.userId) + "/global-settings", settings);
    for (const contribution of draft.contributions || []) {
      await api("PUT", "/api/plugin-contributions/user-global/" + encodeURIComponent(draft.userId) + "/" + encodeURIComponent(contribution.pluginName) + "/" + encodeURIComponent(contribution.id), {
        values: readPluginContributionValues(contribution),
      });
    }
    globalManagementDraft = null;
    closeModal();
    toast(t("users.global_settings_saved"));
    await reloadUsers();
    return saved;
  } catch (error) {
    toast(error.message, "error");
  }
}

export function toggleGlobalManagementSwitch(target) {
  if (target.disabled) return;
  syncManagementSwitch(target, target.getAttribute("aria-pressed") !== "true");
}

export function toggleGlobalPluginSwitch(target) {
  if (target.disabled) return;
  syncManagementSwitch(target, target.getAttribute("aria-pressed") !== "true");
}

export function clearPluginSecret(target) {
  const field = target.closest(".global-management-plugin")?.querySelector('[data-plugin-field="' + CSS.escape(target.dataset.pluginField || "") + '"]');
  if (!field) return;
  field.value = "";
  field.dataset.secretAction = "clear";
  target.disabled = true;
}

function closePluginMultiSelects(except = null) {
  document.querySelectorAll('.plugin-multi-select-trigger[aria-expanded="true"]').forEach(trigger => {
    const wrapper = trigger.closest(".plugin-multi-select");
    if (wrapper === except) return;
    trigger.setAttribute("aria-expanded", "false");
    const menu = wrapper?.querySelector(".plugin-multi-select-menu");
    if (menu) menu.hidden = true;
  });
}

export function togglePluginMultiSelect(target) {
  const wrapper = target.closest(".plugin-multi-select");
  if (!wrapper || target.disabled) return;
  const expanded = target.getAttribute("aria-expanded") === "true";
  closePluginMultiSelects(wrapper);
  target.setAttribute("aria-expanded", expanded ? "false" : "true");
  const menu = wrapper.querySelector(".plugin-multi-select-menu");
  if (menu) menu.hidden = expanded;
}

export function syncPluginMultiSelectOption(target) {
  const option = target.closest("[data-plugin-multi-option]");
  if (!option || option.disabled) return;
  option.setAttribute("aria-selected", option.getAttribute("aria-selected") === "true" ? "false" : "true");
  syncPluginMultiSelect(option.closest(".plugin-multi-select"));
}

if (typeof document !== "undefined") {
  document.addEventListener("click", event => {
    if (!event.target?.closest?.(".plugin-multi-select")) closePluginMultiSelects();
  });
  document.addEventListener("keydown", event => {
    if (event.key !== "Escape") return;
    const openTrigger = document.querySelector('.plugin-multi-select-trigger[aria-expanded="true"]');
    if (!openTrigger) return;
    closePluginMultiSelects();
    event.preventDefault();
    event.stopImmediatePropagation();
  }, true);
  document.addEventListener("input", event => {
    const input = event.target?.closest?.('.plugin-secret-field input[data-plugin-type="secret"]');
    if (!input) return;
    input.dataset.secretAction = input.value ? "set" : "keep";
  });
}

export const actions = {
  "open-global-management": target => withBusy(target, () => openGlobalManagement(target.dataset.userId)),
  "save-global-management": target => withBusy(target, () => saveGlobalManagement()),
  "toggle-global-management-switch": target => toggleGlobalManagementSwitch(target),
  "toggle-global-plugin-switch": target => toggleGlobalPluginSwitch(target),
  "clear-plugin-secret": target => clearPluginSecret(target),
  "toggle-plugin-multi-select": target => togglePluginMultiSelect(target),
  "sync-plugin-multi-select-option": target => syncPluginMultiSelectOption(target),
};
