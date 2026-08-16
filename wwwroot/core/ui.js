import { $, $$ } from "./dom.js";
import { registerInterval } from "./state.js";
import { api } from "./api.js";
import { systemActionCard } from "./forms.js";

const view = $("#view");
let toastTimer = null;
let lastToastMessage = null;
let lastToastAt = 0;
const SHAKE_WINDOW_MS = 2500;

export function render(html) {
  view.innerHTML = html;
  initAutoScroll(view);
  syncAllModeToggles(view);
  // v0.7.3+（P1-2）：路由渲染后焦点移到主内容区（tabindex=-1 的 #view），键盘用户切页后从页面开头继续导航；
  // preventScroll 避免打断视口位置。
  view.focus({ preventScroll: true });
}

/** 切换按钮文字状态同步（v0.6.7+）：.mode-toggle 按钮文字追加「：开/：关」后缀，让用户直接明了开关状态。
 *  跳过星期按钮（data-day）与显式标记 data-toggle-text="false" 的按钮（如「使用判断脚本」模式切换）。
 *  aria-pressed 仍为唯一状态权威，文字仅作展示。 */
export function syncModeToggleText(btn) {
  if (!btn || !btn.classList.contains("mode-toggle")) return;
  if (btn.hasAttribute("data-day") || btn.dataset.toggleText === "false") return;
  const base = btn.dataset.baseText || btn.textContent.trim();
  btn.dataset.baseText = base;
  btn.textContent = base + (btn.getAttribute("aria-pressed") === "true" ? "：开" : "：关");
}

/** 同步根节点内全部切换按钮文字（render/showModal/点击切换后调用）。 */
export function syncAllModeToggles(root = view) {
  root.querySelectorAll(".mode-toggle").forEach(syncModeToggleText);
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

let countdownTimer = null;

/** 下一调度倒计时（v0.6.7+ 清理旧定时器）：仪表盘 3 秒重渲染会重复调用，注册前清理模块级旧 interval，避免累积；路由切换由 disposePage 统一清理。 */
export function startCountdown(targetId, timeValue) {
  const target = new Date(timeValue).getTime();
  if (countdownTimer !== null) clearInterval(countdownTimer);
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
  countdownTimer = registerInterval(setInterval(update, 1000));
}

/** 停止下一调度倒计时（v0.6.7+，仪表盘局部更新时下一调度消失用）。 */
export function stopCountdown() {
  if (countdownTimer !== null) {
    clearInterval(countdownTimer);
    countdownTimer = null;
  }
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
  // v0.7.3+（P1-3）：菜单按钮展开态反馈（aria-expanded/aria-controls）。
  $$('[data-action="open-nav"]').forEach(button => {
    button.setAttribute("aria-expanded", String(open));
    button.setAttribute("aria-controls", "sidebar");
  });
}

/** 取消完成操作倒计时（v0.6.4+ 全局 shell 动作，仪表盘/调度中心共用）：成功提示并拉取最新状态局部刷新卡片。 */
export async function cancelSystemAction() {
  const card = document.querySelector('[data-testid="system-action-card"]');
  const verb = card?.dataset.actionVerb || "执行";
  try {
    await api("POST", "/api/system-action/cancel");
    toast(`已取消${verb}`);
    const status = await api("GET", "/api/status");
    const area = document.querySelector("#system-action-area");
    if (area) {
      area.innerHTML = systemActionCard(status.systemAction);
      startSystemActionCountdown();
    }
  } catch (error) {
    toast(error.message, "error");
  }
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

/** 字段内联错误（v0.7.3+，P2-1）：高亮 + 字段旁错误文字（role=alert）+ aria-invalid + 聚焦；代替仅 toast 提示。 */
export function setFieldError(id, message) {
  const element = $(`#${id}`);
  if (!element) return;
  element.classList.add("field-error");
  element.setAttribute("aria-invalid", "true");
  const wrap = element.closest("div");
  if (wrap) {
    let err = wrap.querySelector(".field-error-text");
    if (!err) {
      err = document.createElement("span");
      err.className = "field-error-text";
      err.setAttribute("role", "alert");
      wrap.appendChild(err);
    }
    err.textContent = message;
  }
  element.focus();
}

/** 清除字段内联错误（无错误时无操作）。 */
export function clearFieldError(id) {
  const element = $(`#${id}`);
  if (!element) return;
  element.classList.remove("field-error");
  element.removeAttribute("aria-invalid");
  element.closest("div")?.querySelector(".field-error-text")?.remove();
}

/** 批量清除（弹窗关闭/保存成功后调用，防止残留高亮）。 */
export function clearFieldErrors(ids) {
  ids.forEach(id => clearFieldError(id));
}

/** 提交按钮忙碌态（v0.7.3+，P2-2）：请求期间禁用按钮并显示 spinner，防止重复提交；按钮随弹窗销毁时安全。 */
export async function withBusy(button, fn) {
  if (!button || button.disabled) return;
  button.disabled = true;
  button.classList.add("busy");
  try {
    await fn();
  } finally {
    button.disabled = false;
    button.classList.remove("busy");
  }
}
