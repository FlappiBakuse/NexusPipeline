import { esc } from "./format.js";
import { numberControlMarkup, pathControlMarkup, selectControlMarkup, timeControlMarkup } from "./controls.js";

export function pageHeader(kicker, title, description, action = "", extraClass = "") {
  return `<header class="page-head${extraClass ? ` ${esc(extraClass)}` : ""}"><div class="page-head-copy">${kicker ? `<div class="eyebrow">${kicker}</div>` : ""}<h2>${title}</h2>${description ? `<p class="page-kicker">${description}</p>` : ""}</div>${action ? `<div class="page-head-actions">${action}</div>` : ""}</header>`;
}

function fieldErrorSlot(id) {
  return `<p id="${id}-error" class="field-error-message" role="alert" hidden></p>`;
}

function fieldHelp(help) {
  return help ? ` data-help="${esc(help)}"` : "";
}

export function valueField(id, label, value, type = "text", extra = "", help = "") {
  const control = type === "number"
    ? numberControlMarkup(id, value, extra, String(label).replace(/<[^>]*>/g, ""))
    : type === "time"
      ? timeControlMarkup(id, value, extra, String(label).replace(/<[^>]*>/g, ""))
      : `<input id="${id}" type="${type}" value="${esc(value)}" ${extra}>`;
  return `<div class="field"${fieldHelp(help)}><label class="field-label" for="${id}">${label}</label>${control}${fieldErrorSlot(id)}</div>`;
}

/** 多行文本填写框：label 在上，正文 textarea，与单行字段同构。 */
export function textareaField(id, label, value, extra = "", placeholder = "", help = "") {
  return `<div class="field"${fieldHelp(help)}><label class="field-label" for="${id}">${label}</label><textarea id="${id}" class="form-textarea" ${placeholder ? `placeholder="${esc(placeholder)}"` : ""} ${extra}>${esc(value)}</textarea>${fieldErrorSlot(id)}</div>`;
}

/** 长提示输入框：原生 placeholder 超出宽度会被裁剪，改用输入框内滚动提示浮层（空值且未聚焦时显示）。 */
export function scrollField(id, label, value, placeholder = "") {
  return `<div class="field"><label class="field-label" for="${id}">${label}</label><div class="input-scroll">
    <input id="${id}" type="text" value="${esc(value)}">
    <span class="scroll-text input-scroll-hint"><span class="scroll-inner">${esc(placeholder)}</span></span>
  </div>${fieldErrorSlot(id)}</div>`;
}

export function selectField(id, label, value, options, extra = "", help = "") {
  // option 的 value 与文本经 esc 转义（此前值含引号/尖括号会破坏 HTML 结构）。
  return `<div class="field"${fieldHelp(help)}><label class="field-label" for="${id}-trigger">${label}</label>${selectControlMarkup(id, value, options, extra, String(label).replace(/<[^>]*>/g, ""))}${fieldErrorSlot(id)}</div>`;
}

/** 本机文件/文件夹路径字段：选择按钮只负责回填，文本框始终保留手工编辑能力。 */
export function pathField(id, label, value, kind = "file", extra = "", filter = "", triggerExtra = "", help = "") {
  const ariaLabel = String(label).replace(/<[^>]*>/g, "");
  return `<div class="field"${fieldHelp(help)}><label class="field-label" for="${id}">${label}</label>${pathControlMarkup(id, value, kind, extra, ariaLabel, filter, triggerExtra)}${fieldErrorSlot(id)}</div>`;
}

/** 标准布尔开关：状态由 aria-pressed 表达，视觉层不再依赖「开/关」文案。 */
export function switchControl(id, label, description, pressed, action, extra = "", ariaLabel = "") {
  const accessibleLabel = ariaLabel || String(label || "").replace(/<[^>]*>/g, "");
  const descriptionText = String(description || "").trim();
  const descriptionId = descriptionText ? `${id}-description` : "";
  const describedBy = descriptionId ? ` aria-describedby="${esc(descriptionId)}"` : "";
  const descriptionMarkup = descriptionText ? `<span id="${esc(descriptionId)}" class="muted">${esc(descriptionText)}</span>` : "";
  return `<div class="switch-row settings-option switch-card" data-switch-row="${esc(id)}">
    <div class="switch-copy"><strong>${label}</strong>${descriptionMarkup}</div>
    <button id="${esc(id)}" class="mode-toggle switch-control" type="button" aria-label="${esc(accessibleLabel)}"${describedBy} aria-pressed="${pressed ? "true" : "false"}" data-state="${pressed ? "on" : "off"}" data-toggle-text="false" data-action="${esc(action)}" ${extra}><span class="switch-track" aria-hidden="true"><span class="switch-thumb"></span></span><span class="sr-only" data-switch-state>${pressed ? "已启用" : "已停用"}</span></button>
  </div>`;
}

/** 完成操作倒计时卡片：队列全部完成后 60 秒倒计时窗口，可取消；无待执行操作返回空串。 */
export function systemActionCard(action) {
  // 退出软件在协调器中立即执行，不展示可取消的倒计时卡片。
  if (!action || action.action === "exit") return "";
  const verb = action.action === "sleep" ? "休眠" : action.action === "reboot" ? "重启" : "关机";
  return `<section class="card section-surface system-action-card" role="status" aria-live="polite" data-testid="system-action-card" data-action-verb="${verb}">
    <div class="section-heading"><h3>完成操作倒计时</h3><span class="muted">队列已完成，等待执行系统操作</span></div>
    <p class="countdown-text">调度队列「${esc(action.queueName || "")}」已完成，<strong data-testid="system-action-countdown" data-deadline="${esc(action.deadline || "")}"></strong></p>
    <div class="qk-row"><button class="danger" type="button" data-action="cancel-system-action">取消${verb}</button></div>
  </section>`;
}
