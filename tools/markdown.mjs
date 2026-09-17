import MarkdownIt from "markdown-it";

const parser = new MarkdownIt({ html: true });

export function slugifyHeading(value) {
  return value.replace(/<[^>]*>/gu, "").normalize("NFKC").toLocaleLowerCase("en-US")
    .replace(/[^\p{L}\p{N}\s-]/gu, "").trim().replace(/\s+/gu, "-");
}

export function collectAnchors(text) {
  const tokens = parser.parse(text, {});
  const result = new Set();
  const counts = new Map();
  for (let i = 0; i < tokens.length; i++) {
    const token = tokens[i];
    if (token.type === "heading_open") {
      const content = (tokens[i + 1]?.children || []).filter(child => ["text", "code_inline", "softbreak"].includes(child.type)).map(child => child.content).join("");
      const base = slugifyHeading(content);
      const count = counts.get(base) || 0;
      counts.set(base, count + 1);
      result.add(base + (count ? `-${count}` : ""));
    }
    for (const html of [token, ...(token.children || [])].filter(child => child.type.startsWith("html"))) {
      for (const match of html.content.matchAll(/\b(?:id|name)\s*=\s*["']([^"']+)["']/giu)) result.add(match[1]);
    }
  }
  return result;
}

export function findLocalLinks(text) {
  const offsets = [0];
  for (let i = 0; i < text.length; i++) if (text[i] === "\n") offsets.push(i + 1);
  return parser.parse(text, {}).flatMap(token => (token.children || []).flatMap(child => {
    const target = child.type === "link_open" ? child.attrGet("href") : child.type === "image" ? child.attrGet("src") : null;
    return target ? [{ rawTarget: target, index: offsets[token.map?.[0] || 0] }] : [];
  }));
}

export function parseLinkTarget(value) {
  const hash = value.indexOf("#");
  const path = hash < 0 ? value : value.slice(0, hash);
  return { path: decodeURIComponent(path.split("?", 1)[0]), fragment: hash < 0 ? "" : decodeURIComponent(value.slice(hash + 1)) };
}
