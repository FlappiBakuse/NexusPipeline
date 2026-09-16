import { apiStream, isAbortError, type ApiError } from "./api";
import { t } from "./i18n";
import { isCurrent, schedule, state, trackController, releaseController } from "./page-state";

export interface ParsedSseEvent {
  event: string;
  data: string;
  id?: string;
}

export interface RealtimeEventEnvelope {
  schemaVersion?: number;
  sequence?: number;
  timestamp?: string;
  data?: Record<string, unknown>;
}

export interface RealtimeSseEvent extends RealtimeEventEnvelope {
  type: string;
}

export interface EventStreamOptions {
  path?: string;
  page?: string;
  token?: number;
  signal?: AbortSignal | null;
  onEvent?: (event: RealtimeSseEvent) => void | Promise<void>;
  onReady?: (event: RealtimeSseEvent) => void | Promise<void>;
  onDisconnected?: () => void;
  onMissed?: (event: RealtimeSseEvent) => void | Promise<void>;
  onFatal?: (reason: unknown) => void;
}

export interface EventStreamHandle {
  close: () => void;
  done: Promise<void>;
}

/** 可在任意 chunk 边界调用的 SSE parser；保留 event/data/id 语义，不依赖浏览器 EventSource。 */
export class SseParser {
  private buffer = "";

  private eventName = "";

  private dataLines: string[] = [];

  private lastEventId: string | undefined;

  private readonly emit: (event: ParsedSseEvent) => void;

  public constructor(emit: (event: ParsedSseEvent) => void) {
    this.emit = emit;
  }

  public push(chunk: string): void {
    this.buffer += chunk;
    let newline = this.buffer.indexOf("\n");
    while (newline >= 0) {
      let line = this.buffer.slice(0, newline);
      this.buffer = this.buffer.slice(newline + 1);
      if (line.endsWith("\r")) line = line.slice(0, -1);
      this.processLine(line);
      newline = this.buffer.indexOf("\n");
    }
  }

  public finish(): void {
    if (this.buffer.length > 0) {
      const line = this.buffer.endsWith("\r") ? this.buffer.slice(0, -1) : this.buffer;
      this.buffer = "";
      this.processLine(line);
    }
    this.dispatch();
  }

  private processLine(line: string): void {
    if (line.length === 0) {
      this.dispatch();
      return;
    }
    if (line.startsWith(":")) return;
    const separator = line.indexOf(":");
    const field = separator >= 0 ? line.slice(0, separator) : line;
    let value = separator >= 0 ? line.slice(separator + 1) : "";
    if (value.startsWith(" ")) value = value.slice(1);
    switch (field) {
      case "event":
        this.eventName = value;
        break;
      case "data":
        this.dataLines.push(value);
        break;
      case "id":
        if (!value.includes("\u0000")) this.lastEventId = value;
        break;
      default:
        break;
    }
  }

  private dispatch(): void {
    if (this.dataLines.length === 0) {
      this.eventName = "";
      return;
    }
    this.emit({
      event: this.eventName || "message",
      data: this.dataLines.join("\n"),
      ...(this.lastEventId === undefined ? {} : { id: this.lastEventId }),
    });
    this.eventName = "";
    this.dataLines = [];
  }
}

export function parseSseChunks(chunks: Iterable<string>): ParsedSseEvent[] {
  const events: ParsedSseEvent[] = [];
  const parser = new SseParser(event => events.push(event));
  for (const chunk of chunks) parser.push(chunk);
  parser.finish();
  return events;
}

const RETRY_DELAYS = [500, 1000, 2000, 5000, 10000];

/**
 * 创建页面范围的 SSE 连接。连接控制器由 page-state 托管，路由离开时会自动中止；
 * 断线使用有限指数退避，认证失败和其他 4xx 直接交给页面处理。
 */
export function openEventStream(options: EventStreamOptions): EventStreamHandle {
  const page = options.page || state.page;
  const token = options.token ?? state.routeToken;
  const controller = trackController(new AbortController());
  let closed = false;
  let retryIndex = 0;
  let retryTimer: ReturnType<typeof setTimeout> | null = null;
  let externalAbortListener: (() => void) | null = null;

  if (options.signal) {
    externalAbortListener = () => controller.abort();
    if (options.signal.aborted) controller.abort();
    else options.signal.addEventListener("abort", externalAbortListener, { once: true });
  }

  const isUsable = () => !closed && !controller.signal.aborted && isCurrent(page, token);

  const close = () => {
    if (closed) return;
    closed = true;
    if (retryTimer) {
      clearTimeout(retryTimer);
      state.timers.delete(retryTimer);
      retryTimer = null;
    }
    controller.abort();
    if (options.signal && externalAbortListener) options.signal.removeEventListener("abort", externalAbortListener);
    releaseController(controller);
  };

  const handleEvent = async (event: RealtimeSseEvent) => {
    if (!isUsable()) return;
    if (event.type === "stream.ready") {
      retryIndex = 0;
      await options.onReady?.(event);
      return;
    }
    if (event.type === "stream.missed") {
      await options.onMissed?.(event);
      return;
    }
    await options.onEvent?.(event);
  };

  const consume = async (response: Response) => {
    if (!response.body) throw new Error(t("api.error.stream_body_unavailable"));
    const reader = response.body.getReader();
    const decoder = new TextDecoder();
    let dispatchChain = Promise.resolve();
    const parser = new SseParser(event => {
      let payload: RealtimeEventEnvelope;
      try {
        payload = JSON.parse(event.data) as RealtimeEventEnvelope;
      } catch {
        return;
      }
      dispatchChain = dispatchChain.then(() => handleEvent({ type: event.event, ...payload }));
    });
    try {
      while (isUsable()) {
        const result = await reader.read();
        if (result.done) break;
        parser.push(decoder.decode(result.value, { stream: true }));
      }
      parser.push(decoder.decode());
      parser.finish();
      await dispatchChain;
    } finally {
      reader.releaseLock();
    }
  };

  const scheduleRetry = () => {
    if (!isUsable()) return;
    const delay = RETRY_DELAYS[Math.min(retryIndex, RETRY_DELAYS.length - 1)];
    retryIndex = Math.min(retryIndex + 1, RETRY_DELAYS.length - 1);
    retryTimer = schedule(() => {
      retryTimer = null;
      void connect();
    }, delay, page, token);
  };

  const connect = async (): Promise<void> => {
    if (!isUsable()) return;
    try {
      const response = await apiStream(options.path || "/api/events", controller.signal);
      if (!isUsable()) return;
      await consume(response);
      if (!isUsable()) return;
      options.onDisconnected?.();
      scheduleRetry();
    } catch (reason) {
      if (!isUsable() || controller.signal.aborted || isAbortError(reason)) return;
      const status = (reason as Partial<ApiError>)?.status;
      if (typeof status === "number" && status >= 400 && status < 500) {
        options.onFatal?.(reason);
        close();
        return;
      }
      options.onDisconnected?.();
      scheduleRetry();
    }
  };

  const done = connect().then(() => undefined);
  return { close, done };
}
