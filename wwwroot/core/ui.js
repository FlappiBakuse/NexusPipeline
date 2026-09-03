import { $, $$ } from "./dom.js";
import { registerInterval } from "./state.js";
import { api } from "./api.js";
import { icon } from "./icons.js";
import { systemActionCard } from "./forms.js";
import { applyThemeValue, cycleThemeValue, initThemeValue } from "./appearance.js";
import { durationClock } from "./duration.js";
import { initTooltips } from "./tooltip.js";

const view = $("#view");
let toastTimer = null;
let lastToastMessage = null;
let lastToastAt = 0;
const SHAKE_WINDOW_MS = 2500;
let noticeSequence = 0;

export function render(html) {
  view.innerHTML = html;
  initAutoScroll(view);
  syncAllModeToggles(view);
  syncAllSwitchControls(view);
  initTooltips();
  // （P1-2）：路由渲染后焦点移到主内容区（tabindex=-1 的 #view），键盘用户切页后从页面开头继续导航；
  // preventScroll 避免打断视口位置。
  view.focus({ preventScroll: true });
}

/** 旧式文本切换按钮的状态同步。新的布尔开关使用 .switch-control 的轨道/滑块视觉。 */
export function syncModeToggleText(btn) {
  if (!btn || !btn.classList.contains("mode-toggle")) return;
  if (btn.classList.contains("switch-control")) return;
  if (btn.hasAttribute("data-day") || btn.dataset.toggleText === "false") return;
  const base = btn.dataset.baseText || btn.textContent.trim();
  btn.dataset.baseText = base;
  btn.textContent = base + (btn.getAttribute("aria-pressed") === "true" ? "：开" : "：关");
}

/** 同步根节点内全部切换按钮文字（render/showModal/点击切换后调用）。 */
export function syncAllModeToggles(root = view) {
  root.querySelectorAll(".mode-toggle").forEach(syncModeToggleText);
}

/** 同步开关的视觉状态与可读的状态数据，不修改用户可见文案。 */
export function syncSwitchControl(btn) {
  if (!btn?.classList.contains("switch-control")) return;
  const on = btn.getAttribute("aria-pressed") === "true";
  btn.dataset.state = on ? "on" : "off";
  const stateText = btn.querySelector("[data-switch-state]");
  if (stateText) stateText.textContent = on ? "已启用" : "已停用";
}

export function syncAllSwitchControls(root = view) {
  root.querySelectorAll(".switch-control").forEach(syncSwitchControl);
}

/** 更多菜单：打开时将焦点交给菜单，关闭时可恢复到触发按钮。 */
export function closeMoreMenus({ restoreFocus = false } = {}) {
  document.querySelectorAll(".overflow-menu:not([hidden])").forEach(menu => {
    menu.hidden = true;
    menu.removeAttribute("data-open");
    menu.removeAttribute("style");
    const trigger = menu.closest(".overflow-menu-wrap")?.querySelector(".overflow-trigger");
    trigger?.setAttribute("aria-expanded", "false");
    if (restoreFocus && trigger) trigger.focus({ preventScroll: true });
  });
}

/** 弹出卡定位：列表容器 overflow:hidden 会裁掉绝对定位弹出卡（底部卡片处被卡片边截断），
 *  改为 fixed + 触发器视口坐标（脱离祖先裁剪）；视口底部空间不足时翻转到触发器上方，水平方向完整可见。 */
function positionMoreMenu(menu, trigger) {
  const GAP = 6; // 与 CSS .overflow-menu 的 calc(100% + 6px) 保持一致
  const MARGIN = 8;
  const triggerRect = trigger.getBoundingClientRect();
  const menuWidth = menu.offsetWidth;
  const menuHeight = menu.offsetHeight;
  let top = triggerRect.bottom + GAP;
  let left = triggerRect.right - menuWidth;
  if (top + menuHeight > window.innerHeight - MARGIN && triggerRect.top - menuHeight - GAP >= MARGIN) {
    top = triggerRect.top - menuHeight - GAP;
  }
  if (top < MARGIN) top = MARGIN;
  if (left < MARGIN) left = MARGIN;
  if (left + menuWidth > window.innerWidth - MARGIN) left = Math.max(MARGIN, window.innerWidth - menuWidth - MARGIN);
  menu.style.position = "fixed";
  menu.style.right = "auto";
  menu.style.top = `${Math.round(top)}px`;
  menu.style.left = `${Math.round(left)}px`;
}

let moreMenuPositionBound = false;
function bindMoreMenuPositioning() {
  if (moreMenuPositionBound) return;
  moreMenuPositionBound = true;
  // fixed 定位不随文档滚动，滚动/改变视口时重新跟随触发器（capture 阶段覆盖任意滚动容器）。
  const reposition = () => {
    document.querySelectorAll(".overflow-menu[data-open]").forEach(menu => {
      const trigger = menu.closest(".overflow-menu-wrap")?.querySelector(".overflow-trigger");
      if (trigger) positionMoreMenu(menu, trigger);
    });
  };
  document.addEventListener("scroll", reposition, true);
  window.addEventListener("resize", reposition);
}

export function toggleMoreMenu(trigger) {
  const wrap = trigger?.closest(".overflow-menu-wrap");
  const menu = wrap?.querySelector(".overflow-menu");
  if (!menu) return;
  const open = menu.hidden;
  closeMoreMenus();
  if (!open) return;
  menu.hidden = false;
  menu.dataset.open = "true";
  trigger.setAttribute("aria-expanded", "true");
  bindMoreMenuPositioning();
  positionMoreMenu(menu, trigger);
  menu.querySelector('[role="menuitem"]')?.focus({ preventScroll: true });
}

/** 长文本滚动：内容溢出容器时启用往返滚动（否则保持省略号兜底）。</summary> */
export function initAutoScroll(root = view) {
  const applyHoverScroll = (el, inner, enabled = true) => {
    const width = inner.scrollWidth;
    if (!enabled || width <= el.clientWidth + 1) {
      inner.style.removeProperty("width");
      el.style.removeProperty("--hover-scroll-x");
      el.classList.remove("is-overflowing");
      return;
    }
    inner.style.width = `${width}px`;
    el.style.setProperty("--hover-scroll-x", `${el.clientWidth - width}px`);
    el.classList.add("is-overflowing");
  };
  root.querySelectorAll(".scroll-text").forEach(el => {
    const inner = el.querySelector(":scope > .scroll-inner");
    if (!inner) return;
    if (inner.scrollWidth > el.clientWidth + 1) {
      el.style.setProperty("--scroll-x", `${el.clientWidth - inner.scrollWidth}px`);
      el.classList.add("scrolling", "is-overflowing");
    } else {
      el.classList.remove("scrolling", "is-overflowing");
      el.style.removeProperty("--scroll-x");
    }
  });
  root.querySelectorAll(".plugin-name-scroll").forEach(el => {
    const inner = el.querySelector(":scope > .plugin-name-scroll-inner");
    if (inner) applyHoverScroll(el, inner);
  });
  root.querySelectorAll(".plugin-detail-name-scroll").forEach(el => {
    const inner = el.querySelector(":scope > .plugin-detail-name-scroll-inner");
    if (!inner) return;
    const narrowViewport = window.matchMedia?.("(max-width: 767px)")?.matches ?? true;
    applyHoverScroll(el, inner, narrowViewport && el.clientWidth > 0);
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

/** 页面角落通知：使用 DOM textContent 组装，允许多条并存并由用户逐条关闭。 */
export function pushNotice(title, body = "", kind = "info") {
  const stack = $("#notice-stack");
  if (!stack) return;
  const allowedKinds = new Set(["info", "success", "warning", "error"]);
  const tone = allowedKinds.has(String(kind)) ? String(kind) : "info";
  const notice = document.createElement("article");
  notice.className = `notice ${tone}`;
  notice.dataset.noticeId = String(++noticeSequence);
  notice.setAttribute("role", "status");

  const content = document.createElement("div");
  content.className = "notice-content";
  const heading = document.createElement("strong");
  heading.className = "notice-title";
  heading.textContent = String(title ?? "");
  content.append(heading);
  if (String(body ?? "").length > 0) {
    const copy = document.createElement("p");
    copy.className = "notice-body";
    copy.textContent = String(body);
    content.append(copy);
  }

  const close = document.createElement("button");
  close.type = "button";
  close.className = "notice-close";
  close.setAttribute("aria-label", "关闭通知");
  close.textContent = "×";
  close.addEventListener("click", () => notice.remove());
  notice.append(content, close);
  stack.append(notice);
  while (stack.children.length > 12) stack.firstElementChild?.remove();
  window.setTimeout(() => notice.remove(), 9000);
}

let countdownTimer = null;

/** 下一调度倒计时（清理旧定时器）：仪表盘 3 秒重渲染会重复调用，注册前清理模块级旧 interval，避免累积；路由切换由 disposePage 统一清理。 */
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
    element.textContent = durationClock(Math.floor(remain / 1000));
  };
  update();
  countdownTimer = registerInterval(setInterval(update, 1000));
}

/** 停止下一调度倒计时（，仪表盘局部更新时下一调度消失用）。 */
export function stopCountdown() {
  if (countdownTimer !== null) {
    clearInterval(countdownTimer);
    countdownTimer = null;
  }
}

let systemActionTimer = null;

/** 完成操作倒计时：每秒更新卡片剩余秒数（「N 秒后将{动作}」，归零显示「即将执行」）。
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
  // （P1-3）：菜单按钮展开态反馈（aria-expanded/aria-controls）。
  $$('[data-action="open-nav"]').forEach(button => {
    button.setAttribute("aria-expanded", String(open));
    button.setAttribute("aria-controls", "sidebar");
  });
}

/** 取消完成操作倒计时（全局 shell 动作，仪表盘/调度中心共用）：成功提示并拉取最新状态局部刷新卡片。 */
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
  syncThemeControls(initThemeValue());
}

export function applyTheme(theme) {
  const value = applyThemeValue(theme);
  syncThemeControls(value);
}

function syncThemeControls(value) {
  const iconName = value === "light" ? "sun" : value === "dark" ? "moon" : "system";
  $$('[data-theme-icon], #theme-icon').forEach(element => element.innerHTML = icon(iconName));
  $$('[data-action="toggle-theme"]').forEach(toggle => toggle.setAttribute("aria-label", `当前${value === "system" ? "跟随系统" : value === "light" ? "浅色" : "深色"}，点击切换主题`));
}

export function cycleTheme() {
  const value = cycleThemeValue();
  syncThemeControls(value);
  toast(`主题：${value === "system" ? "跟随系统" : value === "light" ? "浅色" : "深色"}`);
}

/** 字段错误：高亮输入框，并把错误写入预留的稳定位置。 */
function visualFieldElement(element) {
  if (!element?.matches?.("[data-nxp-select-value]")) return element;
  return element.closest("[data-nxp-select]")?.querySelector("[data-nxp-select-trigger]") || element;
}

function eachFieldElement(element, callback) {
  const visual = visualFieldElement(element);
  callback(element);
  if (visual !== element) callback(visual);
  return visual;
}

export function setFieldError(id, message) {
  const element = $(`#${id}`);
  if (!element) return;
  const visual = eachFieldElement(element, item => {
    item.classList.add("field-error");
    item.setAttribute("aria-invalid", "true");
  });
  let slot = document.getElementById(`${id}-error`);
  if (!slot) {
    slot = document.createElement("p");
    slot.id = `${id}-error`;
    slot.className = "field-error-message";
    slot.setAttribute("role", "alert");
    (element.closest(".field") || element.parentElement)?.append(slot);
  }
  if (slot) {
    slot.textContent = message || "请检查此项";
    slot.hidden = false;
    const describedBy = (visual.getAttribute("aria-describedby") || "").split(/\s+/).filter(Boolean);
    if (!describedBy.includes(slot.id)) describedBy.push(slot.id);
    visual.setAttribute("aria-describedby", describedBy.join(" "));
  }
  visual.focus({ preventScroll: true });
}

/** 字段无文案错误：只保留红色输入框与无障碍状态，不占用额外错误文案。 */
export function setFieldInvalid(id) {
  const element = $(`#${id}`);
  if (!element) return;
  const visual = eachFieldElement(element, item => {
    item.classList.add("field-error");
    item.setAttribute("aria-invalid", "true");
  });
  const slot = document.getElementById(`${id}-error`);
  if (slot) {
    slot.hidden = true;
    slot.textContent = "";
    const describedBy = (visual.getAttribute("aria-describedby") || "")
      .split(/\s+/)
      .filter(Boolean)
      .filter(value => value !== slot.id);
    if (describedBy.length) visual.setAttribute("aria-describedby", describedBy.join(" "));
    else visual.removeAttribute("aria-describedby");
  }
  visual.focus({ preventScroll: true });
}

/** 必填空值错误：保留红色边框与可访问性状态，不在字段下方显示红色文案。 */
export function setRequiredFieldError(id) {
  const element = $(`#${id}`);
  if (!element) return;
  const visual = eachFieldElement(element, item => {
    item.classList.add("field-error");
    item.setAttribute("aria-invalid", "true");
    item.setAttribute("aria-required", "true");
  });
  const slot = document.getElementById(`${id}-error`);
  if (slot) {
    slot.hidden = true;
    slot.textContent = "";
    const describedBy = (visual.getAttribute("aria-describedby") || "")
      .split(/\s+/)
      .filter(Boolean)
      .filter(value => value !== slot.id);
    if (describedBy.length) visual.setAttribute("aria-describedby", describedBy.join(" "));
    else visual.removeAttribute("aria-describedby");
  }
  visual.focus({ preventScroll: true });
}

/** 清除字段内联错误（无错误时无操作）。 */
export function clearFieldError(id) {
  const element = $(`#${id}`);
  if (!element) return;
  const visual = eachFieldElement(element, item => {
    item.classList.remove("field-error");
    item.removeAttribute("aria-invalid");
  });
  const slot = document.getElementById(`${id}-error`);
  if (slot) {
    slot.hidden = true;
    slot.textContent = "";
    const describedBy = (visual.getAttribute("aria-describedby") || "").split(/\s+/).filter(Boolean).filter(value => value !== slot.id);
    if (describedBy.length) visual.setAttribute("aria-describedby", describedBy.join(" "));
    else visual.removeAttribute("aria-describedby");
  }
}

/** 批量清除（弹窗关闭/保存成功后调用，防止残留高亮）。 */
export function clearFieldErrors(ids) {
  ids.forEach(id => clearFieldError(id));
}

/** 提交按钮忙碌态（，P2-2）：请求期间禁用按钮并显示 spinner，防止重复提交；按钮随弹窗销毁时安全。 */
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
