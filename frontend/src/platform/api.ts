import { releaseController, trackController } from "./page-state";
import { getLocale, t } from "./i18n";

export interface ApiError extends Error {
  code: string | null;
  status: number;
  data: unknown;
}

export interface NexusAuthWindow extends Window {
  __showTokenPrompt?: () => void;
}

function apiError(message: string, status: number, data: unknown): ApiError {
  const error = new Error(message) as ApiError;
  error.code = (data as { code?: string } | null)?.code ?? null;
  error.status = status;
  error.data = data;
  return error;
}

/**
 * 浏览器、WebView 和不同 fetch 实现对 AbortController 的错误消息并不一致。
 * 统一通过 name/code/message 判断，避免把页面生命周期取消误报成业务错误。
 */
export function isAbortError(reason: unknown): boolean {
  if (!reason) return false;
  const candidate = reason as { name?: string; code?: number; message?: string };
  if (candidate.name === "AbortError" || candidate.name === "CanceledError" || candidate.code === 20) return true;
  const message = typeof candidate.message === "string" ? candidate.message : String(reason);
  return /\b(?:abort|aborted|cancell?ed)\b/i.test(message);
}

function normalizeAbortError(reason: unknown, signal?: AbortSignal | null): unknown {
  if (!signal?.aborted && !isAbortError(reason)) return reason;
  if (typeof DOMException === "function") return new DOMException("", "AbortError");
  const error = new Error();
  error.name = "AbortError";
  return error;
}

function formatApiError(data: unknown, status: number): string {
  const payload = data as { code?: string; args?: Record<string, unknown> } | null;
  const code = payload?.code;
  if (code) {
    return t(`api.error.${code}`, payload?.args || {}, code);
  }
  return t("api.error.http", { status }, `HTTP ${status}`);
}

function readAuthToken(): string | null {
  try {
    return localStorage.getItem("nexus-token");
  } catch {
    return null;
  }
}

function authHeaders(): Record<string, string> {
  const token = readAuthToken();
  return token
    ? { Authorization: "Bearer " + token, "X-Nexus-Locale": getLocale() }
    : { "X-Nexus-Locale": getLocale() };
}

function isAuthFailure(response: Response, data: unknown): boolean {
  return response.status === 401
    || response.headers.get("X-Nexus-Auth") === "required"
    || (data as { code?: string } | null)?.code === "auth_required";
}

function handleAuthFailure(response: Response, data: unknown): ApiError {
  try {
    localStorage.removeItem("nexus-token");
  } catch {
  }
  const authWindow = window as NexusAuthWindow;
  if (typeof authWindow.__showTokenPrompt === "function") authWindow.__showTokenPrompt();
  return apiError(formatApiError(data || { code: "auth_required" }, response.status), response.status, data);
}

/** 获取受保护的二进制资源。调用方应在资源不再使用时释放返回 blob 对应的 ObjectURL。 */
export async function apiBlob(path: string, signal?: AbortSignal | null): Promise<Blob> {
  const controller = signal ? null : trackController(new AbortController());
  try {
    const response = await fetch(path, {
      method: "GET",
      headers: authHeaders(),
      signal: signal || controller!.signal,
      cache: "no-store",
    });
    if (response.status === 204) throw apiError(t("api.error.binary_not_found", {}, "Binary resource not found"), response.status, null);
    if (response.ok && response.headers.get("X-Nexus-Auth") !== "required") return await response.blob();
    const data = await response.json().catch(() => null);
    if (isAuthFailure(response, data)) throw handleAuthFailure(response, data);
    throw apiError(formatApiError(data, response.status), response.status, data);
  } catch (reason) {
    throw normalizeAbortError(reason, signal);
  } finally {
    if (controller) releaseController(controller);
  }
}

export async function api<T = unknown>(method: string, path: string, body?: unknown, signal?: AbortSignal | null): Promise<T> {
  const controller = signal ? null : trackController(new AbortController());
  const options: RequestInit = { method, headers: {}, signal: signal || controller!.signal };
  try {
    // 存储不可用时按无令牌处理（本地访问豁免；远程访问会走 401 令牌层）。
    Object.assign(options.headers as Record<string, string>, authHeaders());
    if (body !== undefined) {
      (options.headers as Record<string, string>)["Content-Type"] = "application/json";
      options.body = JSON.stringify(body);
    }
    const response = await fetch(path, options);
    if (response.status === 204) return null as T;
    const data = await response.json().catch(() => null);
    if (isAuthFailure(response, data)) throw handleAuthFailure(response, data);
    if (!response.ok) {
      throw apiError(formatApiError(data, response.status), response.status, data);
    }
    return data as T;
  } catch (reason) {
    throw normalizeAbortError(reason, signal);
  } finally {
    if (controller) releaseController(controller);
  }
}

/** 发送二进制请求体并读取 JSON 响应；用于受保护的二进制上传。 */
export async function apiUpload<T = unknown>(
  method: string,
  path: string,
  body: Blob | ArrayBuffer | string,
  contentType = "application/octet-stream",
  signal?: AbortSignal | null): Promise<T> {
  const controller = signal ? null : trackController(new AbortController());
  try {
    const response = await fetch(path, {
      method,
      headers: { ...authHeaders(), "Content-Type": contentType },
      body: body as BodyInit,
      signal: signal || controller!.signal,
    });
    if (response.status === 204) return null as T;
    const data = await response.json().catch(() => null);
    if (isAuthFailure(response, data)) throw handleAuthFailure(response, data);
    if (!response.ok) {
      throw apiError(formatApiError(data, response.status), response.status, data);
    }
    return data as T;
  } catch (reason) {
    throw normalizeAbortError(reason, signal);
  } finally {
    if (controller) releaseController(controller);
  }
}
