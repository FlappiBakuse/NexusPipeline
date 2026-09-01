import { api } from "./api.js";
import { toast } from "./ui.js";
import { hideTooltip } from "./tooltip.js";

let nativeDialogLockCount = 0;
let nativeDialogLockElement = null;
let nativeDialogLockState = null;

function restoreAttribute(element, name, value) {
  if (!element) return;
  if (value === null) element.removeAttribute(name);
  else element.setAttribute(name, value);
}

function acquireNativeDialogLock() {
  if (typeof document === "undefined" || !document.body) return () => {};
  nativeDialogLockCount += 1;
  if (nativeDialogLockCount === 1) {
    const shell = document.querySelector(".app-shell");
    nativeDialogLockState = {
      shell,
      inert: shell?.getAttribute("inert") ?? null,
      ariaBusy: shell?.getAttribute("aria-busy") ?? null,
      activeElement: document.activeElement instanceof HTMLElement ? document.activeElement : null,
    };
    nativeDialogLockElement = document.createElement("div");
    nativeDialogLockElement.className = "native-dialog-lock";
    nativeDialogLockElement.setAttribute("role", "status");
    nativeDialogLockElement.setAttribute("aria-label", "路径选择器已打开");
    nativeDialogLockElement.innerHTML = '<span class="spinner" aria-hidden="true"></span>';
    shell?.setAttribute("inert", "");
    shell?.setAttribute("aria-busy", "true");
    document.body.classList.add("native-dialog-open");
    document.body.append(nativeDialogLockElement);
  }
  let released = false;
  return () => {
    if (released) return;
    released = true;
    nativeDialogLockCount = Math.max(0, nativeDialogLockCount - 1);
    if (nativeDialogLockCount !== 0) return;
    nativeDialogLockElement?.remove();
    nativeDialogLockElement = null;
    document.body.classList.remove("native-dialog-open");
    restoreAttribute(nativeDialogLockState?.shell, "inert", nativeDialogLockState?.inert ?? null);
    restoreAttribute(nativeDialogLockState?.shell, "aria-busy", nativeDialogLockState?.ariaBusy ?? null);
    const activeElement = nativeDialogLockState?.activeElement;
    nativeDialogLockState = null;
    if (activeElement?.isConnected && !activeElement.disabled) activeElement.focus({ preventScroll: true });
  };
}

function stripOuterQuotes(value) {
  const text = String(value || "").trim();
  if (text.length < 2) return text;
  const first = text[0];
  const last = text[text.length - 1];
  return (first === '"' && last === '"') || (first === "'" && last === "'")
    ? text.slice(1, -1).trim()
    : text;
}

function preserveMarker(value) {
  const match = String(value || "").match(/^\s*(%FIRST%|%LAST%)\s+/i);
  return match ? `${match[1]} ` : "";
}

function pathForDialog(value) {
  return stripOuterQuotes(String(value || "").replace(/^\s*(%FIRST%|%LAST%)\s+/i, ""));
}

function dialogPathForTrigger(input, trigger) {
  const rootTarget = trigger.dataset.pathRootTarget || "";
  if (!rootTarget) {
    return { initialPath: pathForDialog(input.value), requiresExistingDirectory: false, invalidMessage: "" };
  }
  const rootInput = document.getElementById(rootTarget);
  return {
    initialPath: pathForDialog(rootInput?.value),
    requiresExistingDirectory: true,
    invalidMessage: trigger.dataset.pathRootError || "脚本根目录错误",
  };
}

/** 路径选择只替换路径正文，保留任务前/后脚本的执行范围标记。 */
function applySelectedPath(input, selected) {
  const current = String(input.value || "");
  const marker = preserveMarker(current);
  input.value = marker + stripOuterQuotes(selected);
}

export async function pickPath(trigger) {
  if (!trigger || trigger.disabled) return;
  const targetId = trigger.dataset.pathTarget || "";
  const input = document.getElementById(targetId);
  if (!input || input.disabled) return;
  const kind = ["file", "folder", "file-or-folder"].includes(trigger.dataset.pathKind)
    ? trigger.dataset.pathKind
    : "file";
  const dialogPath = dialogPathForTrigger(input, trigger);
  if (dialogPath.requiresExistingDirectory && !dialogPath.initialPath) {
    toast(dialogPath.invalidMessage, "error");
    return;
  }
  const wasDisabled = trigger.disabled;
  let selected = false;
  let selectedInput = null;
  trigger.disabled = true;
  hideTooltip();
  const releaseNativeDialogLock = acquireNativeDialogLock();
  try {
    const result = await api("POST", "/api/native-dialog", {
      kind,
      title: `选择${trigger.dataset.pathTitle || "路径"}`,
      initialPath: dialogPath.initialPath,
      filter: trigger.dataset.pathFilter || "",
      requireInitialDirectory: dialogPath.requiresExistingDirectory,
      invalidInitialPathMessage: dialogPath.invalidMessage,
    });
    if (result?.path) {
      selected = true;
      selectedInput = input;
      applySelectedPath(input, result.path);
      input.dispatchEvent(new Event("input", { bubbles: true }));
      input.dispatchEvent(new Event("change", { bubbles: true }));
    }
  } catch (error) {
    toast(error.message || "无法打开路径选择器", "error");
  } finally {
    releaseNativeDialogLock();
    trigger.disabled = wasDisabled;
    if (selectedInput?.isConnected && !selectedInput.disabled) selectedInput.focus({ preventScroll: true });
    else if (trigger.isConnected && !trigger.disabled) trigger.focus({ preventScroll: true });
  }
}
