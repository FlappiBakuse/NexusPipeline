import { esc } from "./format.js";

export function pageHeader(kicker, title, description, action = "") {
  return `<div class="page-head"><div><div class="eyebrow">${kicker}</div><h2>${title}</h2>${description ? `<p class="page-kicker">${description}</p>` : ""}</div>${action}</div>`;
}

export function valueField(id, label, value, type = "text", extra = "") {
  return `<div><label class="field-label" for="${id}">${label}</label><input id="${id}" type="${type}" value="${esc(value)}" ${extra}></div>`;
}

export function selectField(id, label, value, options) {
  return `<div><label class="field-label" for="${id}">${label}</label><select id="${id}">${options.map(option => `<option value="${option}" ${option === value ? "selected" : ""}>${option}</option>`).join("")}</select></div>`;
}
