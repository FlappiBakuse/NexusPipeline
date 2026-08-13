import { esc } from "./format.js";

export function pageHeader(kicker, title, description, action = "") {
  return `<div class="page-head"><div><div class="eyebrow">${kicker}</div><h2>${title}</h2>${description ? `<p class="page-kicker">${description}</p>` : ""}</div>${action}</div>`;
}

export function valueField(id, label, value, type = "text", extra = "") {
  return `<div><label class="field-label" for="${id}">${label}</label><input id="${id}" type="${type}" value="${esc(value)}" ${extra}></div>`;
}

/** 长提示输入框：原生 placeholder 超出宽度会被裁剪，改用输入框内滚动提示浮层（空值且未聚焦时显示）。 */
export function scrollField(id, label, value, placeholder = "") {
  return `<div><label class="field-label" for="${id}">${label}</label><div class="input-scroll">
    <input id="${id}" type="text" value="${esc(value)}">
    <span class="scroll-text input-scroll-hint"><span class="scroll-inner">${esc(placeholder)}</span></span>
  </div></div>`;
}

export function selectField(id, label, value, options, extra = "") {
  return `<div><label class="field-label" for="${id}">${label}</label><select id="${id}" ${extra}>${options.map(option => { const v = typeof option === "string" ? option : option.value; const t = typeof option === "string" ? option : option.label; return `<option value="${v}" ${v === value ? "selected" : ""}>${t}</option>`; }).join("")}</select></div>`;
}

/** 完成操作倒计时卡片（v0.6.3+）：队列全部完成后 60 秒倒计时窗口，可取消；无待执行操作返回空串。 */
export function systemActionCard(action) {
  if (!action) return "";
  const verb = action.action === "sleep" ? "休眠" : action.action === "reboot" ? "重启" : "关机";
  return `<section class="card system-action-card" role="status" aria-live="polite" data-testid="system-action-card" data-action-verb="${verb}">
    <div class="section-heading"><h3>完成操作倒计时</h3><span class="muted">队列已完成，等待执行系统操作</span></div>
    <p class="countdown-text">调度队列「${esc(action.queueName || "")}」已完成，<strong data-testid="system-action-countdown" data-deadline="${esc(action.deadline || "")}"></strong></p>
    <div class="qk-row"><button type="button" data-action="cancel-system-action">取消${verb}</button></div>
  </section>`;
}
