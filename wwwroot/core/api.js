import { trackController, releaseController } from "./state.js";

const iconUrlCache = new Map();

/** v0.7.5（KN-06）：远程访问下图标 API 需要 Bearer 头（`<img>` 无法携带，远程模式图标必 401）——
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
        let token = null;
        try {
          token = localStorage.getItem("nexus-token");
        } catch {
          token = null;
        }
        const res = await fetch("/api/scripts/" + id + "/icon", { headers: token ? { Authorization: "Bearer " + token } : {} });
        if (!res.ok) continue;
        const blob = await res.blob();
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

export async function api(method, path, body, signal) {
  const controller = signal ? null : trackController(new AbortController());
  const options = { method, headers: {}, signal: signal || controller.signal };
  try {
    // v0.7.4（KN-45）：存储不可用时按无令牌处理（本地访问豁免；远程访问会走 401 令牌层）。
    let token = null;
    try {
      token = localStorage.getItem("nexus-token");
    } catch {
      token = null;
    }
    if (token) options.headers["Authorization"] = "Bearer " + token;
    if (body !== undefined) {
      options.headers["Content-Type"] = "application/json";
      options.body = JSON.stringify(body);
    }
    const response = await fetch(path, options);
    if (response.status === 204) return null;
    const data = await response.json().catch(() => null);
    if (response.status === 401 || (data && data.error && String(data.error).includes("访问令牌"))) {
      try {
        localStorage.removeItem("nexus-token");
      } catch {
      }
      if (typeof window.__showTokenPrompt === "function") window.__showTokenPrompt();
      throw new Error((data && data.error) || "需要访问令牌");
    }
    if (!response.ok) {
      throw new Error((data && data.error) || ("HTTP " + response.status));
    }
    return data;
  } finally {
    if (controller) releaseController(controller);
  }
}
