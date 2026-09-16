import { afterEach, describe, expect, it, vi } from "vitest";
import { disposePage, enterPage, state } from "./page-state";
import { openEventStream, parseSseChunks } from "./events";

describe("realtime SSE transport", () => {
  afterEach(() => {
    disposePage();
    vi.restoreAllMocks();
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
    const pageToken = enterPage("dispatch");
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
});
