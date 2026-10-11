import { desktopBridge } from "./desktop";

export function normalizeExternalUrl(value: string): string {
  if (typeof value !== "string" || value.length > 2048 || /[\u0000-\u0020\u007f]/.test(value)) throw new Error("external_url_invalid");
  const url = new URL(value);
  if (url.protocol !== "https:" || url.username || url.password || !url.hostname || url.href.length > 2048) throw new Error("external_url_invalid");
  return url.href;
}
export async function openExternal(value: string): Promise<void> {
  const url = normalizeExternalUrl(value);
  const desktop = desktopBridge();
  if (desktop) { await desktop.openExternal(url); return; }
  const tab = window.open("about:blank", "_blank");
  if (!tab) throw new Error("external_window_blocked");
  tab.opener = null;
  const policy = tab.document.createElement("meta");
  policy.name = "referrer";
  policy.content = "no-referrer";
  tab.document.head.append(policy);
  tab.location.replace(url);
}
