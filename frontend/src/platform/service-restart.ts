import { api } from "./api";
import { CONTROL_SERVICE_NAME } from "./service-identity";

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
  controlApiVersion?: string;
  version?: string;
  frontendBuildId?: string;
  ready?: boolean;
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

/** 按端口构造探测根地址：只保留协议与主机，避免把页面路径与 hash 路由带进 API 请求。 */
export function restartProbeUrl(href: string, port: number): string {
  const url = new URL(href);
  if (port > 0) url.port = String(port);
  url.pathname = "/";
  url.search = "";
  url.hash = "";
  return url.toString();
}

function statusPayload(value: unknown): ServiceInstanceProbe | null {
  const payload = value as { service?: unknown; controlApiVersion?: unknown; instanceId?: unknown; restartHandoffId?: unknown; actualPort?: unknown; ready?: unknown; version?: unknown; frontendBuildId?: unknown } | null;
  if (!payload || typeof payload !== "object") return null;
  if (String(payload.service || "") !== CONTROL_SERVICE_NAME) return null;
  if (!String(payload.controlApiVersion || "") || payload.ready !== true) return null;
  const instanceId = String(payload.instanceId || "");
  if (!instanceId) return null;
  const version = String(payload.version || "");
  if (!version) return null;
  const actualPort = Number(payload.actualPort);
  if (!Number.isInteger(actualPort) || actualPort < 1 || actualPort > 65535) return null;
  return {
    instanceId,
    restartHandoffId: String(payload.restartHandoffId || ""),
    actualPort,
    controlApiVersion: String(payload.controlApiVersion || ""),
    version,
    frontendBuildId: String(payload.frontendBuildId || ""),
    ready: true,
  };
}

/**
 * 读取目标主机端口的服务状态；应答不是 NexusPipeline、或不是本次重启拉起的新实例时返回 null。
 * 探测地址只使用协议与主机，页面路径与 hash 路由不会进入 API 请求。
 */
export async function probeServiceInstance(
  url: string,
  handoff: ServiceRestartHandoff,
  signal?: AbortSignal): Promise<ServiceInstanceProbe | null> {
  const controller = new AbortController();
  const onAbort = () => controller.abort();
  signal?.addEventListener("abort", onAbort, { once: true });
  if (signal?.aborted) controller.abort();
  const timeout = setTimeout(() => controller.abort(), 2000);
  const origin = new URL(url, location.href);
  origin.pathname = "/";
  origin.search = "";
  origin.hash = "";
  try {
    const response = await fetch(new URL("api/status?view=identity", origin).toString(), {
      method: "GET",
      cache: "no-store",
      signal: controller.signal,
    });
    if (!response.ok) return null;
    const instance = statusPayload(await response.json().catch(() => null));
    if (!instance) return null;
    if (handoff.handoffId && instance.restartHandoffId !== handoff.handoffId) return null;
    if (handoff.previousInstanceId && instance.instanceId === handoff.previousInstanceId) return null;
    return instance;
  } catch {
    return null;
  } finally {
    clearTimeout(timeout);
    signal?.removeEventListener("abort", onAbort);
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
export async function waitForRestartedService(options: ServiceRecoveryOptions): Promise<ServiceInstanceProbe | null> {
  const timeoutMs = Math.max(1000, Number(options.timeoutMs) || 120_000);
  const baseInterval = Math.max(200, Number(options.intervalMs) || 800);
  const maxInterval = Math.max(baseInterval, Number(options.maxIntervalMs) || 2500);
  const limit = Number.isFinite(Number(options.portScanLimit)) ? Math.max(0, Number(options.portScanLimit)) : 20;
  const probe = options.probe || probeServiceInstance;
  const ports = restartCandidatePorts(options.href, options.handoff, limit);
  if (ports.length === 0) return null;
  const deadline = Date.now() + timeoutMs;
  let delay = baseInterval;
  let firstProbe = true;
  while (Date.now() < deadline) {
    if (options.signal?.aborted) return null;
    if (!firstProbe) {
      try {
        await sleep(delay, options.signal);
      } catch {
        return null;
      }
    }
    firstProbe = false;
    const results = await Promise.all(ports.map(async port => {
      try {
        return await probe(restartProbeUrl(options.href, port), options.handoff, options.signal);
      } catch {
        return null;
      }
    }));
    const ready = results.find(result => result !== null);
    if (ready) return ready;
    delay = Math.min(maxInterval, Math.round(delay * 1.4));
  }
  return null;
}

/**
 * 跳转到重启后的服务地址：地址与当前页面完全一致时执行刷新。
 * 同地址的 `location.replace` 被浏览器视为同文档导航，页面不会重新加载。
 */
export function applyRestartNavigation(url: string, current: string): void {
  let target: URL;
  try {
    target = new URL(url, current);
  } catch {
    window.location.reload();
    return;
  }
  const currentUrl = new URL(current);
  if (target.origin === currentUrl.origin && target.pathname === currentUrl.pathname && target.search === currentUrl.search) {
    window.location.reload();
    return;
  }
  window.location.replace(target.href);
}
