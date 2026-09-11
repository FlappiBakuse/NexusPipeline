import assert from "node:assert/strict";
import test from "node:test";
import { apiBlob, isAbortError } from "../../wwwroot/core/api.js";

test("apiBlob sends the bearer token for protected image resources", async () => {
  const previousFetch = globalThis.fetch;
  const previousStorage = globalThis.localStorage;
  try {
    globalThis.localStorage = { getItem: key => key === "nexus-token" ? "remote-token" : null };
    let request;
    globalThis.fetch = async (path, options) => {
      request = { path, options };
      return new Response(new Blob(["png"], { type: "image/png" }), {
        status: 200,
        headers: { "Content-Type": "image/png" },
      });
    };

    const blob = await apiBlob("/api/history/image?id=run-1");

    assert.equal(request.path, "/api/history/image?id=run-1");
    assert.equal(request.options.headers.Authorization, "Bearer remote-token");
    assert.equal(request.options.cache, "no-store");
    assert.equal(blob.type, "image/png");
  } finally {
    globalThis.fetch = previousFetch;
    if (previousStorage === undefined) delete globalThis.localStorage;
    else globalThis.localStorage = previousStorage;
  }
});

test("api abort detection normalizes browser-specific cancellation messages", () => {
  assert.equal(isAbortError({ name: "AbortError", message: "The operation was aborted." }), true);
  assert.equal(isAbortError(new Error("signal is aborted without reason")), true);
  assert.equal(isAbortError({ name: "TypeError", message: "Failed to fetch" }), false);
  assert.equal(isAbortError(null), false);
});
