import { esc } from "./format.js";

export function pageHeader(kicker, title, description, action = "") {
  return `<div class="page-head"><div><div class="eyebrow">${kicker}</div><h2>${title}</h2>${description ? `<p class="page-kicker">${description}</p>` : ""}</div>${action}</div>`;
}

export function valueField(id, label, value, type = "text", extra = "") {
  return `<div><label class="field-label" for="${id}">${label}</label><input id="${id}" type="${type}" value="${esc(value)}" ${extra}></div>`;
}

export function selectField(id, label, value, options, extra = "") {
  return `<div><label class="field-label" for="${id}">${label}</label><select id="${id}" ${extra}>${options.map(option => { const v = typeof option === "string" ? option : option.value; const t = typeof option === "string" ? option : option.label; return `<option value="${v}" ${v === value ? "selected" : ""}>${t}</option>`; }).join("")}</select></div>`;
}
