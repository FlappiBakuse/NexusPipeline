import { api } from "../core/api.js";
import { $ } from "../core/dom.js";
import { esc } from "../core/format.js";
import { pageHeader, selectField, switchControl, valueField } from "../core/forms.js";
import { icon } from "../core/icons.js";
import { closeModal, confirmModal, modalShell, showModal } from "../core/modal.js";
import { isCurrent, schedule, state } from "../core/state.js";
import { navActive, render, setTopbarTitle, toast, withBusy } from "../core/ui.js";
import { pluginSlotMarkup, renderPluginSlots } from "../core/plugin-slots.js";
import { updateActionsMarkup } from "../core/update-status.js";
import { applyTranslations, getLocale, getLocaleOptions, setLocale, t } from "../core/i18n.js";

let restartRequired = false;
let openSettingsPanel = "service";
let updateAutoNoticeKey = "";

export async function pageSettings(token) {
  if (!isCurrent("settings", token)) return;
  navActive("settings"); setTopbarTitle(t("shell.settings", {}, "Settings"));
  let data;
  try { data = await api("GET", "/api/settings"); }
  catch (error) { render(`<div class="empty"><strong>${t("settings.error.load")}</strong>${esc(error.message)}</div>`); return; }
  if (!isCurrent("settings", token)) return;
  state.settings = data.settings;
  const settings = data.settings;
  const remote = data.status && data.status.remote;
  const lanList = (remote && remote.lanAddresses && remote.lanAddresses.length)
    ? remote.lanAddresses.map(addr => `<div class="kv"><span class="k">${t("settings.lan_address")}</span><span>http://${esc(addr)}:${settings.webPort}/</span></div>`).join("")
    : "";
  openSettingsPanel = "service";
  render(pageHeader(t("settings.title", {}, "System settings"), t("shell.settings", {}, "Settings"), t("settings.page.help")) + restartNoticeMarkup(settings) + `<div class="settings-cards" data-testid="settings-cards">
    ${settingsCardMarkup("service", "settings.service_behavior", "settings.service.options_help", serviceSettingsMarkup(settings), "service-settings")}
    ${settingsCardMarkup("notifications", "settings.notification_channels", "settings.notification.channels_help", notificationSettingsMarkup(settings), "notification-settings")}
    ${settingsCardMarkup("remote-mcp", "settings.remote_access.mcp", "settings.remote_access.management_help", remoteMcpSettingsMarkup(settings, lanList), "mcp-settings")}
    ${settingsCardMarkup("network", "settings.network_proxy", "settings.network.external_requests", networkSettingsMarkup(settings), "network-settings")}
    ${settingsCardMarkup("updates", "settings.update_settings", "settings.update.channel_help", updateSectionMarkup(settings), "update-section")}
    ${settingsCardMarkup("diagnostics", "settings.system_diagnostics", "settings.diagnostics.check_help", diagnosticsSettingsMarkup(), "diagnostics-settings")}
    ${pluginSlotMarkup("settings.cards", "settings.cards", "settings-cards-plugin-slot", { mode: "settings" })}
  </div>${pluginSlotMarkup("settings.sections", "settings.sections", "settings-plugin-slot", { mode: "settings" })}`);
  await renderPluginSlots(document.querySelector("#view"));
  syncSettingsPanels();
  bindAutoSave();
  renderUpdateStatus();
  loadUpdateStatus(token);
  loadDiagnostics(token);
}

function settingsCardMarkup(id, title, description, body, testId) {
  const expanded = openSettingsPanel === id;
  return `<section class="settings-card section-surface${expanded ? " is-expanded" : ""}" data-settings-panel="${id}"${testId ? ` data-testid="${testId}"` : ""}>
    <button class="settings-card-toggle" type="button" data-action="toggle-settings-panel" data-panel="${id}" aria-expanded="${expanded ? "true" : "false"}" aria-controls="settings-panel-${id}"><span class="settings-card-copy"><strong class="settings-card-title">${t(title)}</strong><span class="muted">${t(description)}</span></span><span class="settings-card-arrow" aria-hidden="true">${icon(expanded ? "chevronDown" : "chevronRight", "settings-card-arrow-icon")}</span></button>
    <div id="settings-panel-${id}" class="settings-card-body"${expanded ? "" : " hidden"}>${body}</div>
  </section>`;
}

async function changeLocale(target) {
  await setLocale(target.value);
  location.reload();
}

function syncSettingsPanels() {
  document.querySelectorAll("[data-settings-panel]").forEach(card => {
    const expanded = card.dataset.settingsPanel === openSettingsPanel;
    card.classList.toggle("is-expanded", expanded);
    const toggle = card.querySelector(".settings-card-toggle");
    if (toggle) {
      toggle.setAttribute("aria-expanded", expanded ? "true" : "false");
      const arrow = toggle.querySelector(".settings-card-arrow");
      if (arrow) arrow.innerHTML = icon(expanded ? "chevronDown" : "chevronRight", "settings-card-arrow-icon");
    }
    const body = card.querySelector(".settings-card-body");
    if (body) body.hidden = !expanded;
  });
}

function toggleSettingsPanel(panelId) {
  const builtInPanels = ["service", "notifications", "remote-mcp", "network", "updates", "diagnostics"];
  const isRenderedPluginPanel = Array.from(document.querySelectorAll("[data-settings-panel]"))
    .some(card => card.dataset.settingsPanel === panelId && card.querySelector(".settings-card-body"));
  if (!builtInPanels.includes(panelId) && !isRenderedPluginPanel) return;
  openSettingsPanel = openSettingsPanel === panelId ? null : panelId;
  syncSettingsPanels();
}

function restartNoticeMarkup(settings) {
  if (!restartRequired) return "";
  const disabled = settings.lightweightMode;
  return `<section id="restart-notice" class="dashboard-system-note" role="status" aria-live="polite"><p>${t("settings.service.restart_saved")}</p>${disabled ? `<span class="muted">${t("settings.service.lightweight_restart")}</span>` : `<button class="primary" type="button" data-action="restart-service" data-testid="restart-service">${t("settings.restart_service")}</button>`}</section>`;
}

function serviceSettingsMarkup(settings) {
  const locale = getLocale();
  const localeOptions = getLocaleOptions().map(item => ({ value: item.id, label: item.nativeName }));
  return `<div class="settings-list">
    ${switchControl("st-autostart", t("settings.start_with_windows"), t("settings.service.startup_registration"), settings.autoStart, "toggle-st-flag", 'data-flag="st-autostart"')}
    ${switchControl("st-lightweight", t("settings.lightweight_mode"), t("settings.service.web_disabled"), settings.lightweightMode, "toggle-st-flag", 'data-flag="st-lightweight" data-restart-required="true"')}
    ${switchControl("st-browser", t("settings.open_browser"), t("settings.service.console_startup"), settings.autoOpenBrowser, "toggle-st-flag", 'data-flag="st-browser"')}
  </div><div class="settings-service-fields" data-help="${t("settings.service.restart_requirements")}"><div class="form-grid settings-service-grid settings-service-grid-primary">${valueField("st-retention", t("settings.history_retention_days"), settings.historyRetentionDays, "number", 'min="1" max="180"')}${valueField("st-port", t("settings.web_port"), settings.webPort, "number", 'min="1024" max="65535"')}${selectField("st-loglevel", t("settings.log_level"), settings.logLevel || "info", [{ value: "debug", label: t("settings.log_level_debug") }, { value: "info", label: t("settings.log_level_info") }, { value: "warn", label: t("settings.log_level_warn") }, { value: "error", label: t("settings.log_level_error") }, { value: "fatal", label: t("settings.log_level_fatal") }])}</div><div class="form-grid settings-service-grid settings-service-grid-locale">${selectField("settings-locale", t("settings.language", {}, "Interface language"), locale, localeOptions, 'data-action="change-locale"', t("settings.language_help", {}, "Language preference is stored in this browser only"))}${selectField("st-host-locale", t("settings.host_language", {}, "Host language"), settings.hostLocale || "zh-CN", localeOptions, "", t("settings.host_language_help", {}, "Controls CLI, tray, notifications, and background logs."))}</div></div>${settings.lightweightMode ? `<p class="callout callout-warning">${t("settings.service.lightweight_not_started")}</p>` : ""}`;
}

function remoteMcpSettingsMarkup(settings, lanList) {
  const port = Number(settings.mcpPort) || 58732;
  return `<div class="settings-merged-content">
    <section class="settings-subsection remote-settings"><div class="settings-list">${switchControl("st-remote", t("settings.remote_access"), t("settings.remote_access.bind_all_help"), settings.allowRemoteAccess, "toggle-st-flag", 'data-flag="st-remote" data-restart-required="true"')}</div><div class="field-btn-row">${valueField("st-token", t("settings.access_token"), "", "password", `autocomplete="new-password" placeholder="${t("common.leave_blank_to_keep")}"`, t("settings.remote_access.token_keep_help"))}<button type="button" class="ghost" data-action="toggle-token-visibility" data-testid="toggle-token-visibility" aria-pressed="false">${t("common.show")}</button><button type="button" class="ghost" data-action="copy-token">${t("settings.copy")}</button><button type="button" class="ghost" data-action="gen-token" data-testid="gen-token">${t("settings.generate_token")}</button></div><div id="remote-lan-list" class="detail"${settings.allowRemoteAccess ? ` data-help="${t("settings.remote_access.lan_help_short")}"` : ""}>${lanList}</div><p class="callout callout-warning">${t("settings.remote_access.warning")}</p></section>
    <section class="settings-subsection mcp-settings"><div class="settings-list">
    ${switchControl("st-mcp-enabled", t("settings.enable_mcp_service"), t("settings.remote_access.mcp_listen_help"), settings.mcpEnabled, "toggle-st-flag", 'data-flag="st-mcp-enabled" data-restart-required="true"')}
  </div><div class="form-grid settings-single-field" data-help="${t("settings.remote_access.mcp_endpoint_help", { port })}">${valueField("st-mcp-port", t("settings.mcp_port"), port, "number", 'min="1024" max="65535"')}</div></section>
  </div>`;
}

function networkSettingsMarkup(settings) {
  const mode = settings.proxyMode || "none";
  const customHidden = mode === "http" ? "" : " hidden";
  return `<div class="network-settings" data-help="${t("settings.network_proxy_help")}">
    ${selectField("st-proxy-mode", t("settings.proxy_mode"), mode, [{ value: "none", label: t("settings.no_proxy") }, { value: "system", label: t("settings.use_system_settings") }, { value: "http", label: t("settings.http_https_proxy") }], 'data-action="toggle-proxy-fields"')}
    <div id="st-proxy-custom" class="proxy-custom-fields"${customHidden}>
      ${valueField("st-proxy-url", t("settings.http_https_proxy_address"), settings.proxyUrl || "", "text", 'placeholder="http://127.0.0.1:7890"', t("settings.validation.proxy_scheme"))}
      ${valueField("st-proxy-user", t("settings.username_optional"), settings.proxyUsername || "")}
      <div class="field" data-help="${t("settings.network.proxy_password_keep")}"><label class="field-label" for="st-proxy-pwd">${t("settings.password_optional")} ${settings.proxyPassword ? `<span class="badge ok">${t("common.set")}</span>` : ""}</label><input id="st-proxy-pwd" type="password" autocomplete="new-password" placeholder="${settings.proxyPassword ? t("common.leave_blank_to_keep") : ""}"></div>
    </div>
  </div>`;
}

function notificationSettingsMarkup(settings) {
  const body = `<div class="notification-settings">
    <button class="panel-toggle" type="button" data-action="toggle-panel" data-panel="panel-wh" aria-expanded="true" aria-controls="panel-wh"><span class="panel-arrow" id="arrow-wh">▾</span><span class="panel-label">Webhook ${t("common.notifications")}</span><span class="badge ${settings.webhookEnabled ? "ok" : "muted"}">${settings.webhookEnabled ? t("common.enabled") : t("common.disabled")}</span></button>
    <div id="panel-wh" class="panel-body"><div class="settings-list">${switchControl("st-wh-enabled", t("settings.enable_webhook"), t("settings.notification.webhook_status"), settings.webhookEnabled, "toggle-notify-flag", 'data-flag="st-wh-enabled"')}${switchControl("st-wh-screenshot", t("settings.send_screenshots"), t("settings.notification.screenshot_scope"), settings.webhookScreenshotEnabled, "toggle-notify-flag", 'data-flag="st-wh-screenshot"')}</div><div class="form-grid">${selectField("st-whtype", t("settings.webhook_type"), settings.webhookType, [{ value: "feishu", label: "Feishu" }, { value: "dingtalk", label: "Dingtalk" }, { value: "wecom", label: "WeCom" }, { value: "slack", label: "Slack" }, { value: "discord", label: "Discord" }, { value: "generic", label: "Generic" }], 'data-action="toggle-webhook-fields"')} ${valueField("st-whtimeout", t("settings.timeout_seconds"), settings.webhookTimeout || 30, "number", 'min="1"')}</div><div class="form-grid"><div class="field" data-help="${t("settings.notification.webhook_url_keep")}"><label class="field-label" for="st-whurl">${t("settings.webhook_address")} ${settings.webhookUrl ? `<span class="badge ok">${t("common.set")}</span>` : ""}</label><input id="st-whurl" type="text" placeholder="${settings.webhookUrl ? t("common.leave_blank_to_keep") : "https://…"}"></div><div class="field" data-help="${t("settings.notification.secret_keep")}"><label class="field-label" for="st-whsec">${t("settings.webhook_signing_secret")} ${settings.webhookSecret ? `<span class="badge ok">${t("common.set")}</span>` : ""}</label><input id="st-whsec" type="password" placeholder="${settings.webhookSecret ? t("common.leave_blank_to_keep") : ""}"></div></div>${webhookAdvancedMarkup(settings)}<div id="st-whtpl-box" class="field" data-help="${t("settings.notification.template_help")}" ${settings.webhookType === "generic" ? "" : "hidden"}><label class="field-label" for="st-whtpl">${t("settings.notification.custom_template")}</label><textarea id="st-whtpl">${esc(settings.webhookTemplate || "")}</textarea></div></div>
    <button class="panel-toggle" type="button" data-action="toggle-panel" data-panel="panel-smtp" aria-expanded="false" aria-controls="panel-smtp"><span class="panel-arrow" id="arrow-smtp">▸</span><span class="panel-label">SMTP ${t("settings.notification.smtp")}</span><span class="badge ${settings.smtpEnabled ? "ok" : "muted"}">${settings.smtpEnabled ? t("common.enabled") : t("common.disabled")}</span></button>
    <div id="panel-smtp" class="panel-body" hidden><div class="settings-list">${switchControl("st-smtp-enabled", t("settings.enable_smtp"), t("settings.notification.email_status"), settings.smtpEnabled, "toggle-notify-flag", 'data-flag="st-smtp-enabled"')}${switchControl("st-smtp-screenshot", t("settings.send_screenshots"), t("settings.notification.screenshot_scope"), settings.smtpScreenshotEnabled, "toggle-notify-flag", 'data-flag="st-smtp-screenshot"')}</div><div class="form-grid three">${valueField("st-host", t("settings.smtp_server"), settings.smtpHost)}${valueField("st-port2", t("settings.port"), settings.smtpPort, "number")}${selectField("st-secure", t("settings.encryption"), settings.smtpSecure, ["auto", "ssl", "starttls", "none"])}</div><div class="form-grid">${valueField("st-user", t("common.account"), settings.smtpUser)}<div class="field"><label class="field-label" for="st-pwd">${t("settings.smtp_password")} ${settings.smtpPassword ? `<span class="badge ok">${t("common.set")}</span>` : ""}</label><input id="st-pwd" type="password" placeholder="${settings.smtpPassword ? t("common.leave_blank_to_keep") : ""}"></div></div><div class="form-grid">${valueField("st-to", t("settings.notification.recipients_help"), settings.smtpTo)}${valueField("st-from", t("settings.from_address_blank_account"), settings.smtpFrom)}</div><div class="form-grid">${valueField("st-subject", t("settings.subject_prefix"), settings.smtpSubjectPrefix)}${valueField("st-smtp-timeout", t("settings.timeout_seconds"), settings.smtpTimeout || 30, "number", 'min="1"')}</div></div>
    <div class="modal-footer-inline plain"><button class="ghost" type="button" data-action="test-notify">${t("settings.test_notifications")}</button></div>
  </div>`;
  return body.replaceAll("▾", icon("chevronDown", "icon panel-arrow-icon")).replaceAll("▸", icon("chevronRight", "icon panel-arrow-icon"));
}

function webhookAdvancedMarkup(settings) {
  const type = settings.webhookType || "feishu";
  const hidden = name => type === name ? "" : " hidden";
  return `<div class="webhook-advanced-fields" data-webhook-advanced="feishu"${hidden("feishu")}><div class="form-grid">${valueField("st-feishu-appid", t("settings.feishu_app_id"), settings.feishuAppId || "", "text", "", t("settings.notification.image_credentials"))}<div class="field" data-help="${t("settings.notification.app_secret_keep")}"><label class="field-label" for="st-feishu-secret">Feishu App Secret ${settings.feishuAppSecret ? `<span class="badge ok">${t("common.set")}</span>` : ""}</label><input id="st-feishu-secret" type="password" placeholder="${settings.feishuAppSecret ? t("common.leave_blank_to_keep") : ""}"></div></div></div>
    <div class="webhook-advanced-fields" data-webhook-advanced="slack"${hidden("slack")}><div class="form-grid">${valueField("st-slack-channel", t("settings.slack_channel_id"), settings.slackChannelId || "", "text", "", t("settings.notification.bot_member_help"))}<div class="field" data-help="${t("settings.notification.bot_token_keep")}"><label class="field-label" for="st-slack-token">${t("settings.slack_bot_token")} ${settings.slackBotToken ? `<span class="badge ok">${t("common.set")}</span>` : ""}</label><input id="st-slack-token" type="password" placeholder="${settings.slackBotToken ? t("common.leave_blank_to_keep") : "xoxb-…"}"></div></div></div>
    <div class="webhook-advanced-fields" data-webhook-advanced="dingtalk"${hidden("dingtalk")}><div class="form-grid">${valueField("st-dingtalk-key", t("settings.dingtalk_app_key"), settings.dingTalkAppKey || "")}${valueField("st-dingtalk-robot", t("settings.dingtalk_robot_code"), settings.dingTalkRobotCode || "")}</div><div class="form-grid">${valueField("st-dingtalk-conversation", t("settings.dingtalk_open_conversation_id"), settings.dingTalkOpenConversationId || "")}<div class="field" data-help="${t("settings.notification.app_secret_keep")}"><label class="field-label" for="st-dingtalk-secret">${t("settings.dingtalk_app_secret")} ${settings.dingTalkAppSecret ? `<span class="badge ok">${t("common.set")}</span>` : ""}</label><input id="st-dingtalk-secret" type="password" placeholder="${settings.dingTalkAppSecret ? t("common.leave_blank_to_keep") : ""}"></div></div></div>`;
}

/** 更新区：设置（自动检查/渠道/镜像源）与检查 / 下载 / 应用状态区。 */
function updateSectionMarkup(settings) {
  const checkEnabled = settings.updateCheckEnabled === true;
  const autoEnabled = checkEnabled && settings.updateAutoApplyEnabled === true;
  const autoExtra = `data-flag="st-update-auto" aria-disabled="${checkEnabled ? "false" : "true"}"${checkEnabled ? "" : " disabled"}`;
  return `<div class="update-section">
    <div class="settings-list">${switchControl("st-update-check", t("settings.update.periodic"), t("settings.update.check_schedule"), checkEnabled, "toggle-update-flag", 'data-flag="st-update-check"')}${switchControl("st-update-auto", t("settings.update_automatically_when_idle"), t("settings.update.auto_download_help"), autoEnabled, "toggle-update-flag", autoExtra)}</div>
    <div class="form-grid">${selectField("st-update-channel", t("settings.update_channel"), settings.updateChannel, [{ value: "prerelease", label: t("settings.pre_release") }, { value: "stable", label: t("settings.stable") }])}${valueField("st-update-source", t("settings.mirror_url"), settings.updateSourceUrl, "text", `placeholder="${t("settings.default_github")}"`, t("settings.update.source_default_help"))}</div>
    <div id="update-status-box" class="update-status" data-testid="update-status"></div>
  </div>`;
}

function diagnosticsSettingsMarkup() {
  return `<div class="diagnostics-section">
    <div class="row-actions"><button class="ghost" type="button" data-action="load-diagnostics" data-testid="load-diagnostics">${t("settings.refresh_diagnostics")}</button><button class="ghost" type="button" data-action="export-diagnostics" data-testid="export-diagnostics">${t("settings.diagnostics.export_help")}</button></div>
    <div id="diagnostics-status" class="diagnostics-status" data-testid="diagnostics-status" aria-live="polite"><p class="muted">${t("settings.loading_diagnostics")}</p></div>
  </div>`;
}

function diagnosticStatusLabel(status) {
  return t(`diagnostics.status.${status}`, {}, { pass: "Normal", warn: "Attention", fail: "Failed", skipped: "Skipped" }[status] || status || "Unknown");
}

function diagnosticStatusClass(status) {
  return status === "pass" ? "ok" : status === "fail" ? "bad" : status === "warn" ? "warn" : "muted";
}

function diagnosticCategoryLabel(category) {
  return t(`diagnostics.category.${category}`, {}, {
    host: "Host",
    network: "Network",
    recovery: "Recovery",
    execution: "Run",
    scheduler: "Scheduler",
    plugins: "Plugin",
    dependencies: "Dependencies",
    logs: "Logs",
    diagnostics: "Diagnostics",
  }[category] || category || "Other");
}

function diagnosticCheckLabel(id) {
  const key = String(id || "").replaceAll(".", "_").replaceAll("-", "_");
  const fallback = String(id || "").split(".").map(part => part.replaceAll("-", " ")).join(" · ");
  return t(`diagnostics.check.${key}`, {}, fallback || "Diagnostic check");
}

function diagnosticMessage(code, args, fallback) {
  if (!code) return fallback;
  const value = t(code, args || {}, "");
  if (value !== code) return value;
  if (code.includes(".summary.")) return fallback || t("diagnostics.summary", {}, "Summary");
  if (code.endsWith(".detail")) return t("diagnostics.detail", {}, fallback);
  if (code.endsWith(".remediation")) return t("diagnostics.remediation", {}, fallback);
  return fallback;
}

function renderDiagnostics(data) {
  const box = $("#diagnostics-status");
  if (!box) return;
  const checks = Array.isArray(data?.checks) ? data.checks : [];
  const overall = data?.overallStatus || "warn";
  const attentionCount = checks.filter(check => check?.status === "warn" || check?.status === "fail").length;
  const attentionText = attentionCount
    ? t("settings.diagnostics.attention_summary", { count: attentionCount })
    : t("settings.diagnostics.all_passed_suffix");
  const rows = checks.map(check => {
    const summary = diagnosticMessage(check.summaryCode, check.summaryArgs, diagnosticStatusLabel(check.status));
    const detail = diagnosticMessage(check.detailCode, check.detailArgs, "");
    const remediation = diagnosticMessage(check.remediationCode, check.remediationArgs, "");
    return `<div class="diagnostic-row" role="row" data-diagnostic-status="${esc(check.status || "unknown")}">
      <div class="diagnostic-check-name" role="cell"><div class="diagnostic-check-title-line"><strong class="diagnostic-check-title">${esc(diagnosticCheckLabel(check.id))}</strong><span class="badge muted diagnostic-category-badge">${esc(diagnosticCategoryLabel(check.category))}</span></div><span class="muted diagnostic-check-id mono">${esc(check.id || "")}</span></div>
      <div class="diagnostic-check-status" role="cell"><span class="badge ${diagnosticStatusClass(check.status)}">${esc(diagnosticStatusLabel(check.status))}</span></div>
      <div class="diagnostic-check-info" role="cell"><div class="diagnostic-check-summary"><span class="diagnostic-info-label">${esc(t("diagnostics.summary", {}, "Summary"))}</span><span>${esc(summary)}</span></div>${detail ? `<div class="diagnostic-check-detail"><span class="diagnostic-info-label">${esc(t("diagnostics.detail", {}, "Details"))}</span><span>${esc(detail)}</span></div>` : ""}${remediation ? `<div class="diagnostic-check-remediation"><span class="diagnostic-info-label">${esc(t("diagnostics.remediation", {}, "Recommendation"))}</span><span>${esc(remediation)}</span></div>` : ""}</div>
    </div>`;
  }).join("");
  box.innerHTML = `<div class="diagnostics-overview"><div class="diagnostics-overview-status"><span class="diagnostics-overview-label">${esc(t("diagnostics.overall", {}, "Overall status"))}</span><span class="badge ${diagnosticStatusClass(overall)}">${esc(diagnosticStatusLabel(overall))}</span><span class="muted">v${esc(data?.hostVersion || "")}</span></div><span class="muted diagnostics-overview-meta">${checks.length} ${t("settings.checks")}${attentionText}</span></div><div class="diagnostics-table" role="table" aria-label="${esc(t("settings.diagnostics.system_checks"))}"><div class="diagnostics-table-header" role="row"><span role="columnheader">${t("common.check")}</span><span role="columnheader">${t("common.status")}</span><span role="columnheader">${t("settings.diagnostics")}</span></div>${rows || `<div class="diagnostics-empty" role="row">${esc(t("diagnostics.empty", {}, "No diagnostic results"))}</div>`}</div>`;
  applyTranslations(box);
}

async function loadDiagnostics(token = state.routeToken) {
  const box = $("#diagnostics-status");
  if (!box) return;
  try {
    const data = await api("GET", "/api/diagnostics");
    if (!isCurrent("settings", token)) return;
    renderDiagnostics(data);
  } catch (error) {
    if (box) {
      box.innerHTML = `<p class="callout callout-warning">${esc(error.message || t("settings.diagnostics.load_failed"))}</p>`;
      applyTranslations(box);
    }
  }
}

async function exportDiagnostics() {
  try {
    const result = await api("POST", "/api/diagnostics/export");
    toast(t("settings.diagnostics.exported", { path: result.path || t("settings.generated") }));
  } catch (error) { toast(error.message, "error"); }
}

let updateStatus = null;

async function loadUpdateStatus(token = state.routeToken) {
  try {
    const data = await api("GET", "/api/update/status");
    if (!isCurrent("settings", token)) return;
    updateStatus = data;
    renderUpdateStatus(data);
    notifyAutomaticUpdate(data);
    scheduleUpdateStatusPoll(token, data);
  } catch { /* 状态区保持占位 */ }
}

function notifyAutomaticUpdate(data) {
  if (data?.automation?.autoUpdateEnabled !== true || !data?.available || !data.latest) return;
  const key = `${data.latest}|${data.channel || ""}`;
  if (key === updateAutoNoticeKey) return;
  updateAutoNoticeKey = key;
  toast(t("settings.update.version_available", { version: data.latest }));
}

function updateStatusPollDelay(data = {}) {
  const currentState = data.state || "idle";
  if (currentState === "checking" || currentState === "downloading") return 1000;
  if (data.automation?.waitingForIdle === true) return 5000;
  if (data.automation?.checkEnabled === true) return 60000;
  return null;
}

/** 状态区按当前业务状态刷新：进行中的事务高频刷新，闲时等待低频刷新，普通静态状态停止轮询。 */
function scheduleUpdateStatusPoll(token, hint = updateStatus) {
  const delay = updateStatusPollDelay(hint);
  if (delay === null) return;
  schedule(async () => {
    if (!isCurrent("settings", token)) return;
    try {
      const data = await api("GET", "/api/update/status");
      if (!isCurrent("settings", token)) return;
      updateStatus = data;
      renderUpdateStatus(data);
      notifyAutomaticUpdate(data);
      scheduleUpdateStatusPoll(token, data);
    } catch { /* 服务重启或短暂不可用时结束本轮等待 */ }
  }, delay, "settings", token);
}

/** 状态区渲染：当前版本 / 渠道 / 最新版本与 release note（截断）/ 进度 / 按钮流。 */
function renderUpdateStatus(data) {
  const box = $("#update-status-box");
  if (!box) return;
  if (!data) {
    box.innerHTML = `<p class="muted update-state-copy">${t("settings.update.status_loading")}</p>`;
    applyTranslations(box);
    return;
  }
  const state = data.state || "idle";
  const current = data.current || "—";
  const channelText = data.channel === "stable" ? t("settings.stable") : t("settings.pre_release");
  const actions = updateActionsMarkup(data);
  let progress = "";
  if (state === "downloading" && typeof data.progress === "number") {
    progress = `<div class="progress-line" role="progressbar" aria-valuemin="0" aria-valuemax="100" aria-valuenow="${data.progress}" aria-label="${esc(t("settings.download_progress"))}"><div data-progress="${data.progress}"></div></div>`;
  }
  let notes = "";
  if (data.notes) {
    const text = data.notes.length > 300 ? data.notes.slice(0, 300) + "…" : data.notes;
    notes = `<p class="update-notes">${esc(text)}</p>`;
  }
  let stateText = "";
  if (state === "checking") stateText = `<p class="muted update-state-copy">${t("settings.update.checking")}</p>`;
  else if (state === "downloading") stateText = `<p class="muted update-state-copy">${t(data.automation?.autoUpdateEnabled === true ? "settings.update.download_starting" : "settings.update.downloading")}</p>`;
  else if (state === "ready" && data.automation?.waitingForIdle === true) stateText = `<p class="muted update-state-copy">${t("settings.update.ready_idle")}</p>`;
  else if (state === "ready") stateText = `<p class="muted update-state-copy">${t("settings.update.ready_confirm")}</p>`;
  else if (state === "applypending") stateText = `<p class="muted update-state-copy">${t("settings.update.queued")}</p>`;
  else if (state === "applying") stateText = `<p class="muted update-state-copy">${t("settings.update.applying")}</p>`;
  else if (state === "recoverypending") stateText = `<p class="callout callout-warning">${t("settings.update.recovery_pending_help")}</p>`;
  else if (state === "idle" && data.available) stateText = `<p class="muted update-state-copy">${t("settings.update.version", { version: esc(data.latest), channel: data.prerelease ? t("settings.pre_release") : "" })}</p>`;
  else if (state === "idle" && !data.checked && data.automation?.checkEnabled === true) stateText = `<p class="muted update-state-copy">${t("settings.update.waiting_check")}</p>`;
  else if (state === "idle" && !data.checked) stateText = `<p class="muted update-state-copy">${t("settings.update.not_checked")}</p>`;
  else if (state === "idle" && !data.available && data.errorCode) stateText = `<p class="callout callout-warning">${esc(t(`api.error.${data.errorCode}`, {}, data.errorCode))}</p>`;
  else if (state === "idle") stateText = `<p class="muted update-state-copy">${t("settings.update.latest")}</p>`;
  const idleReason = state === "ready" && data.automation?.waitingForIdle && data.automation.idleBlockCode
    ? `<p class="muted update-state-copy">${t("settings.update.waiting_reason", { reason: esc(t(`settings.update_idle.${data.automation.idleBlockCode}`, {}, data.automation.idleBlockCode)) })}</p>`
    : "";
  const backupWarning = state === "ready"
    ? `<p class="callout callout-warning update-backup-warning" data-testid="update-backup-warning">${t("settings.update.backup_help")}</p>`
    : "";
  box.innerHTML = `<div class="detail"><div class="kv"><span class="k">${t("common.current_version")}</span><span>v${esc(current)}</span></div><div class="kv"><span class="k">${t("settings.update_channel")}</span><span>${channelText}</span></div></div>${notes}${stateText}${idleReason}${backupWarning}${progress}<div class="modal-footer-inline plain update-actions">${actions}</div>`;
  applyTranslations(box);
  box.querySelectorAll("[data-progress]").forEach(element => {
    element.style.width = `${Math.max(0, Math.min(100, Number(element.dataset.progress) || 0))}%`;
  });
}

function updateSettingsPayload() {
  return {
    updateCheckEnabled: $("#st-update-check")?.getAttribute("aria-pressed") === "true",
    updateAutoApplyEnabled: $("#st-update-auto")?.getAttribute("aria-pressed") === "true",
    updateChannel: $("#st-update-channel")?.value || "prerelease",
    updateSourceUrl: ($("#st-update-source")?.value || "").trim(),
  };
}

function syncUpdateToggleState(settings = {}) {
  const checkEnabled = Object.prototype.hasOwnProperty.call(settings, "updateCheckEnabled")
    ? settings.updateCheckEnabled === true
    : $("#st-update-check")?.getAttribute("aria-pressed") === "true";
  const auto = $("#st-update-auto");
  if (!auto) return;
  auto.disabled = !checkEnabled;
  auto.setAttribute("aria-disabled", String(!checkEnabled));
  if (!checkEnabled) {
    auto.setAttribute("aria-pressed", "false");
    auto.dataset.state = "off";
    const stateText = auto.querySelector("[data-switch-state]");
    if (stateText) stateText.textContent = t("common.disabled");
  }
}

async function saveUpdateSettings() {
  const data = await api("PUT", "/api/settings", updateSettingsPayload());
  if (data?.settings) {
    state.settings = data.settings;
    syncUpdateToggleState(data.settings);
  }
  await loadUpdateStatus();
}

/** 分组串行化保存：同一组设置字段的连续修改按顺序落盘，失败只弹一次错误。 */
function createSaveQueue(save) {
  let chain = Promise.resolve();
  return {
    queue() {
      const pending = chain.then(save);
      chain = pending.catch(error => toast(error.message, "error"));
      return pending;
    },
    settled() {
      return chain;
    },
  };
}

const notifySaveQueue = createSaveQueue(saveNotifySettings);
const updateSaveQueue = createSaveQueue(saveUpdateSettings);
const networkSaveQueue = createSaveQueue(saveNetworkSettings);
const queueNotifySave = () => notifySaveQueue.queue();
const queueUpdateSave = () => updateSaveQueue.queue();
const queueNetworkSave = () => networkSaveQueue.queue();
const awaitNotifySaveSettled = () => notifySaveQueue.settled();
const awaitUpdateSaveSettled = () => updateSaveQueue.settled();
const awaitNetworkSaveSettled = () => networkSaveQueue.settled();

async function checkUpdate() {
  try {
    const result = await api("POST", "/api/update/check");
    updateStatus = result;
    renderUpdateStatus(result);
    if (result.state === "checking") toast(t("settings.checking_for_updates"), "info");
    else if (result.available) toast(t("settings.update.version_available", { version: result.latest }));
    else toast(t("common.already_up_to_date"), "info");
    scheduleUpdateStatusPoll(state.routeToken, result);
  } catch (error) { toast(error.message, "error"); }
}

async function startUpdateDownload() {
  try {
    await api("POST", "/api/update/download");
    scheduleUpdateStatusPoll(state.routeToken, { ...(updateStatus || {}), state: "downloading" });
  } catch (error) { toast(error.message, "error"); }
}

async function cancelUpdateDownload() {
  try {
    await api("POST", "/api/update/cancel");
    toast(t("settings.download_cancelled"));
    await loadUpdateStatus();
  } catch (error) { toast(error.message, "error"); }
}

function confirmUpdateApply(defer) {
  const version = updateStatus?.latest ? ` v${esc(updateStatus.latest)}` : "";
  const actionText = defer ? t("settings.update.apply_next_start") : t("settings.update.apply_now");
  confirmModal(
    defer ? t("common.update_on_next_startup") : t("common.update_now"),
    t("settings.update.backup_confirm", { action: actionText, version }),
    "update-apply-confirm",
    { defer: defer ? "true" : "false" },
  );
}

async function applyUpdate(defer) {
  try {
    const result = await api("POST", "/api/update/apply", { defer });
    if (result.error) {
      toast(result.error, "error");
      if (result.code === "busy") toast(t("settings.update.busy_help"), "info");
      await loadUpdateStatus();
      return;
    }
    if (result.deferred) {
      toast(t("settings.update.queued_service_start"));
      await loadUpdateStatus();
      return;
    }
    renderUpdateStatus({ ...(updateStatus || {}), state: "applying" });
    showModal(modalShell(t("settings.applying_update"), `<p class="modal-copy">${t("settings.update_apply_started")}</p>`), false, true);
    pollServiceRestart(Date.now() + 120000);
  } catch (error) { toast(error.message, "error"); }
}

/** 更新应用后轮询服务恢复（相对路径探测当前源），恢复后刷新页面。 */
function pollServiceRestart(deadline) {
  schedule(async () => {
    try {
      const probe = await fetch("api/status", { cache: "no-store" });
      if (probe.ok) {
        location.reload();
        return;
      }
    } catch { /* 服务未就绪 */ }
    if (Date.now() < deadline) pollServiceRestart(deadline);
    else {
      closeModal();
      toast(t("settings.service.restart_timeout"), "error");
    }
  }, 1000, "settings", state.routeToken);
}

function togglePanel(panelId, trigger) {
  const panel = $("#" + panelId); if (!panel) return;
  const hidden = panel.hasAttribute("hidden");
  panel.toggleAttribute("hidden", !hidden);
  if (trigger) {
    trigger.setAttribute("aria-expanded", String(hidden));
    const arrow = trigger.querySelector(".panel-arrow");
    if (arrow) arrow.innerHTML = icon(hidden ? "chevronDown" : "chevronRight", "icon panel-arrow-icon");
  }
}

function toggleWebhookFields() {
  const box = $("#st-whtpl-box"); if (!box) return;
  box.toggleAttribute("hidden", $("#st-whtype")?.value !== "generic");
  const type = $("#st-whtype")?.value || "";
  document.querySelectorAll("[data-webhook-advanced]").forEach(field => {
    field.toggleAttribute("hidden", field.dataset.webhookAdvanced !== type);
  });
}

async function saveNotifySettings() {
  const payload = {
    webhookEnabled: $("#st-wh-enabled")?.getAttribute("aria-pressed") === "true",
    webhookScreenshotEnabled: $("#st-wh-screenshot")?.getAttribute("aria-pressed") === "true",
    smtpEnabled: $("#st-smtp-enabled")?.getAttribute("aria-pressed") === "true",
    smtpScreenshotEnabled: $("#st-smtp-screenshot")?.getAttribute("aria-pressed") === "true",
    webhookType: $("#st-whtype")?.value || "generic",
    webhookTimeout: +($("#st-whtimeout")?.value || 30),
    webhookTemplate: $("#st-whtpl")?.value.trim() || "",
    feishuAppId: $("#st-feishu-appid")?.value.trim() || "",
    slackChannelId: $("#st-slack-channel")?.value.trim() || "",
    dingTalkAppKey: $("#st-dingtalk-key")?.value.trim() || "",
    dingTalkRobotCode: $("#st-dingtalk-robot")?.value.trim() || "",
    dingTalkOpenConversationId: $("#st-dingtalk-conversation")?.value.trim() || "",
    smtpHost: $("#st-host")?.value.trim() || "",
    smtpPort: +($("#st-port2")?.value || 465),
    smtpSecure: $("#st-secure")?.value || "auto",
    smtpUser: $("#st-user")?.value.trim() || "",
    smtpTo: $("#st-to")?.value.trim() || "",
    smtpFrom: $("#st-from")?.value.trim() || "",
    smtpSubjectPrefix: $("#st-subject")?.value.trim() || "",
    smtpTimeout: +($("#st-smtp-timeout")?.value || 30),
  };
  const secrets = [
    ["webhookUrl", $("#st-whurl")?.value.trim() || "", "st-whurl"],
    ["webhookSecret", $("#st-whsec")?.value.trim() || "", "st-whsec"],
    ["feishuAppSecret", $("#st-feishu-secret")?.value.trim() || "", "st-feishu-secret"],
    ["slackBotToken", $("#st-slack-token")?.value.trim() || "", "st-slack-token"],
    ["dingTalkAppSecret", $("#st-dingtalk-secret")?.value.trim() || "", "st-dingtalk-secret"],
    ["smtpPassword", $("#st-pwd")?.value.trim() || "", "st-pwd"],
  ].filter(([, value]) => value.length > 0);
  let data = await api("PUT", "/api/settings", payload);
  for (const [key, value] of secrets) data = await api("PUT", "/api/settings", { secretKey: key, secretValue: value });
  if (data?.settings) {
    state.settings = data.settings;
    syncNotificationBadges(data.settings);
  }
  for (const [, value, id] of secrets) {
    const input = $("#" + id);
    if (input?.value.trim() === value) input.value = "";
  }
}

function networkSettingsPayload() {
  return {
    proxyMode: $("#st-proxy-mode")?.value || "none",
    proxyUrl: ($("#st-proxy-url")?.value || "").trim(),
    proxyUsername: ($("#st-proxy-user")?.value || "").trim(),
  };
}

async function saveNetworkSettings() {
  const password = ($("#st-proxy-pwd")?.value || "").trim();
  let data = await api("PUT", "/api/settings", networkSettingsPayload());
  if (password) {
    data = await api("PUT", "/api/settings", { secretKey: "proxyPassword", secretValue: password });
    const input = $("#st-proxy-pwd");
    if (input && input.value.trim() === password) input.value = "";
  }
  if (data?.settings) state.settings = data.settings;
}

function toggleProxyFields() {
  const mode = $("#st-proxy-mode")?.value || "none";
  const box = $("#st-proxy-custom");
  if (box) box.hidden = mode !== "http";
  // 空地址时让用户先填写地址；地址存在时模式选择可即时保存。
  if (mode !== "http" || ($("#st-proxy-url")?.value || "").trim()) {
    queueNetworkSave();
  }
}

function syncNotificationBadges(settings) {
  const badges = [
    ["panel-wh", settings.webhookEnabled],
    ["panel-smtp", settings.smtpEnabled],
  ];
  for (const [panelId, enabled] of badges) {
    const badge = document.querySelector(`[data-panel="${panelId}"] .badge`);
    if (!badge) continue;
    badge.classList.toggle("ok", enabled);
    badge.classList.toggle("muted", !enabled);
    badge.textContent = enabled ? t("common.enabled") : t("common.disabled");
  }
}

async function testNotify() {
  await awaitNotifySaveSettled();
  try {
    const result = await api("POST", "/api/settings/test");
    toast(result.ok ? t("settings.notification.test_success") : t("settings.notification.send_failed"), result.ok ? "info" : "error");
  } catch (error) { toast(error.message, "error"); }
}

export function markRestartRequired() {
  restartRequired = true;
  if (state.page !== "settings") return;
  const view = document.querySelector("#view");
  const lightweight = $("#st-lightweight")?.getAttribute("aria-pressed") === "true";
  const markup = restartNoticeMarkup({ ...(state.settings || {}), lightweightMode: lightweight });
  const existing = document.querySelector("#restart-notice");
  if (existing) {
    existing.outerHTML = markup;
    applyTranslations(document.querySelector("#restart-notice"));
    return;
  }
  if (!view) return;
  const header = view.querySelector(".page-head");
  header?.insertAdjacentHTML("afterend", markup);
  applyTranslations(document.querySelector("#restart-notice"));
}

/** 自动保存串行链（用户需求：修改一次即保存一次，成功静默、失败 toast）：连续触发（快速切换开关）
 *  串行执行，避免并发 PUT 乱序覆盖；重启/离开前可 await 等待链完成。 */
let saveChain = Promise.resolve();

export function awaitSaveSettled() {
  return saveChain;
}

function autoSave() {
  const save = saveChain.then(() => doSave());
  saveChain = save.catch(error => toast(error.message, "error"));
  return save;
}

/** 收集当前控件值并 PUT 保存；成功后更新内存状态（不重渲染页面），远程开关变化后局部刷新地址列表。 */
async function doSave() {
  const token = ($("#st-token")?.value || "").trim();
  const payload = {
    autoStart: $("#st-autostart")?.getAttribute("aria-pressed") === "true",
    lightweightMode: $("#st-lightweight")?.getAttribute("aria-pressed") === "true",
    autoOpenBrowser: $("#st-browser")?.getAttribute("aria-pressed") === "true",
    historyRetentionDays: +($("#st-retention")?.value || 3),
    webPort: +($("#st-port")?.value || 58731),
    mcpEnabled: $("#st-mcp-enabled")?.getAttribute("aria-pressed") === "true",
    mcpPort: +($("#st-mcp-port")?.value || 58732),
    logLevel: $("#st-loglevel")?.value || "info",
    hostLocale: $("#st-host-locale")?.value || "zh-CN",
    allowRemoteAccess: $("#st-remote")?.getAttribute("aria-pressed") === "true",
  };
  if (token) {
    payload.secretKey = "accessToken";
    payload.secretValue = token;
  }
  const data = await api("PUT", "/api/settings", payload);
  state.settings = data.settings;
  await refreshLanList();
}

/** 局部刷新局域网地址列表（远程访问开关切换后地址随之变化；失败静默保持旧内容）。 */
async function refreshLanList() {
  const box = $("#remote-lan-list");
  if (!box) return;
  try {
    const data = await api("GET", "/api/settings");
    state.settings = data.settings;
    const lan = (data.status && data.status.remote && data.status.remote.lanAddresses) || [];
    const remoteEnabled = data.settings.allowRemoteAccess === true;
    box.innerHTML = remoteEnabled && lan.length
      ? lan.map(addr => `<div class="kv"><span class="k">${t("settings.lan_address")}</span><span>http://${esc(addr)}:${data.settings.webPort}/</span></div>`).join("")
      : "";
    applyTranslations(box);
    if (remoteEnabled) box.dataset.help = t("settings.remote_access.lan_help");
    else delete box.dataset.help;
  } catch { /* 静默 */ }
}

/** 设置页控件自动保存绑定：服务行为沿用 change 保存，通知与更新设置按输入失焦或下拉 change 保存。 */
function bindAutoSave() {
  ["st-loglevel", "st-host-locale", "st-retention", "st-port", "st-mcp-port", "st-token"].forEach(id => {
    $("#" + id)?.addEventListener("change", () => {
      if (["st-port", "st-mcp-port"].includes(id)) markRestartRequired();
      autoSave();
    });
  });
  bindSettingsFields(["st-whtype", "st-whtimeout", "st-whurl", "st-whsec", "st-feishu-appid", "st-feishu-secret", "st-slack-channel", "st-slack-token", "st-dingtalk-key", "st-dingtalk-secret", "st-dingtalk-robot", "st-dingtalk-conversation", "st-whtpl", "st-host", "st-port2", "st-secure", "st-user", "st-pwd", "st-to", "st-from", "st-subject", "st-smtp-timeout"], queueNotifySave);
  bindSettingsFields(["st-update-channel", "st-update-source"], queueUpdateSave);
  bindSettingsFields(["st-proxy-url", "st-proxy-user", "st-proxy-pwd"], queueNetworkSave);
}

function bindSettingsFields(ids, handler) {
  ids.forEach(id => {
    const field = $("#" + id);
    if (!field) return;
    field.addEventListener(field.tagName === "SELECT" ? "change" : "blur", handler);
  });
}

/** 重启服务：等待挂起的自动保存完成后弹确认卡片（端口改动已即时保存，无需再校验）。 */
export async function restartService() {
  await Promise.all([awaitSaveSettled(), awaitNotifySaveSettled(), awaitUpdateSaveSettled(), awaitNetworkSaveSettled()]);
  confirmModal(t("settings.restart_service"), t("settings.restart_warning"), "restart-confirm");
}

export async function restartConfirmed() {
  let newPort = 0;
  try {
    const res = await api("POST", "/api/settings/restart");
    newPort = (res && res.newPort) || 0;
  } catch (error) {
    toast(error.message, "error");
    return;
  }
  const currentPort = Number(location.port || (location.protocol === "http:" ? 80 : 443));
  const candidates = [];
  for (const port of [currentPort, newPort, newPort + 1]) {
    if (port > 0 && !candidates.includes(port)) candidates.push(port);
  }
  showModal(modalShell(t("settings.service_restarting"), `<p class="modal-copy">${t("settings.service_restarting")}</p>`), false, true);
  pollRestart(candidates, Date.now() + 60000);
}

/** 每 1 秒探测候选端口（当前端口 / 保存端口 / 端口漂移 +1 补偿），服务恢复后刷新页面或跳转到新端口；60 秒超时提示手动刷新。 */
function pollRestart(candidates, deadline) {
  schedule(async () => {
    const headers = {};
    // 存储不可用（隐私模式）时按无令牌处理，避免 getItem 抛异常中断重启轮询。
    let token = null;
    try {
      token = localStorage.getItem("nexus-token");
    } catch {
      token = null;
    }
    if (token) headers["Authorization"] = "Bearer " + token;
    for (const port of candidates) {
      try {
        const probe = new URL(location.href);
        probe.port = String(port);
        probe.pathname = "/api/status";
        probe.search = "";
        probe.hash = "";
        const res = await fetch(probe, { cache: "no-store", headers });
        if (res.ok) {
          if (port === Number(location.port || (location.protocol === "http:" ? 80 : 443))) {
            location.reload();
          } else {
            const target = new URL(location.href);
            target.port = String(port);
            target.pathname = "/";
            target.search = "";
            target.hash = "#/settings";
            location.href = target.href;
          }
          return;
        }
      } catch { /* 服务未就绪，继续轮询 */ }
    }
    if (Date.now() < deadline) {
      pollRestart(candidates, deadline);
    } else {
      closeModal();
      toast(t("settings.service.restart_timeout"), "error");
    }
  }, 1000, "settings", state.routeToken);
}

export const actions = {
  "restart-service": () => restartService(),
  "restart-confirm": target => withBusy(target, () => restartConfirmed()),
  "toggle-st-flag": target => {
    const btn = $("#" + target.dataset.flag);
    if (!btn) return;
    const pressed = btn.getAttribute("aria-pressed") !== "true";
    btn.setAttribute("aria-pressed", pressed ? "true" : "false");
    btn.dataset.state = pressed ? "on" : "off";
    const stateText = btn.querySelector("[data-switch-state]");
    if (stateText) stateText.textContent = pressed ? t("common.enabled") : t("common.disabled");
    if (target.dataset.restartRequired === "true" || ["st-lightweight", "st-remote", "st-mcp-enabled"].includes(target.dataset.flag)) markRestartRequired();
    autoSave();
  },
  "toggle-token-visibility": target => {
    const input = $("#st-token");
    if (!input) return;
    const visible = input.type === "password";
    input.type = visible ? "text" : "password";
    target.setAttribute("aria-pressed", String(visible));
    target.textContent = visible ? t("settings.hide") : t("common.show");
  },
  "copy-token": async target => {
    const input = $("#st-token");
    const value = input?.value?.trim();
    if (!value) { toast(t("settings.there_is_no_token_to_copy"), "error"); return; }
    try {
      await navigator.clipboard.writeText(value);
      toast(t("settings.access_token_copied"));
    } catch (error) {
      toast(t("settings.remote_access.copy_failed"), "error");
    }
  },
  "gen-token": () => {
    const bytes = new Uint8Array(24);
    crypto.getRandomValues(bytes);
    const hex = Array.from(bytes, b => b.toString(16).padStart(2, "0")).join("");
    const input = $("#st-token");
    if (input) {
      input.value = hex;
      input.type = "password";
    }
    toast(t("settings.remote_access.token_generated"));
    void autoSave().then(() => toast(t("settings.access_token_saved")), () => {});
  },
  "toggle-settings-panel": target => toggleSettingsPanel(target.dataset.panel),
  "change-locale": target => changeLocale(target),
  "load-diagnostics": target => withBusy(target, () => loadDiagnostics()),
  "export-diagnostics": target => withBusy(target, () => exportDiagnostics()),
  "toggle-panel": target => togglePanel(target.dataset.panel, target),
  "toggle-webhook-fields": () => toggleWebhookFields(),
  "toggle-generic-template": () => toggleWebhookFields(),
  "toggle-proxy-fields": () => toggleProxyFields(),
  "toggle-notify-flag": target => {
    const btn = $("#" + target.dataset.flag);
    if (btn) {
      btn.setAttribute("aria-pressed", btn.getAttribute("aria-pressed") === "true" ? "false" : "true");
      queueNotifySave();
    }
  },
  "test-notify": target => withBusy(target, () => testNotify()),
  "update-check": target => withBusy(target, () => checkUpdate()),
  "update-download": target => withBusy(target, () => startUpdateDownload()),
  "update-cancel": () => cancelUpdateDownload(),
  "update-apply": () => confirmUpdateApply(false),
  "update-defer": () => confirmUpdateApply(true),
  "update-apply-confirm": target => {
    closeModal();
    return withBusy(target, () => applyUpdate(target.dataset.defer === "true"));
  },
  "toggle-update-flag": target => {
    const btn = $("#" + target.dataset.flag);
    if (!btn || btn.disabled) return;
    const pressed = btn.getAttribute("aria-pressed") !== "true";
    btn.setAttribute("aria-pressed", pressed ? "true" : "false");
    btn.dataset.state = pressed ? "on" : "off";
    const stateText = btn.querySelector("[data-switch-state]");
    if (stateText) stateText.textContent = pressed ? t("common.enabled") : t("common.disabled");
    if (target.dataset.flag === "st-update-check" && !pressed) {
      const auto = $("#st-update-auto");
      if (auto) {
        auto.setAttribute("aria-pressed", "false");
        auto.dataset.state = "off";
        const autoState = auto.querySelector("[data-switch-state]");
        if (autoState) autoState.textContent = t("common.disabled");
      }
    }
    syncUpdateToggleState({ updateCheckEnabled: pressed });
    queueUpdateSave();
  },
};
