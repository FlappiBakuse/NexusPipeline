import { $, $$ } from "./api.js";
import { registerInterval } from "./state.js";

const view = $("#view");
let toastTimer = null;
let modalReturnFocus = null;

export function render(html) {
  view.innerHTML = html;
}

export function navActive(page) {
  $$(`[data-page]`).forEach(link => {
    const active = link.dataset.page === page;
    link.classList.toggle("active", active);
    if (active) link.setAttribute("aria-current", "page");
    else link.removeAttribute("aria-current");
  });
}

export function setTopbarTitle(title) {
  const el = $("#topbar-title");
  if (el) el.textContent = title;
}

export function toast(message, kind = "info") {
  const element = $("#toast");
  if (!element) return;
  element.textContent = message;
  element.classList.toggle("error", kind === "error");
  element.classList.remove("hidden");
  clearTimeout(toastTimer);
  toastTimer = setTimeout(() => element.classList.add("hidden"), 3200);
}

export function modalShell(title, body, footer = "", wide = false) {
  const titleId = "modal-title-" + Math.random().toString(36).slice(2);
  return `<div class="modal-header">
    <div><h3 class="modal-title" id="${titleId}">${title}</h3></div>
    <button class="icon-button modal-close" type="button" data-action="close-modal" aria-label="关闭">×</button>
  </div>
  <div class="modal-body">${body}</div>
  ${footer ? `<div class="modal-footer">${footer}</div>` : ""}`;
}

export function showModal(content, wide = false) {
  closeModal();
  modalReturnFocus = document.activeElement instanceof HTMLElement ? document.activeElement : null;
  const mask = document.createElement("div");
  mask.className = "modal-mask";
  mask.setAttribute("role", "presentation");
  const modal = document.createElement("div");
  modal.className = wide ? "modal wide" : "modal";
  modal.setAttribute("role", "dialog");
  modal.setAttribute("aria-modal", "true");
  modal.innerHTML = content;
  const heading = $("[id^='modal-title-']", modal);
  if (heading) modal.setAttribute("aria-labelledby", heading.id);
  mask.appendChild(modal);
  mask.addEventListener("mousedown", event => {
    if (event.target === mask) closeModal();
  });
  mask.addEventListener("keydown", event => {
    if (event.key === "Escape") {
      event.preventDefault();
      closeModal();
      return;
    }
    if (event.key !== "Tab") return;
    const focusable = $$('button, input, select, textarea, a[href], [tabindex]:not([tabindex="-1"])', modal)
      .filter(element => !element.disabled && element.offsetParent !== null);
    if (!focusable.length) return;
    const first = focusable[0];
    const last = focusable[focusable.length - 1];
    if (event.shiftKey && document.activeElement === first) {
      event.preventDefault();
      last.focus();
    } else if (!event.shiftKey && document.activeElement === last) {
      event.preventDefault();
      first.focus();
    }
  });
  document.body.appendChild(mask);
  requestAnimationFrame(() => {
    const first = $("input, select, textarea", modal) || $("button, a[href]", modal);
    if (first) first.focus();
  });
}

export function closeModal() {
  const mask = $(".modal-mask");
  if (mask) mask.remove();
  if (modalReturnFocus && document.contains(modalReturnFocus)) modalReturnFocus.focus();
  modalReturnFocus = null;
}

export function startCountdown(targetId, timeValue) {
  const target = new Date(timeValue).getTime();
  const update = () => {
    const element = $("#" + targetId);
    if (!element) return;
    const remain = target - Date.now();
    if (remain <= 0) {
      element.textContent = "即将触发";
      return;
    }
    const seconds = Math.floor(remain / 1000);
    const hours = String(Math.floor(seconds / 3600)).padStart(2, "0");
    const minutes = String(Math.floor(seconds % 3600 / 60)).padStart(2, "0");
    const secs = String(seconds % 60).padStart(2, "0");
    element.textContent = `剩余 ${hours}:${minutes}:${secs}`;
  };
  update();
  registerInterval(setInterval(update, 1000));
}

export function setNavOpen(open) {
  document.body.classList.toggle("nav-open", open);
  const sidebar = $("#sidebar");
  if (sidebar) sidebar.setAttribute("aria-hidden", String(!open && window.innerWidth <= 820));
}

export function initTheme() {
  const stored = localStorage.getItem("nexus-theme") || "system";
  applyTheme(stored);
}

export function applyTheme(theme) {
  const value = ["light", "dark", "system"].includes(theme) ? theme : "system";
  document.body.dataset.theme = value;
  localStorage.setItem("nexus-theme", value);
  const icon = value === "light" ? "☼" : value === "dark" ? "☾" : "◐";
  $$('[data-theme-icon]').forEach(element => element.textContent = icon);
  $$('[data-action="toggle-theme"]').forEach(toggle => toggle.setAttribute("aria-label", `当前${value === "system" ? "跟随系统" : value === "light" ? "浅色" : "深色"}，点击切换主题`));
}

export function cycleTheme() {
  const current = document.body.dataset.theme || "system";
  applyTheme(current === "system" ? "light" : current === "light" ? "dark" : "system");
  toast(`主题：${document.body.dataset.theme === "system" ? "跟随系统" : document.body.dataset.theme === "light" ? "浅色" : "深色"}`);
}
