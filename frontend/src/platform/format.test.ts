import { describe, expect, it } from "vitest";
import { esc } from "./format";

describe("host format helpers", () => {
  it("escapes all HTML-sensitive characters", () => {
    expect(esc(`<tag a="1">it's & ok</tag>`)).toBe("&lt;tag a=&quot;1&quot;&gt;it&#39;s &amp; ok&lt;/tag&gt;");
    expect(esc(null)).toBe("");
    expect(esc(undefined)).toBe("");
  });
});
