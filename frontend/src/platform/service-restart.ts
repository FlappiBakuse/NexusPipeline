import { api, readAuthToken } from "./api";

/**
 * 服务重启恢复协议。
 *
 * 重启接口返回本次重启的交接标识与旧实例标识；新实例在 `/api/status` 中同时暴露自身实例标识与
 * 接管到的交接标识。恢复探测因此能区分三种应答：旧 NexusPipeline 实例、本次重启拉起的新实例，
 * 以及与 NexusPipeline 无关的 HTTP 服务。配置端口被占用时宿主按 +1 顺延，探测覆盖这段候选端口，
 * 最终跳转地址使用新实例上报的实际监听端口。
 */

/** 重启接口返回的交接信息。 */
export interface ServiceRestartHandoff {
  /** 新实例优先尝试的配置端口；0 表示宿主未返回端口。 */
  newPort: number;
  /** 本次重启的交接标识，只有被本次重启拉起的实例才会携带。 */
  handoffId: string;
  /** 提交重启的旧实例标识。 */
  previousInstanceId: string;
}

/** `/api/status` 中与重启恢复相关的字段。 */
export interface ServiceInstanceProbe {
  instanceId: string;
  restartHandoffId: string;
  actualPort: number;
}

export interface ServiceRecoveryOptions {
  href: string;
  handoff: ServiceRestartHandoff;
  signal?: AbortSignal;
  timeoutMs?: number;
  intervalMs?: number;
  maxIntervalMs?: number;
  /** 配置端口之后的候选端口数量，与宿主启动时的端口顺延次数一致。 */
  portScanLimit?: number;
  probe?: (url: string, handoff: ServiceRestartHandoff, signal?: AbortSignal) => Promise<ServiceInstanceProbe | null>;
}

export interface ServiceRecoveryResult {
  url: string;
  instance: ServiceInstanceProbe;
}

export type ServiceRestartOutcome = "ready" | "timeout" | "failed";

const SERVICE_NAME = "NexusPipeline";

/** 请求宿主重启服务；返回新实例的候选端口、交接标识与旧实例标识。 */
export async function requestServiceRestart(signal?: AbortSignal): Promise<ServiceRestartHandoff> {
  const response = await api<{ newPort?: number; handoffId?: string; instanceId?: string }>(
    "POST",
    "/api/settings/restart",
    undefined,
    signal);
  const newPort = Number(response?.newPort);
  return {
    newPort: Number.isFinite(newPort) && newPort > 0 ? newPort : 0,
    handoffId: String(response?.handoffId || ""),
    previousInstanceId: String(response?.instanceId || ""),
  };
}

/** 按端口构造跳转地址，保留协议、主机名、路径与 hash 路由。 */
export function restartTargetUrl(href: string, port: number): string {
  const url = new URL(href);
  if (port > 0) url.port = String(port);
  return url.toString();
}

function statusPayload(value: unknown): ServiceInstanceProbe | null {
  const payload = value as { service?: unknown; instanceId?: unknown; restartHandoffId?: unknown; actualPort?: unknown } | null;
  if (!payload || typeof payload !== "object") return null;
  if (String(payload.service || "") !== SERVICE_NAME) return null;
  const instanceId = String(payload.instanceId || "");
  if (!instanceId) return null;
  const actualPort = Number(payload.actualPort);
  return {
    instanceId,
    restartHandoffId: String(payload.restartHandoffId || ""),
    actualPort: Number.isFinite(actualPort) && actualPort > 0 ? actualPort : 0,
  };
}

/** 读取目标地址的状态；应答不是 NexusPipeline、或不是本次重启拉起的新实例时返回 null。 */
export async function probeServiceInstance(
  url: string,
  handoff: ServiceRestartHandoff,
  signal?: AbortSignal): Promise<ServiceInstanceProbe | null> {
  const timeout = typeof AbortSignal !== "undefined" && typeof AbortSignal.timeout === "function"
    ? AbortSignal.timeout(2000)
    : undefined;
  const combined = signal && timeout && typeof AbortSignal.any === "function"
    ? AbortSignal.any([signal, timeout])
    : signal || timeout;
  const token = readAuthToken();
  try {
    const response = await fetch(`${url.replace(/\/+$/u, "")}/api/status`, {
      method: "GET",
      cache: "no-store",
      signal: combined,
      headers: token ? { Authorization: `Bearer ${token}` } : {},
    });
    if (!response.ok) return null;
    const instance = statusPayload(await response.json().catch(() => null));
    if (!instance) return null;
    if (handoff.handoffId && instance.restartHandoffId !== handoff.handoffId) return null;
    if (handoff.previousInstanceId && instance.instanceId === handoff.previousInstanceId) return null;
    return instance;
  } catch {
    return null;
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

/** 候选端口：配置端口与宿主启动时顺延的后续端口；配置端口未知时只探测当前页面端口。 */
export function restartCandidatePorts(href: string, handoff: ServiceRestartHandoff, limit: number): number[] {
  const current = Number(new URL(href).port) || 0;
  if (handoff.newPort <= 0) return current > 0 ? [current] : [];
  const ports: number[] = [];
  for (let offset = 0; offset <= Math.max(0, limit); offset += 1) {
    const port = handoff.newPort + offset;
    if (port > 65535) break;
    ports.push(port);
  }
  return ports;
}

/**
 * 等待本次重启的新实例接管服务：按候选端口探测，只接受携带本次交接标识、且实例标识不同于旧实例的应答。
 * 超时返回 null，由调用方呈现"服务仍在启动"与重试入口。
 */
export async function waitForRestartedService(options: ServiceRecoveryOptions): Promise<ServiceRecoveryResult | null> {
  const timeoutMs = Math.max(1000, Number(options.timeoutMs) || 40_000);
  const baseInterval = Math.max(200, Number(options.intervalMs) || 800);
  const maxInterval = Math.max(baseInterval, Number(options.maxIntervalMs) || 2500);
  const limit = Number.isFinite(Number(options.portScanLimit)) ? Math.max(0, Number(options.portScanLimit)) : 20;
  const probe = options.probe || probeServiceInstance;
  const ports = restartCandidatePorts(options.href, options.handoff, limit);
  if (ports.length === 0) return null;
  const deadline = Date.now() + timeoutMs;
  let delay = baseInterval;
  while (Date.now() < deadline) {
    try {
      await sleep(delay, options.signal);
    } catch {
      return null;
    }
    const results = await Promise.all(ports.map(async port => {
      const target = restartTargetUrl(options.href, port);
      try {
        return { port, target, instance: await probe(target, options.handoff, options.signal) };
      } catch {
        return { port, target, instance: null };
      }
    }));
    const ready = results.find(result => result.instance !== null);
    if (ready && ready.instance) {
      const actualPort = ready.instance.actualPort > 0 ? ready.instance.actualPort : ready.port;
      return { url: restartTargetUrl(options.href, actualPort), instance: ready.instance };
    }
    delay = Math.min(maxInterval, Math.round(delay * 1.4));
  }
  return null;
}

export interface RestartServiceOptions extends Omit<ServiceRecoveryOptions, "href" | "handoff"> {
  navigate?: (url: string) => void;
  /** 当前页面地址；省略时使用浏览器地址，跳转保留其路径与 hash 路由。 */
  href?: string;
  /** 重试时复用上一次重启的交接信息，避免对已经退出的旧实例重复提交重启请求。 */
  handoff?: ServiceRestartHandoff;
  onHandoff?: (handoff: ServiceRestartHandoff) => void;
}

/**
 * 完整重启流程：提交重启（或复用已有交接信息）→ 探测新实例 → 顶层跳转到实际监听端口。
 * 超时返回 timeout，由调用方保留交接信息并提供重试入口。
 */
export async function restartService(options: RestartServiceOptions = {}): Promise<ServiceRestartOutcome> {
  let handoff = options.handoff || null;
  if (!handoff) {
    try {
      handoff = await requestServiceRestart(options.signal);
    } catch {
      return "failed";
    }
    options.onHandoff?.(handoff);
  }
  const href = options.href || window.location.href;
  const recovered = await waitForRestartedService({ ...options, href, handoff });
  if (!recovered) return "timeout";
  const navigate = options.navigate || ((url: string) => window.location.replace(url));
  navigate(recovered.url);
  return "ready";
}
