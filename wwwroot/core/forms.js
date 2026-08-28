import { esc } from "./format.js";

export function pageHeader(kicker, title, description, action = "") {
  return `<header class="page-head"><div class="page-head-copy">${kicker ? `<div class="eyebrow">${kicker}</div>` : ""}<h2>${title}</h2>${description ? `<p class="page-kicker">${description}</p>` : ""}</div>${action ? `<div class="page-head-actions">${action}</div>` : ""}</header>`;
}

function fieldErrorSlot(id) {
  return `<p id="${id}-error" class="field-error-message" role="alert" hidden></p>`;
}

export function valueField(id, label, value, type = "text", extra = "") {
  return `<div class="field"><label class="field-label" for="${id}">${label}</label><input id="${id}" type="${type}" value="${esc(value)}" ${extra}>${fieldErrorSlot(id)}</div>`;
}

/** 多行文本填写框：label 在上，正文 textarea，与单行字段同构。 */
export function textareaField(id, label, value, extra = "", placeholder = "") {
  return `<div class="field"><label class="field-label" for="${id}">${label}</label><textarea id="${id}" class="form-textarea" ${placeholder ? `placeholder="${esc(placeholder)}"` : ""} ${extra}>${esc(value)}</textarea>${fieldErrorSlot(id)}</div>`;
}

/** 长提示输入框：原生 placeholder 超出宽度会被裁剪，改用输入框内滚动提示浮层（空值且未聚焦时显示）。 */
export function scrollField(id, label, value, placeholder = "") {
  return `<div class="field"><label class="field-label" for="${id}">${label}</label><div class="input-scroll">
    <input id="${id}" type="text" value="${esc(value)}">
    <span class="scroll-text input-scroll-hint"><span class="scroll-inner">${esc(placeholder)}</span></span>
  </div>${fieldErrorSlot(id)}</div>`;
}

export function selectField(id, label, value, options, extra = "") {
  // option 的 value 与文本经 esc 转义（此前值含引号/尖括号会破坏 HTML 结构）。
  return `<div class="field"><label class="field-label" for="${id}">${label}</label><select id="${id}" ${extra}>${options.map(option => { const v = typeof option === "string" ? option : option.value; const t = typeof option === "string" ? option : option.label; return `<option value="${esc(v)}" ${v === value ? "selected" : ""}>${esc(t)}</option>`; }).join("")}</select>${fieldErrorSlot(id)}</div>`;
}

/** 标准布尔开关：状态由 aria-pressed 表达，视觉层不再依赖「开/关」文案。 */
export function switchControl(id, label, description, pressed, action, extra = "", ariaLabel = "") {
  const accessibleLabel = ariaLabel || String(label || "").replace(/<[^>]*>/g, "");
  return `<div class="switch-row settings-option" data-switch-row="${id}">
    <div class="switch-copy"><strong>${label}</strong>${description ? `<span class="muted">${description}</span>` : ""}</div>
    <button id="${id}" class="mode-toggle switch-control" type="button" aria-label="${esc(accessibleLabel)}" aria-pressed="${pressed ? "true" : "false"}" data-state="${pressed ? "on" : "off"}" data-toggle-text="false" data-action="${action}" ${extra}><span class="switch-track" aria-hidden="true"><span class="switch-thumb"></span></span><span class="sr-only" data-switch-state>${pressed ? "已启用" : "已停用"}</span></button>
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
