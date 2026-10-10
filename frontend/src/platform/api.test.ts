import { describe, expect, it, vi } from "vitest";
import { api, apiBlob, isAbortError } from "./api";

describe("host api client", () => {
  it("keeps management authentication when a client session expires", async () => {
    const previousFetch=globalThis.fetch;
    const prompt=vi.fn();(window as Window&{__showTokenPrompt?:()=>void}).__showTokenPrompt=prompt;
    localStorage.setItem('nexus-token','owned-management-token');
    try {
      globalThis.fetch=vi.fn(async()=>new Response(JSON.stringify({code:'client_session_required'}),{status:401})) as unknown as typeof fetch;
      await expect(api('GET','/api/client-sessions/current')).rejects.toMatchObject({code:'client_session_required',status:401});
      expect(localStorage.getItem('nexus-token')).toBe('owned-management-token');expect(prompt).not.toHaveBeenCalled();
      globalThis.fetch=vi.fn(async()=>new Response(JSON.stringify({code:'auth_required'}),{status:401})) as unknown as typeof fetch;
      await expect(api('GET','/api/status')).rejects.toMatchObject({code:'auth_required'});
      expect(prompt).toHaveBeenCalledOnce();expect(localStorage.getItem('nexus-token')).toBeNull();
    }finally{globalThis.fetch=previousFetch;delete (window as Window&{__showTokenPrompt?:()=>void}).__showTokenPrompt;localStorage.clear();}
  });
  it("sends the bearer token for protected binary resources", async () => {
    const previousFetch = globalThis.fetch;
    const getItem = vi.spyOn(Storage.prototype, "getItem").mockImplementation(function (this: Storage, key: string) {
      return key === "nexus-token" ? "remote-token" : null;
    });
    try {
      let request: { path: string; options: RequestInit } | null = null;
      globalThis.fetch = (async (path: string, options: RequestInit) => {
        request = { path, options };
        return new Response("png", {
          status: 200,
          headers: { "Content-Type": "image/png" },
        });
      }) as unknown as typeof fetch;

      const blob = await apiBlob("/api/history/image?id=run-1");

      expect(request!.path).toBe("/api/history/image?id=run-1");
      expect((request!.options.headers as Record<string, string>).Authorization).toBe("Bearer remote-token");
      expect(request!.options.cache).toBe("no-store");
      expect(blob.type).toBe("image/png");
    } finally {
      globalThis.fetch = previousFetch;
      getItem.mockRestore();
    }
  });

  it("normalizes browser-specific cancellation messages", () => {
    expect(isAbortError({ name: "AbortError", message: "The operation was aborted." })).toBe(true);
    expect(isAbortError(new Error("signal is aborted without reason"))).toBe(true);
    expect(isAbortError({ name: "TypeError", message: "Failed to fetch" })).toBe(false);
    expect(isAbortError(null)).toBe(false);
  });

  it("projects backend error codes through the host locale resources", async () => {
    const previousFetch = globalThis.fetch;
    try {
      globalThis.fetch = (async () => new Response(JSON.stringify({ code: "script_not_found" }), {
        status: 404,
        headers: { "Content-Type": "application/json" },
      })) as unknown as typeof fetch;
      await expect(api("GET", "/api/scripts/missing")).rejects.toMatchObject({ status: 404, code: "script_not_found" });
    } finally {
      globalThis.fetch = previousFetch;
    }
  });

  it("returns null for empty responses and JSON payloads otherwise", async () => {
    const previousFetch = globalThis.fetch;
    try {
      globalThis.fetch = (async () => new Response(null, { status: 204 })) as unknown as typeof fetch;
      await expect(api("DELETE", "/api/scripts/1")).resolves.toBeNull();
      globalThis.fetch = (async () => new Response(JSON.stringify({ ok: true }), {
        status: 200,
        headers: { "Content-Type": "application/json" },
      })) as unknown as typeof fetch;
      await expect(api<{ ok: boolean }>("GET", "/api/status")).resolves.toEqual({ ok: true });
    } finally {
      globalThis.fetch = previousFetch;
    }
  });
});
