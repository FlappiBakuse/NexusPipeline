import { $, $$ } from "./dom.js";
import { registerInterval } from "./state.js";

const view = $("#view");
let toastTimer = null;
let lastToastMessage = null;
let lastToastAt = 0;
const SHAKE_WINDOW_MS = 2500;

export function render(html) {
  view.innerHTML = html;
  initAutoScroll(view);
}

/** 长文本滚动：内容溢出容器时启用往返滚动（否则保持省略号兜底）。</summary> */
export function initAutoScroll(root = view) {
  root.querySelectorAll(".scroll-text").forEach(el => {
    const inner = el.querySelector(":scope > .scroll-inner");
    if (!inner) return;
    if (inner.scrollWidth > el.clientWidth + 1) {
      el.style.setProperty("--scroll-x", `${el.clientWidth - inner.scrollWidth}px`);
      el.classList.add("scrolling");
    } else {
      el.classList.remove("scrolling");
    }
  });
  initInputHints(root);
}

/** 滚动提示浮层显隐：输入框有值或聚焦时隐藏（原生 placeholder 无法滚动，改用浮层；:placeholder-shown 对无 placeholder 属性的输入框不可靠）。 */
function initInputHints(root) {
  root.querySelectorAll(".input-scroll").forEach(wrap => {
    const input = wrap.querySelector("input");
    const hint = wrap.querySelector(".input-scroll-hint");
    if (!input || !hint) return;
    const sync = () => {
      hint.hidden = input.value.length > 0 || document.activeElement === input;
    };
    input.addEventListener("input", sync);
    input.addEventListener("focus", sync);
    input.addEventListener("blur", sync);
    sync();
  });
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
  const now = Date.now();
  element.textContent = message;
  element.classList.toggle("error", kind === "error");
  element.classList.remove("shake");
  if (kind === "error" && message === lastToastMessage && now - lastToastAt < SHAKE_WINDOW_MS) {
    void element.offsetWidth;
    element.classList.add("shake");
  }
  if (kind === "error") {
    lastToastMessage = message;
    lastToastAt = now;
  }
  element.classList.remove("hidden");
  clearTimeout(toastTimer);
  toastTimer = setTimeout(() => element.classList.add("hidden"), 3200);
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
    element.textContent = `${hours}:${minutes}:${secs}`;
  };
  update();
  registerInterval(setInterval(update, 1000));
}

let systemActionTimer = null;

/** 完成操作倒计时（v0.6.3+）：每秒更新卡片剩余秒数（「N 秒后将{动作}」，归零显示「即将执行」）。
 *  注册前清理旧定时器（仪表盘每 3 秒重渲染会重复调用，避免累积）；路由切换由 disposePage 统一清理。 */
export function startSystemActionCountdown() {
  const card = document.querySelector('[data-testid="system-action-card"]');
  if (!card) return;
  const countdown = card.querySelector('[data-testid="system-action-countdown"]');
  if (!countdown) return;
  const deadline = new Date(countdown.dataset.deadline || "").getTime();
  const verb = card.dataset.actionVerb || "执行";
  if (systemActionTimer !== null) clearInterval(systemActionTimer);
  const update = () => {
    const remain = Math.max(0, Math.round((deadline - Date.now()) / 1000));
    countdown.textContent = remain > 0 ? `${remain} 秒后将${verb}` : `即将执行${verb}`;
  };
  update();
  systemActionTimer = registerInterval(setInterval(update, 1000));
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
