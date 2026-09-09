import { trackController, releaseController } from "./state.js";
import { getLocale } from "./i18n.js";

const iconUrlCache = new Map();

function apiError(message, status, data) {
  const error = new Error(message);
  error.code = data?.code ?? null;
  error.status = status;
  error.data = data;
  return error;
}

function readAuthToken() {
  try {
    return localStorage.getItem("nexus-token");
  } catch {
    return null;
  }
}

function authHeaders() {
  const token = readAuthToken();
  return token
    ? { Authorization: "Bearer " + token, "X-Nexus-Locale": getLocale() }
    : { "X-Nexus-Locale": getLocale() };
}

function isAuthFailure(response, data) {
  return response.status === 401
    || response.headers.get("X-Nexus-Auth") === "required"
    || data?.code === "auth_required"
    || (data && data.error && String(data.error).includes("访问令牌"));
}

function handleAuthFailure(response, data) {
  try {
    localStorage.removeItem("nexus-token");
  } catch {
  }
  if (typeof window.__showTokenPrompt === "function") window.__showTokenPrompt();
  return apiError((data && data.error) || "需要访问令牌", response.status, data);
}

/** 远程访问下图标 API 需要 Bearer 头（`<img>` 无法携带，远程模式图标必 401）——
 * 渲染后经 fetch 取 blob 转 ObjectURL 替换 `[data-icon-id]` 元素的 src；失败保留占位图（data: 前缀）。
 * 图标按脚本 Id 缓存（页面生命周期内不重复请求；ObjectURL 随页面卸载自动释放）。 */
export async function hydrateIcons(container) {
  const els = (container || document).querySelectorAll("[data-icon-id]");
  for (const el of els) {
    const id = el.dataset.iconId;
    if (!id || el.dataset.iconDone) continue;
    el.dataset.iconDone = "1";
    try {
      let url = iconUrlCache.get(id);
      if (!url) {
        const blob = await apiBlob("/api/scripts/" + id + "/icon");
        if (!blob.type.startsWith("image/")) continue;
        url = URL.createObjectURL(blob);
        iconUrlCache.set(id, url);
      }
      if (el.isConnected) el.src = url;
    } catch {
      // 保留占位图
    }
  }
}

/** 获取受保护的二进制资源。调用方应在资源不再使用时释放返回 blob 对应的 ObjectURL。 */
export async function apiBlob(path, signal) {
  const controller = signal ? null : trackController(new AbortController());
  try {
    const response = await fetch(path, {
      method: "GET",
      headers: authHeaders(),
      signal: signal || controller.signal,
      cache: "no-store",
    });
    if (response.status === 204) throw apiError("二进制资源不存在", response.status, null);
    if (response.ok && response.headers.get("X-Nexus-Auth") !== "required") return await response.blob();
    const data = await response.json().catch(() => null);
    if (isAuthFailure(response, data)) throw handleAuthFailure(response, data);
    throw apiError((data && data.error) || ("HTTP " + response.status), response.status, data);
  } finally {
    if (controller) releaseController(controller);
  }
}

export async function api(method, path, body, signal) {
  const controller = signal ? null : trackController(new AbortController());
  const options = { method, headers: {}, signal: signal || controller.signal };
  try {
    // 存储不可用时按无令牌处理（本地访问豁免；远程访问会走 401 令牌层）。
    Object.assign(options.headers, authHeaders());
    if (body !== undefined) {
      options.headers["Content-Type"] = "application/json";
      options.body = JSON.stringify(body);
    }
    const response = await fetch(path, options);
    if (response.status === 204) return null;
    const data = await response.json().catch(() => null);
    if (isAuthFailure(response, data)) throw handleAuthFailure(response, data);
    if (!response.ok) {
      throw apiError((data && data.error) || ("HTTP " + response.status), response.status, data);
    }
    return data;
  } finally {
    if (controller) releaseController(controller);
  }
}
