export function normalizeExternalUrl(value: unknown): string {
  if (typeof value !== "string" || value.length > 2048 || /[\u0000-\u0020\u007f]/.test(value)) throw new TypeError("external_url_invalid");
  const url = new URL(value);
  if (url.protocol !== "https:" || url.username || url.password || !url.hostname || url.href.length > 2048) throw new TypeError("external_url_invalid");
  return url.href;
}
