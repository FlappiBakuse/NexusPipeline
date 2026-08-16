import { trackController, releaseController } from "./state.js";

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
