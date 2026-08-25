/**
 * 通用拖拽排序组件（v0.6.8+）：Pointer Events 统一鼠标/触屏，无业务依赖（定位同 core/pager.js）。
 *
 * 用法：
 *   import { initDndList } from "./dnd.js";
 *   initDndList(container, { onDrop(orderedIds, draggedId) })
 *
 * 约定：
 *   - 容器内每个可拖项需带 data-dnd-id；拖拽把手用 .drag-handle（触屏需 touch-action: none，见 style.css）
 *   - 拖拽结束后 DOM 已按新顺序重排（insertBefore/appendChild），onDrop 收到容器内全部 data-dnd-id 的新顺序
 *   - 视图收到 onDrop 后自行提交全量顺序到后端；失败时可重新渲染回滚
 */

let active = null;

function scrollTargetKey(target) {
  if (target?.id) return { type: "id", value: target.id };
  if (target?.classList?.contains("modal-body")) return { type: "selector", value: ".modal-mask .modal-body" };
  return null;
}

function captureScrollState(container) {
  const state = [{ target: window, left: window.scrollX, top: window.scrollY }];
  let parent = container.parentElement;
  while (parent) {
    if (parent.scrollHeight > parent.clientHeight || parent.scrollWidth > parent.clientWidth) {
      state.push({ target: parent, key: scrollTargetKey(parent), left: parent.scrollLeft, top: parent.scrollTop });
    }
    parent = parent.parentElement;
  }
  return state;
}

function restoreScrollState(state) {
  const restore = () => state.forEach(({ target, key, left, top }) => {
    let current = target;
    if (current !== window && !current?.isConnected && key) {
      current = key.type === "id" ? document.getElementById(key.value) : document.querySelector(key.value);
    }
    if (current === window) window.scrollTo(left, top);
    else if (current) { current.scrollLeft = left; current.scrollTop = top; }
  });
  requestAnimationFrame(() => {
    restore();
    requestAnimationFrame(restore);
  });
}

function submitDrop(container, ids, itemId, scrollState, onDrop) {
  let result;
  try {
    result = onDrop?.(ids, itemId);
  } finally {
    if (result && typeof result.then === "function") Promise.resolve(result).then(() => restoreScrollState(scrollState), () => restoreScrollState(scrollState));
    else restoreScrollState(scrollState);
  }
}

export function initDndList(container, { onDrop } = {}) {
  container.addEventListener("pointerdown", (event) => {
    const handle = event.target.closest(".drag-handle");
    if (!handle || !container.contains(handle)) return;
    const item = handle.closest("[data-dnd-id]");
    if (!item || active) return;
    event.preventDefault();
    active = {
      container,
      item,
      pointerId: event.pointerId,
      startClientY: event.clientY,
      offsetY: event.clientY - item.getBoundingClientRect().top,
      scrollState: captureScrollState(container),
      placeBefore: null,
      moved: false,
    };
    item.classList.add("dnd-dragging");
    try {
      container.setPointerCapture(event.pointerId);
    } catch { /* pointer capture 失败不影响拖拽 */ }
  });

  container.addEventListener("pointermove", (event) => {
    if (!active || active.pointerId !== event.pointerId) return;
    event.preventDefault();
    const s = active;
    s.moved = true;
    const delta = event.clientY - s.startClientY;
    s.item.style.transform = `translateY(${delta}px)`;
    s.item.style.zIndex = "1";
    updatePlacement(event.clientY);
  });

  container.addEventListener("pointerup", (event) => {
    if (!active || active.pointerId !== event.pointerId) return;
    const s = active;
    active = null;
    clearClasses(s.container);
    s.item.style.transform = "";
    s.item.style.zIndex = "";
    s.item.classList.remove("dnd-dragging");
    if (!s.moved) return;
    if (s.placeBefore) {
      container.insertBefore(s.item, s.placeBefore);
    } else {
      container.appendChild(s.item);
    }
    const ids = Array.from(container.querySelectorAll("[data-dnd-id]"))
      .map(el => el.dataset.dndId)
      .filter(Boolean);
    submitDrop(container, ids, s.item.dataset.dndId, s.scrollState, onDrop);
  });

  container.addEventListener("pointercancel", (event) => {
    if (!active || active.pointerId !== event.pointerId) return;
    const s = active;
    active = null;
    clearClasses(s.container);
    s.item.style.transform = "";
    s.item.style.zIndex = "";
  });

  // （P2-3）：键盘替代——焦点在 .drag-handle 上时按 ↑/↓ 移动该项并提交新顺序
  // （拖拽对键盘用户不可用，此为其等价的键控重排）。
  container.addEventListener("keydown", event => {
    if (event.key !== "ArrowUp" && event.key !== "ArrowDown") return;
    const handle = event.target?.closest?.(".drag-handle");
    if (!handle || !container.contains(handle)) return;
    const item = handle.closest("[data-dnd-id]");
    if (!item) return;
    event.preventDefault();
    const items = Array.from(container.querySelectorAll("[data-dnd-id]"));
    const index = items.indexOf(item);
    const target = event.key === "ArrowUp" ? index - 1 : index + 1;
    if (target < 0 || target >= items.length) return;
    const scrollState = captureScrollState(container);
    container.insertBefore(item, event.key === "ArrowUp" ? items[target] : items[target].nextSibling);
    const ids = Array.from(container.querySelectorAll("[data-dnd-id]"))
      .map(el => el.dataset.dndId)
      .filter(Boolean);
    submitDrop(container, ids, item.dataset.dndId, scrollState, onDrop);
  });
}

/** 计算插入位置：鼠标越过哪一项的垂直中点，就插到它前面；没有则插到末尾。 */
function updatePlacement(clientY) {
  const s = active;
  let placeBefore = null;
  for (const el of s.container.children) {
    // 只跳过被拖拽项自身；带 .dnd-drop-before 的插入位置标记元素就是真正的候选目标，不得跳过
    // （跳过它会因「当前插入线元素不可见」而把落位震荡到其后一项—— 拖拽排序踩坑）。
    if (el === s.item) continue;
    const rect = el.getBoundingClientRect();
    if (clientY < rect.top + rect.height / 2) {
      placeBefore = el;
      break;
    }
  }
  if (placeBefore === s.placeBefore) return;
  if (s.placeBefore) s.placeBefore.classList.remove("dnd-drop-before");
  s.placeBefore = placeBefore;
  if (placeBefore) placeBefore.classList.add("dnd-drop-before");
}

function clearClasses(container) {
  container.querySelectorAll(".dnd-drop-before").forEach(el => el.classList.remove("dnd-drop-before"));
}
