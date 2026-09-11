import { t } from "./i18n";
import type { NexusAuthWindow } from "./api";

export interface ExecutionPreviewResult {
  state: string;
  capturedAt: string | null;
  source: string;
  url: string | null;
}

function authHeaders(): Record<string, string> {
  const headers: Record<string, string> = {};
  try {
    const token = localStorage.getItem("nexus-token");
    if (token) headers.Authorization = "Bearer " + token;
  } catch {
    // 本地存储不可用时按无令牌请求，服务端仍会执行本地访问策略。
  }
  return headers;
}

function unauthorized(message: string): Error {
  try { localStorage.removeItem("nexus-token"); } catch { /* ignore */ }
  const authWindow = window as NexusAuthWindow;
  if (typeof authWindow.__showTokenPrompt === "function") authWindow.__showTokenPrompt();
  return new Error(message || t("common.auth_required"));
}

export async function captureExecutionPreview(runId: string, pluginName: string, signal?: AbortSignal | null): Promise<ExecutionPreviewResult> {
  const id = String(runId || "").trim();
  const plugin = String(pluginName || "").trim();
  if (!id || !plugin) throw new TypeError(t("common.screenshot.target_invalid"));
  const response = await fetch(
    `/api/execution-preview/${encodeURIComponent(id)}?plugin=${encodeURIComponent(plugin)}`,
    { headers: authHeaders(), cache: "no-store", signal: signal ?? undefined });
  if (response.status === 204) {
    return {
      state: response.headers.get("X-Nexus-Preview-State") || "waiting_for_game",
      capturedAt: null,
      source: response.headers.get("X-Nexus-Preview-Source") || "",
      url: null,
    };
  }
  if (response.status === 401) {
    throw unauthorized(t("common.auth_required"));
  }
  if (!response.ok) {
    const data = await response.json().catch(() => null);
    throw new Error(data?.error || `HTTP ${response.status}`);
  }
  const blob = await response.blob();
  if (!blob.type.startsWith("image/")) throw new Error(t("common.screenshot.response_invalid"));
  return {
    state: "ready",
    capturedAt: response.headers.get("X-Nexus-Preview-Captured-At") || "",
    source: response.headers.get("X-Nexus-Preview-Source") || "",
    url: URL.createObjectURL(blob),
  };
}
