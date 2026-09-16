import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { disposePage, enterPage, state } from "./page-state";
import { openEventStream, parseSseChunks } from "./events";

describe("realtime SSE transport", () => {
  beforeEach(() => {
    vi.useFakeTimers();
    enterPage("dispatch");
    localStorage.setItem("nexus-token", "test-token");
    (window as Window & { __showTokenPrompt?: () => void }).__showTokenPrompt = vi.fn();
  });

  afterEach(() => {
    disposePage();
    localStorage.removeItem("nexus-token");
    vi.restoreAllMocks();
    vi.useRealTimers();
  });

  it("parses split CRLF frames, comments, ids, and multiline data", () => {
    expect(parseSseChunks([
      ": keep-alive\r\n",
      "id: 7\r\nevent: run.status\r\ndata: {\"line\":\r\n",
      "data: \"one\"}\r\n\r\n",
      "event: run.log\n",
      "data: first\n",
      "data: second\n\n",
    ])).toEqual([
      { event: "run.status", data: "{\"line\":\n\"one\"}", id: "7" },
      { event: "run.log", data: "first\nsecond", id: "7" },
    ]);
  });

  it("uses TextDecoder streaming and dispatches ready plus runtime events", async () => {
    const pageToken = state.routeToken;
    const frame = [
      "event: stream.ready\n",
      "data: {\"schemaVersion\":1,\"sequence\":1,\"data\":{}}\n\n",
      "event: run.status\n",
      "data: {\"schemaVersion\":1,\"sequence\":2,\"data\":{\"runId\":\"run-1\",\"targetName\":\"测试\"}}\n\n",
    ].join("");
    const encoded = new TextEncoder().encode(frame);
    const response = new Response(new ReadableStream<Uint8Array>({
      start(controller) {
        controller.enqueue(encoded.slice(0, 11));
        controller.enqueue(encoded.slice(11, 32));
        controller.enqueue(encoded.slice(32));
        controller.close();
      },
    }), { status: 200, headers: { "Content-Type": "text/event-stream" } });
    const fetchMock = vi.spyOn(globalThis, "fetch").mockResolvedValue(response);
    const ready = vi.fn();
    const received: string[] = [];
    const handle = openEventStream({
      page: "dispatch",
      token: pageToken,
      onReady: ready,
      onEvent: event => {
        received.push(`${event.type}:${String(event.data?.runId || "")}`);
      },
    });

    await handle.done;
    expect(fetchMock).toHaveBeenCalledWith("/api/events", expect.objectContaining({ method: "GET", cache: "no-store" }));
    expect(ready).toHaveBeenCalledTimes(1);
    expect(received).toEqual(["run.status:run-1"]);
    expect(state.controllers.size).toBe(1);
    handle.close();
  });

  it.each([
    [401, { code: "auth_required" }],
    [403, { code: "operation_forbidden" }],
  ])("treats HTTP %s as fatal without fallback polling or retry", async (status, payload) => {
    const fetchMock = vi.spyOn(globalThis, "fetch").mockResolvedValue(
      new Response(JSON.stringify(payload), { status, headers: { "Content-Type": "application/json" } }),
    );
    const callbacks: string[] = [];
    const handle = openEventStream({
      page: "dispatch",
      token: state.routeToken,
      onDisconnected: () => callbacks.push("disconnected"),
      onFatal: reason => {
        callbacks.push(`fatal:${(reason as { status?: number }).status || 0}`);
      },
    });

    await handle.done;
    expect(fetchMock).toHaveBeenCalledTimes(1);
    expect(callbacks).toEqual([`fatal:${status}`]);
    expect(state.timers.size).toBe(0);
    expect(state.controllers.size).toBe(0);
  });

  it("retries HTTP 500 after notifying disconnection", async () => {
    const fetchMock = vi.spyOn(globalThis, "fetch").mockImplementation(async () =>
      new Response(JSON.stringify({ code: "server_error" }), {
        status: 500,
        headers: { "Content-Type": "application/json" },
      }));
    const callbacks: string[] = [];
    const handle = openEventStream({
      page: "dispatch",
      token: state.routeToken,
      onDisconnected: () => callbacks.push("disconnected"),
      onFatal: () => callbacks.push("fatal"),
    });

    await handle.done;
    expect(fetchMock).toHaveBeenCalledTimes(1);
    expect(callbacks).toEqual(["disconnected"]);
    expect(state.timers.size).toBe(1);

    await vi.advanceTimersByTimeAsync(499);
    expect(fetchMock).toHaveBeenCalledTimes(1);
    await vi.advanceTimersByTimeAsync(1);
    await Promise.resolve();
    expect(fetchMock).toHaveBeenCalledTimes(2);
    expect(callbacks).toEqual(["disconnected", "disconnected"]);
    expect(callbacks).not.toContain("fatal");
    handle.close();
  });

  it("retries a network failure after notifying disconnection", async () => {
    const fetchMock = vi.spyOn(globalThis, "fetch").mockRejectedValue(new TypeError("network down"));
    const disconnected = vi.fn();
    const handle = openEventStream({
      page: "dispatch",
      token: state.routeToken,
      onDisconnected: disconnected,
    });

    await handle.done;
    expect(fetchMock).toHaveBeenCalledTimes(1);
    expect(disconnected).toHaveBeenCalledTimes(1);
    expect(state.timers.size).toBe(1);
    handle.close();
    await vi.runOnlyPendingTimersAsync();
    expect(fetchMock).toHaveBeenCalledTimes(1);
  });

  it("does not invoke callbacks or schedule retry when the stream is aborted", async () => {
    const external = new AbortController();
    const callbacks = { disconnected: vi.fn(), fatal: vi.fn() };
    const fetchMock = vi.spyOn(globalThis, "fetch").mockImplementation((_input, init) =>
      new Promise<Response>((_resolve, reject) => {
        init?.signal?.addEventListener("abort", () => reject(new DOMException("aborted", "AbortError")), { once: true });
      }));
    const handle = openEventStream({
      page: "dispatch",
      token: state.routeToken,
      signal: external.signal,
      onDisconnected: callbacks.disconnected,
      onFatal: callbacks.fatal,
    });

    await Promise.resolve();
    external.abort();
    await handle.done;
    expect(fetchMock).toHaveBeenCalledTimes(1);
    expect(callbacks.disconnected).not.toHaveBeenCalled();
    expect(callbacks.fatal).not.toHaveBeenCalled();
    expect(state.timers.size).toBe(0);
  });

  it("resyncs a missed stream event before handling the disconnect", async () => {
    const frame = "event: stream.missed\ndata: {\"schemaVersion\":1,\"sequence\":4,\"data\":{}}\n\n";
    const response = new Response(new ReadableStream<Uint8Array>({
      start(controller) {
        controller.enqueue(new TextEncoder().encode(frame));
        controller.close();
      },
    }), { status: 200, headers: { "Content-Type": "text/event-stream" } });
    vi.spyOn(globalThis, "fetch").mockResolvedValue(response);
    const callbacks: string[] = [];
    const handle = openEventStream({
      page: "dispatch",
      token: state.routeToken,
      onMissed: async () => { callbacks.push("missed"); },
      onDisconnected: () => callbacks.push("disconnected"),
    });

    await handle.done;
    expect(callbacks).toEqual(["missed", "disconnected"]);
    handle.close();
  });
});
