import { esc } from "./format.js";
import { numberControlMarkup, pathControlMarkup, selectControlMarkup, timeControlMarkup } from "./controls.js";
import { t } from "./i18n.js";

export function pageHeader(kicker, title, description, action = "", extraClass = "") {
  return `<header class="page-head${extraClass ? ` ${esc(extraClass)}` : ""}"><div class="page-head-copy">${kicker ? `<div class="eyebrow">${t(kicker)}</div>` : ""}<h2>${t(title)}</h2>${description ? `<p class="page-kicker">${t(description)}</p>` : ""}</div>${action ? `<div class="page-head-actions">${action}</div>` : ""}</header>`;
}

function fieldErrorSlot(id) {
  return `<p id="${id}-error" class="field-error-message" role="alert" hidden></p>`;
}

function localizedText(value) {
  const text = String(value ?? "");
  return t(text, {}, text);
}

function localizedLabel(value) {
  const raw = String(value ?? "");
  const required = raw.match(/\s*(<span\s+class=['"]req['"][^>]*>.*?<\/span>)/iu);
  const plain = required ? raw.replace(required[0], "").trim() : raw.replace(/<[^>]*>/g, "").trim();
  const translated = localizedText(plain);
  return required ? `${translated} ${required[1]}` : translated;
}

function fieldHelp(help) {
  const translated = localizedText(help);
  return translated ? ` data-help="${esc(translated)}"` : "";
}

export function valueField(id, label, value, type = "text", extra = "", help = "") {
  const displayLabel = localizedLabel(label);
  const control = type === "number"
    ? numberControlMarkup(id, value, extra, String(displayLabel).replace(/<[^>]*>/g, ""))
    : type === "time"
      ? timeControlMarkup(id, value, extra, String(displayLabel).replace(/<[^>]*>/g, ""))
      : `<input id="${id}" type="${type}" value="${esc(value)}" ${extra}>`;
  return `<div class="field"${fieldHelp(help)}><label class="field-label" for="${id}">${displayLabel}</label>${control}${fieldErrorSlot(id)}</div>`;
}

/** 多行文本填写框：label 在上，正文 textarea，与单行字段同构。 */
export function textareaField(id, label, value, extra = "", placeholder = "", help = "") {
  const displayLabel = localizedLabel(label);
  return `<div class="field"${fieldHelp(help)}><label class="field-label" for="${id}">${displayLabel}</label><textarea id="${id}" class="form-textarea" ${placeholder ? `placeholder="${esc(localizedText(placeholder))}"` : ""} ${extra}>${esc(value)}</textarea>${fieldErrorSlot(id)}</div>`;
}

/** 长提示输入框：原生 placeholder 超出宽度会被裁剪，改用输入框内滚动提示浮层（空值且未聚焦时显示）。 */
export function scrollField(id, label, value, placeholder = "") {
  const displayLabel = localizedLabel(label);
  return `<div class="field"><label class="field-label" for="${id}">${displayLabel}</label><div class="input-scroll">
    <input id="${id}" type="text" value="${esc(value)}">
    <span class="scroll-text input-scroll-hint"><span class="scroll-inner">${esc(localizedText(placeholder))}</span></span>
  </div>${fieldErrorSlot(id)}</div>`;
}

export function selectField(id, label, value, options, extra = "", help = "") {
  // option 的 value 与文本经 esc 转义（此前值含引号/尖括号会破坏 HTML 结构）。
  const displayLabel = localizedLabel(label);
  return `<div class="field"${fieldHelp(help)}><label class="field-label" for="${id}-trigger">${displayLabel}</label>${selectControlMarkup(id, value, options, extra, String(displayLabel).replace(/<[^>]*>/g, ""))}${fieldErrorSlot(id)}</div>`;
}

/** 本机文件/文件夹路径字段：选择按钮只负责回填，文本框始终保留手工编辑能力。 */
export function pathField(id, label, value, kind = "file", extra = "", filter = "", triggerExtra = "", help = "") {
  const displayLabel = localizedLabel(label);
  const ariaLabel = String(displayLabel).replace(/<[^>]*>/g, "");
  return `<div class="field"${fieldHelp(help)}><label class="field-label" for="${id}">${displayLabel}</label>${pathControlMarkup(id, value, kind, extra, ariaLabel, filter, triggerExtra)}${fieldErrorSlot(id)}</div>`;
}

/** 标准布尔开关：状态由 aria-pressed 表达，视觉层不再依赖「开/关」文案。 */
export function switchControl(id, label, description, pressed, action, extra = "", ariaLabel = "") {
  const displayLabel = localizedLabel(label);
  const accessibleLabel = ariaLabel || String(displayLabel || "").replace(/<[^>]*>/g, "");
  const descriptionText = localizedText(description).trim();
  const descriptionId = descriptionText ? `${id}-description` : "";
  const describedBy = descriptionId ? ` aria-describedby="${esc(descriptionId)}"` : "";
  const descriptionMarkup = descriptionText ? `<span id="${esc(descriptionId)}" class="muted">${esc(descriptionText)}</span>` : "";
  return `<div class="switch-row settings-option switch-card" data-switch-row="${esc(id)}">
    <div class="switch-copy"><strong>${displayLabel}</strong>${descriptionMarkup}</div>
    <button id="${esc(id)}" class="mode-toggle switch-control" type="button" aria-label="${esc(accessibleLabel)}"${describedBy} aria-pressed="${pressed ? "true" : "false"}" data-state="${pressed ? "on" : "off"}" data-toggle-text="false" data-action="${esc(action)}" ${extra}><span class="switch-track" aria-hidden="true"><span class="switch-thumb"></span></span><span class="sr-only" data-switch-state>${t(pressed ? "common.enabled" : "common.disabled")}</span></button>
  </div>`;
}

/** 完成操作倒计时卡片：队列全部完成后 60 秒倒计时窗口，可取消；无待执行操作返回空串。 */
export function systemActionCard(action) {
  // 退出软件在协调器中立即执行，不展示可取消的倒计时卡片。
  if (!action || action.action === "exit") return "";
  const verb = t(action.action === "sleep" ? "common.sleep" : action.action === "reboot" ? "common.restart" : "common.shut_down");
  return `<section class="card section-surface system-action-card" role="status" aria-live="polite" data-testid="system-action-card" data-action-verb="${esc(verb)}">
    <div class="section-heading"><h3>${t("common.completion_action_countdown")}</h3><span class="muted">${t("common.status.queue_action_pending")}</span></div>
    <p class="countdown-text">${t("common.status.queue_complete", { queueName: esc(action.queueName || "") })}<strong data-testid="system-action-countdown" data-deadline="${esc(action.deadline || "")}"></strong></p>
    <div class="qk-row"><button class="danger" type="button" data-action="cancel-system-action">${t("common.action.cancel_verb", { verb })}</button></div>
  </section>`;
}
