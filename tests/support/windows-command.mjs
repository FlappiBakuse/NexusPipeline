export function quoteWindowsArg(value) {
  const text = String(value);
  if (/^[A-Za-z0-9_./:=+,-]+$/u.test(text)) return text;
  return `"${text.replace(/(["^&|<>])/gu, "^$1").replace(/%/gu, "%%")}"`;
}
