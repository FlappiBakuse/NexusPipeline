import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { clearBackgroundSurface, createAppearanceHost, setBackgroundSurface } from "./appearance";

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

describe("appearance color token scopes", () => {
  const host = createAppearanceHost();

  afterEach(() => {
    host.clearTokens();
    host.applyTheme("system");
  });

  it("projects explicit theme colors to body while appearance colors retain priority", () => {
    const theme = host.registerTheme("appearance-token-scope-test", {
      tokens: {
        "--nx-color-accent": "#123456",
        "--nx-focus-ring": "0 0 0 3px #123456",
        "--accent": "#456789",
      },
    });

    host.applyTheme("appearance-token-scope-test");
    expect(document.documentElement.style.getPropertyValue("--_nexus-theme-color-accent")).toBe("#123456");
    expect(document.documentElement.style.getPropertyValue("--_nexus-theme-focus-ring")).toBe("0 0 0 3px #123456");

    host.setTokens({ "--accent": "#abcdef" });
    expect(document.body.style.getPropertyValue("--accent")).toBe("#abcdef");
    expect(document.body.style.getPropertyValue("--_nexus-appearance-color-accent")).toBe("var(--accent)");

    host.clearTokens();
    expect(document.body.style.getPropertyValue("--accent")).toBe("");
    expect(document.body.style.getPropertyValue("--_nexus-appearance-color-accent")).toBe("");
    expect(document.documentElement.style.getPropertyValue("--_nexus-theme-color-accent")).toBe("#123456");
    expect(document.documentElement.style.getPropertyValue("--accent")).toBe("#456789");

    theme.dispose();
  });
});
