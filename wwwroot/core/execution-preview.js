function authHeaders() {
  const headers = {};
  try {
    const token = localStorage.getItem("nexus-token");
    if (token) headers.Authorization = "Bearer " + token;
  } catch {
    // 本地存储不可用时按无令牌请求，服务端仍会执行本地访问策略。
  }
  return headers;
}

function unauthorized(message) {
  try { localStorage.removeItem("nexus-token"); } catch { /* ignore */ }
  if (typeof window.__showTokenPrompt === "function") window.__showTokenPrompt();
  return new Error(message || "需要访问令牌");
}

export async function captureExecutionPreview(runId, pluginName, signal) {
  const id = String(runId || "").trim();
  const plugin = String(pluginName || "").trim();
  if (!id || !plugin) throw new TypeError("实时截图目标无效");
  const response = await fetch(
    `/api/execution-preview/${encodeURIComponent(id)}?plugin=${encodeURIComponent(plugin)}`,
    { headers: authHeaders(), cache: "no-store", signal });
  if (response.status === 204) {
    return {
      state: response.headers.get("X-Nexus-Preview-State") || "waiting_for_game",
      capturedAt: null,
      source: response.headers.get("X-Nexus-Preview-Source") || "",
      url: null,
    };
  }
  if (response.status === 401) {
    throw unauthorized("需要访问令牌");
  }
  if (!response.ok) {
    const data = await response.json().catch(() => null);
    throw new Error(data?.error || `HTTP ${response.status}`);
  }
  const blob = await response.blob();
  if (!blob.type.startsWith("image/")) throw new Error("实时截图响应格式无效");
  return {
    state: "ready",
    capturedAt: response.headers.get("X-Nexus-Preview-Captured-At") || "",
    source: response.headers.get("X-Nexus-Preview-Source") || "",
    url: URL.createObjectURL(blob),
  };
}
