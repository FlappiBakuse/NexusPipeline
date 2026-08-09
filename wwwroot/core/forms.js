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
