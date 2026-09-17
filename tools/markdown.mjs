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
  return findMarkdownLinks(text);
}

function walkTokens(tokens, offsets, links) {
  for (const token of tokens) {
    const line = token.map?.[0] || 0;
    const index = offsets[line] || 0;
    if (token.type === "link_open") {
      const target = token.attrGet("href");
      if (target) links.push({ rawTarget: target, index });
    } else if (token.type === "image") {
      const target = token.attrGet("src");
      if (target) links.push({ rawTarget: target, index });
    } else if (token.type === "html_inline" || token.type === "html_block") {
      for (const match of token.content.matchAll(/<(?:a\b[^>]*\bhref|area\b[^>]*\bhref|img\b[^>]*\bsrc)\s*=\s*["']([^"']+)["']/giu)) {
        links.push({ rawTarget: match[1], index: index + match.index });
      }
    }
    if (token.children?.length) walkTokens(token.children, offsets, links);
  }
}

export function findMarkdownLinks(text) {
  const offsets = [0];
  for (let i = 0; i < text.length; i++) if (text[i] === "\n") offsets.push(i + 1);
  const links = [];
  walkTokens(parser.parse(text, {}), offsets, links);
  return links;
}

export function parseLinkTarget(value) {
  const hash = value.indexOf("#");
  const path = hash < 0 ? value : value.slice(0, hash);
  return { path: decodeURIComponent(path.split("?", 1)[0]), fragment: hash < 0 ? "" : decodeURIComponent(value.slice(hash + 1)) };
}
