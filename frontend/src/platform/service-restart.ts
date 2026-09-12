import { api } from "./api";

/** 服务重启的探测与跳转编排；重启协议本身由宿主 `/api/settings/restart` 提供。 */
export interface ServiceRestartRequest {
  newPort: number;
}

export interface ServiceRecoveryOptions {
  targetUrl: string;
  signal?: AbortSignal;
  timeoutMs?: number;
  intervalMs?: number;
  maxIntervalMs?: number;
  probe?: (url: string, signal?: AbortSignal) => Promise<boolean>;
}

export type ServiceRestartOutcome = "ready" | "timeout" | "failed";

/** 请求宿主重启服务；返回新端口（未变化时与当前端口相同）。 */
export async function requestServiceRestart(signal?: AbortSignal): Promise<ServiceRestartRequest> {
  const response = await api<{ newPort?: number }>("POST", "/api/settings/restart", undefined, signal);
  const newPort = Number(response?.newPort);
  return { newPort: Number.isFinite(newPort) && newPort > 0 ? newPort : 0 };
}

/** 按新端口构造跳转地址，保留协议、主机名、路径与 hash 路由。 */
export function restartTargetUrl(href: string, newPort: number): string {
  const url = new URL(href);
  if (newPort > 0) url.port = String(newPort);
  return url.toString();
}

/** 连通性探测：只判断目标地址是否有服务应答，不依赖认证头或跨端口读取响应。 */
export async function probeService(url: string, signal?: AbortSignal): Promise<boolean> {
  const timeout = typeof AbortSignal !== "undefined" && typeof AbortSignal.timeout === "function"
    ? AbortSignal.timeout(2000)
    : undefined;
  const combined = signal && timeout && typeof AbortSignal.any === "function"
    ? AbortSignal.any([signal, timeout])
    : signal || timeout;
  try {
    await fetch(url, { mode: "no-cors", cache: "no-store", signal: combined });
    return true;
  } catch {
    return false;
  }
}

function sleep(delayMs: number, signal?: AbortSignal): Promise<void> {
  return new Promise((resolve, reject) => {
    if (signal?.aborted) {
      reject(new DOMException("", "AbortError"));
      return;
    }
    const handle = setTimeout(() => {
      signal?.removeEventListener("abort", onAbort);
      resolve();
    }, delayMs);
    function onAbort() {
      clearTimeout(handle);
      reject(new DOMException("", "AbortError"));
    }
    signal?.addEventListener("abort", onAbort, { once: true });
  });
}

/** 等待服务恢复：有限次数探测 + 退避间隔，连续两次成功才认为新服务已经稳定。 */
export async function waitForServiceRecovery(options: ServiceRecoveryOptions): Promise<boolean> {
  const timeoutMs = Math.max(1000, Number(options.timeoutMs) || 40_000);
  const baseInterval = Math.max(200, Number(options.intervalMs) || 800);
  const maxInterval = Math.max(baseInterval, Number(options.maxIntervalMs) || 2500);
  const probe = options.probe || probeService;
  const deadline = Date.now() + timeoutMs;
  let delay = baseInterval;
  let successes = 0;
  while (Date.now() < deadline) {
    try {
      await sleep(delay, options.signal);
    } catch {
      return false;
    }
    let ready = false;
    try {
      ready = await probe(options.targetUrl, options.signal);
    } catch {
      ready = false;
    }
    successes = ready ? successes + 1 : 0;
    if (successes >= 2) return true;
    delay = Math.min(maxInterval, Math.round(delay * 1.4));
  }
  return false;
}

export interface RestartServiceOptions extends Omit<ServiceRecoveryOptions, "targetUrl"> {
  navigate?: (url: string) => void;
}

/**
 * 完整重启流程：请求重启 → 计算新地址 → 等待服务恢复 → 顶层跳转。
 * 超时返回 timeout，由调用方呈现“服务仍在启动”和手动重试入口。
 */
export async function restartService(options: RestartServiceOptions = {}): Promise<ServiceRestartOutcome> {
  let request: ServiceRestartRequest;
  try {
    request = await requestServiceRestart(options.signal);
  } catch {
    return "failed";
  }
  const targetUrl = restartTargetUrl(window.location.href, request.newPort);
  const recovered = await waitForServiceRecovery({ ...options, targetUrl });
  if (!recovered) return "timeout";
  const navigate = options.navigate || ((url: string) => window.location.replace(url));
  navigate(targetUrl);
  return "ready";
}
