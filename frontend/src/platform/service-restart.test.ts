import { afterEach, describe, expect, it, vi } from "vitest";
import {
  probeServiceInstance,
  requestServiceRestart,
  restartCandidatePorts,
  restartService,
  restartTargetUrl,
  waitForRestartedService,
  type ServiceRestartHandoff,
} from "./service-restart";

/**
 * 重启恢复编排：实例身份判定、候选端口探测与跳转结果都在这一层验证。
 * 恢复必须确认应答来自本次重启拉起的新实例，任何无关 HTTP 服务与旧实例都不算恢复。
 */
const handoff: ServiceRestartHandoff = { newPort: 58001, handoffId: "handoff-1", previousInstanceId: "instance-old" };

function statusPayload(overrides: Record<string, unknown> = {}) {
  return {
    service: "NexusPipeline",
    instanceId: "instance-new",
    restartHandoffId: handoff.handoffId,
    actualPort: 58001,
    ...overrides,
  };
}

function stubStatus(reply: (url: string) => Response): void {
  vi.stubGlobal("fetch", vi.fn(async (input: RequestInfo | URL) => reply(String(input))));
}

afterEach(() => {
  vi.unstubAllGlobals();
});

describe("service restart orchestration", () => {
  it("keeps protocol, hostname, path and hash while switching to the new port", () => {
    expect(restartTargetUrl("http://127.0.0.1:8080/dashboard?tab=1#/history", 8090))
      .toBe("http://127.0.0.1:8090/dashboard?tab=1#/history");
    expect(restartTargetUrl("http://127.0.0.1:8080/#/plugins", 8090))
      .toBe("http://127.0.0.1:8090/#/plugins");
  });

  it("keeps the current address when the port is unknown", () => {
    expect(restartTargetUrl("http://127.0.0.1:8080/#/settings", 0))
      .toBe("http://127.0.0.1:8080/#/settings");
  });

  it("reads the restart handoff from the host reply", async () => {
    vi.stubGlobal("fetch", vi.fn(async () => new Response(
      JSON.stringify({ ok: true, newPort: 58002, handoffId: "handoff-2", instanceId: "instance-old" }),
      { status: 200, headers: { "Content-Type": "application/json" } })));

    await expect(requestServiceRestart()).resolves.toEqual({
      newPort: 58002,
      handoffId: "handoff-2",
      previousInstanceId: "instance-old",
    });
  });

  it("accepts only the instance launched by this restart", async () => {
    stubStatus(url => {
      if (url.includes(":58001/")) return new Response(JSON.stringify(statusPayload()), { status: 200 });
      if (url.includes(":58002/")) {
        return new Response(JSON.stringify(statusPayload({ instanceId: "instance-new", actualPort: 58002 })), { status: 200 });
      }
      // 旧实例：仍在应答，但不是本次重启拉起的实例。
      return new Response(JSON.stringify(statusPayload({ instanceId: handoff.previousInstanceId, restartHandoffId: "", actualPort: 58000 })), { status: 200 });
    });

    expect(await probeServiceInstance("http://127.0.0.1:58000", handoff)).toBeNull();
    expect(await probeServiceInstance("http://127.0.0.1:58002", handoff)).toEqual({
      instanceId: "instance-new",
      restartHandoffId: handoff.handoffId,
      actualPort: 58002,
    });
  });

  it("rejects replies that are not NexusPipeline", async () => {
    stubStatus(() => new Response(JSON.stringify({ service: "something-else", instanceId: "x" }), { status: 200 }));
    expect(await probeServiceInstance("http://127.0.0.1:58001", handoff)).toBeNull();

    stubStatus(() => new Response("<html>other service</html>", { status: 200 }));
    expect(await probeServiceInstance("http://127.0.0.1:58001", handoff)).toBeNull();

    stubStatus(() => new Response("{}", { status: 200 }));
    expect(await probeServiceInstance("http://127.0.0.1:58001", handoff)).toBeNull();
  });

  it("ignores an unrelated service that occupies the configured port", async () => {
    const probed: string[] = [];
    const outcome = await restartService({
      handoff,
      href: "http://127.0.0.1:58000/#/settings",
      timeoutMs: 4000,
      intervalMs: 1,
      maxIntervalMs: 2,
      navigate: url => probed.push(url),
      probe: async url => {
        // 58001 被无关服务占用：NexusPipeline 实际监听 58002。
        if (!url.includes(":58002/")) return null;
        return { instanceId: "instance-new", restartHandoffId: handoff.handoffId, actualPort: 58002 };
      },
    });

    expect(outcome).toBe("ready");
    expect(probed).toEqual(["http://127.0.0.1:58002/#/settings"]);
  });

  it("waits for the new instance instead of the still-running old one", async () => {
    const navigated: string[] = [];
    let rounds = 0;
    const outcome = await restartService({
      handoff,
      href: "http://127.0.0.1:58001/#/queues",
      timeoutMs: 4000,
      intervalMs: 1,
      maxIntervalMs: 2,
      navigate: url => navigated.push(url),
      probe: async () => {
        // 同端口重启：前两轮旧实例仍在应答，第三轮新实例接管。
        rounds += 1;
        return rounds < 3 ? null : { instanceId: "instance-new", restartHandoffId: handoff.handoffId, actualPort: 58001 };
      },
    });

    expect(outcome).toBe("ready");
    expect(rounds).toBeGreaterThanOrEqual(3);
    expect(navigated).toEqual(["http://127.0.0.1:58001/#/queues"]);
  });

  it("covers the ports the host may fall back to", () => {
    expect(restartCandidatePorts("http://127.0.0.1:58000/#/", handoff, 3))
      .toEqual([58001, 58002, 58003, 58004]);
    expect(restartCandidatePorts("http://127.0.0.1:58000/#/", { ...handoff, newPort: 0 }, 3)).toEqual([58000]);
  });

  it("gives up at the timeout without navigating", async () => {
    let calls = 0;
    const recovered = await waitForRestartedService({
      href: "http://127.0.0.1:58000/",
      handoff,
      timeoutMs: 60,
      intervalMs: 1,
      maxIntervalMs: 5,
      probe: async () => {
        calls += 1;
        return null;
      },
    });

    expect(recovered).toBeNull();
    expect(calls).toBeGreaterThan(0);
  });

  it("stops waiting when the caller aborts", async () => {
    const controller = new AbortController();
    const pending = waitForRestartedService({
      href: "http://127.0.0.1:58000/",
      handoff,
      timeoutMs: 5000,
      intervalMs: 50,
      signal: controller.signal,
      probe: async () => null,
    });
    controller.abort();

    await expect(pending).resolves.toBeNull();
  });

  it("submits the restart, waits for the new instance and navigates to its actual port", async () => {
    const calls: Array<{ method: string; path: string }> = [];
    vi.stubGlobal("fetch", vi.fn(async (input: RequestInfo | URL, init?: RequestInit) => {
      calls.push({ method: String(init?.method || "GET"), path: String(input) });
      return new Response(JSON.stringify({ ok: true, newPort: 8091, handoffId: "handoff-9", instanceId: "instance-old" }), {
        status: 200,
        headers: { "Content-Type": "application/json" },
      });
    }));
    const navigated: string[] = [];

    const outcome = await restartService({
      timeoutMs: 2000,
      intervalMs: 1,
      probe: async () => ({ instanceId: "instance-new", restartHandoffId: "handoff-9", actualPort: 8091 }),
      navigate: url => navigated.push(url),
    });

    expect(outcome).toBe("ready");
    expect(calls[0]).toEqual({ method: "POST", path: "/api/settings/restart" });
    expect(navigated).toHaveLength(1);
    expect(navigated[0]).toContain(":8091/");
  });

  it("reuses the submitted handoff when the caller retries", async () => {
    const fetchMock = vi.fn(async () => new Response("{}", { status: 500 }));
    vi.stubGlobal("fetch", fetchMock);

    const outcome = await restartService({
      handoff,
      timeoutMs: 200,
      intervalMs: 1,
      probe: async () => null,
      navigate: () => undefined,
    });

    expect(outcome).toBe("timeout");
    expect(fetchMock).not.toHaveBeenCalled();
  });

  it("reports a failed request without probing", async () => {
    vi.stubGlobal("fetch", vi.fn(async () => new Response(JSON.stringify({ code: "operation_forbidden" }), {
      status: 409,
      headers: { "Content-Type": "application/json" },
    })));
    let probed = 0;

    const outcome = await restartService({
      timeoutMs: 200,
      intervalMs: 1,
      probe: async () => {
        probed += 1;
        return null;
      },
      navigate: () => undefined,
    });

    expect(outcome).toBe("failed");
    expect(probed).toBe(0);
  });
});
