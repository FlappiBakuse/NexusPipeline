import { describe, expect, it, vi } from "vitest";
vi.mock("./desktop",()=>({desktopBridge:()=>null}));
import { normalizeExternalUrl, openExternal } from "./navigation";
describe("external navigation", () => {
  it("rejects non HTTPS credentials and control characters before opening", async () => {
    const open=vi.spyOn(window,"open");
    for(const url of ["http://example.com","javascript:alert(1)","https://user:secret@example.com","https://example.com/\n"]) expect(()=>normalizeExternalUrl(url)).toThrow();
    await expect(openExternal("file:///C:/secret")).rejects.toThrow();expect(open).not.toHaveBeenCalled();open.mockRestore();
  });
  it("opens a separate tab without opener or referrer", async () => {
    const target={opener:window,document:document.implementation.createHTMLDocument(),location:{replace:vi.fn()}};
    const open=vi.spyOn(window,"open").mockReturnValue(target as unknown as Window);
    await openExternal("https://example.com/news");expect(target.opener).toBeNull();
    expect(target.document.querySelector('meta[name="referrer"]')?.getAttribute("content")).toBe("no-referrer");
    expect(target.location.replace).toHaveBeenCalledWith("https://example.com/news");open.mockRestore();
  });
});
