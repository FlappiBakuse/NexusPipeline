import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { clearBackgroundSurface, setBackgroundSurface } from "./appearance";

/**
 * 背景表面的 Blob URL 归属：地址交给外观表面托管后，替换或清除都必须回收上一个 Object URL。
 */
const BLOB_A = "blob:http://localhost/11111111-1111-1111-1111-111111111111";
const BLOB_B = "blob:http://localhost/22222222-2222-2222-2222-222222222222";

describe("appearance background surface", () => {
  let revoked: string[];

  beforeEach(() => {
    revoked = [];
    (URL as unknown as { revokeObjectURL: (url: string) => void }).revokeObjectURL = (url: string) => {
      revoked.push(String(url));
    };
  });

  afterEach(() => {
    clearBackgroundSurface();
    revoked = [];
    vi.unstubAllGlobals();
  });

  it("revokes the previous blob address when a new background replaces it", () => {
    setBackgroundSurface({ url: BLOB_A });
    expect(revoked).toEqual([]);

    setBackgroundSurface({ url: BLOB_B });
    expect(revoked).toEqual([BLOB_A]);
    expect(document.documentElement.style.getPropertyValue("--nexus-wallpaper-image")).toContain(BLOB_B);
  });

  it("keeps the current blob address while it is still the active background", () => {
    setBackgroundSurface({ url: BLOB_A });
    setBackgroundSurface({ url: BLOB_A, blurPx: 6 });
    expect(revoked).toEqual([]);

    clearBackgroundSurface();
    expect(revoked).toEqual([BLOB_A]);
  });

  it("revokes the active blob address when the background is cleared", () => {
    setBackgroundSurface({ url: BLOB_A });
    clearBackgroundSurface();
    clearBackgroundSurface();

    expect(revoked).toEqual([BLOB_A]);
    expect(document.body.dataset.wallpaper).toBeUndefined();
  });

  it("leaves http and data addresses untouched", () => {
    setBackgroundSurface({ url: "http://127.0.0.1:8080/wallpaper.png" });
    setBackgroundSurface({ url: "data:image/png;base64,AAAA" });
    clearBackgroundSurface();

    expect(revoked).toEqual([]);
  });
});
