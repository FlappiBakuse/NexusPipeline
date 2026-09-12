import { afterEach, describe, expect, it, vi } from "vitest";
import { restartService, restartTargetUrl, waitForServiceRecovery } from "./service-restart";

/** 重启恢复编排：地址构造、有限探测与结果判定都在这一层验证。 */
describe("service restart orchestration", () => {
  afterEach(() => {
    vi.unstubAllGlobals();
  });

  it("keeps protocol, hostname, path and hash while switching to the new port", () => {
    expect(restartTargetUrl("http://127.0.0.1:8080/dashboard?tab=1#/history", 8090))
      .toBe("http://127.0.0.1:8090/dashboard?tab=1#/history");
    expect(restartTargetUrl("http://127.0.0.1:8080/#/plugins", 8090))
      .toBe("http://127.0.0.1:8090/#/plugins");
  });

  it("keeps the current address when the port did not change", () => {
    expect(restartTargetUrl("http://127.0.0.1:8080/#/settings", 0))
      .toBe("http://127.0.0.1:8080/#/settings");
  });

  it("requires two consecutive successful probes before reporting recovery", async () => {
    const responses = [true, false, true, true];
    let calls = 0;
    const recovered = await waitForServiceRecovery({
      targetUrl: "http://127.0.0.1:8080/",
      timeoutMs: 5000,
      intervalMs: 1,
      maxIntervalMs: 2,
      probe: async () => responses[Math.min(calls++, responses.length - 1)],
    });

    expect(recovered).toBe(true);
    expect(calls).toBe(4);
  });

  it("gives up at the timeout instead of polling forever", async () => {
    let calls = 0;
    const recovered = await waitForServiceRecovery({
      targetUrl: "http://127.0.0.1:8080/",
      timeoutMs: 60,
      intervalMs: 1,
      maxIntervalMs: 5,
      probe: async () => {
        calls += 1;
        return false;
      },
    });

    expect(recovered).toBe(false);
    expect(calls).toBeGreaterThan(0);
  });

  it("stops waiting when the caller aborts", async () => {
    const controller = new AbortController();
    const pending = waitForServiceRecovery({
      targetUrl: "http://127.0.0.1:8080/",
      timeoutMs: 5000,
      intervalMs: 50,
      signal: controller.signal,
      probe: async () => false,
    });
    controller.abort();

    await expect(pending).resolves.toBe(false);
  });

  it("requests the restart, waits for recovery and navigates to the new port", async () => {
    const calls: Array<{ method: string; path: string }> = [];
    vi.stubGlobal("fetch", vi.fn(async (input: RequestInfo | URL, init?: RequestInit) => {
      calls.push({ method: String(init?.method || "GET"), path: String(input) });
      if (String(input).includes("/api/settings/restart")) {
        return new Response(JSON.stringify({ ok: true, newPort: 8091 }), { status: 200, headers: { "Content-Type": "application/json" } });
      }
      return new Response("{}", { status: 200, headers: { "Content-Type": "application/json" } });
    }));
    const navigated: string[] = [];

    const outcome = await restartService({
      timeoutMs: 2000,
      intervalMs: 1,
      probe: async () => true,
      navigate: url => navigated.push(url),
    });

    expect(outcome).toBe("ready");
    expect(calls[0]).toEqual({ method: "POST", path: "/api/settings/restart" });
    expect(navigated).toHaveLength(1);
    expect(navigated[0]).toContain(`:8091/`);
  });

  it("reports a timeout without navigating", async () => {
    vi.stubGlobal("fetch", vi.fn(async () => new Response(JSON.stringify({ ok: true, newPort: 8092 }), {
      status: 200,
      headers: { "Content-Type": "application/json" },
    })));
    const navigated: string[] = [];

    const outcome = await restartService({
      timeoutMs: 60,
      intervalMs: 1,
      maxIntervalMs: 2,
      probe: async () => false,
      navigate: url => navigated.push(url),
    });

    expect(outcome).toBe("timeout");
    expect(navigated).toEqual([]);
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
        return false;
      },
      navigate: () => undefined,
    });

    expect(outcome).toBe("failed");
    expect(probed).toBe(0);
  });
});
