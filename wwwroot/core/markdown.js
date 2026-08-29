import { esc } from "./format.js";

const MAX_MARKDOWN_LENGTH = 256 * 1024;

function placeholder(values, html) {
  const index = values.push(html) - 1;
  return `\u0000${index}\u0000`;
}

function restore(value, values) {
  return value.replace(/\u0000(\d+)\u0000/g, (_, index) => values[Number(index)] || "");
}

function inlineMarkup(source) {
  const values = [];
  const withoutLinks = String(source || "").replace(/\[([^\]\r\n]{1,256})\]\(([^\s)]+)\)/gu, (match, label, rawUrl) => {
    try {
      const url = new URL(rawUrl);
      if (url.protocol !== "https:") return match;
      return placeholder(values, `<a href="${esc(url.href)}" target="_blank" rel="noopener noreferrer">${esc(label)}</a>`);
    } catch {
      return match;
    }
  });
  let value = esc(withoutLinks);
  value = value.replace(/`([^`\r\n]+)`/gu, (_, code) => placeholder(values, `<code>${code}</code>`));
  value = value.replace(/\*\*([^*\r\n]+)\*\*/gu, "<strong>$1</strong>");
  value = value.replace(/__([^_\r\n]+)__/gu, "<strong>$1</strong>");
  return restore(value, values);
}

function isListLine(line) {
  return /^\s*(?:[-*+]\s+|\d+[.]\s+)/u.test(line);
}

function listMarkup(lines, start) {
  const ordered = /^\s*\d+[.]\s+/u.test(lines[start]);
  const items = [];
  let index = start;
  while (index < lines.length) {
    const match = ordered
      ? lines[index].match(/^\s*\d+[.]\s+(.+)$/u)
      : lines[index].match(/^\s*[-*+]\s+(.+)$/u);
    if (!match) break;
    items.push(`<li>${inlineMarkup(match[1])}</li>`);
    index += 1;
  }
  return { html: `<${ordered ? "ol" : "ul"}>${items.join("")}</${ordered ? "ol" : "ul"}>`, next: index };
}

export function renderMarkdown(markdown) {
  const text = String(markdown || "").slice(0, MAX_MARKDOWN_LENGTH);
  const lines = text.replace(/\r\n?/gu, "\n").split("\n");
  const blocks = [];
  let index = 0;
  while (index < lines.length) {
    const line = lines[index];
    if (!line.trim()) {
      index += 1;
      continue;
    }
    const fence = line.match(/^\s*```(?:[^\s`]*)?\s*$/u);
    if (fence) {
      index += 1;
      const code = [];
      while (index < lines.length && !/^\s*```\s*$/u.test(lines[index])) {
        code.push(lines[index]);
        index += 1;
      }
      if (index < lines.length) index += 1;
      blocks.push(`<pre><code>${esc(code.join("\n"))}</code></pre>`);
      continue;
    }
    const heading = line.match(/^\s*(#{1,3})\s+(.+?)\s*#*\s*$/u);
    if (heading) {
      const level = Math.min(4, heading[1].length + 1);
      blocks.push(`<h${level}>${inlineMarkup(heading[2])}</h${level}>`);
      index += 1;
      continue;
    }
    if (isListLine(line)) {
      const list = listMarkup(lines, index);
      blocks.push(list.html);
      index = list.next;
      continue;
    }
    if (/^\s*>\s?/u.test(line)) {
      const quote = [];
      while (index < lines.length && /^\s*>\s?/u.test(lines[index])) {
        quote.push(lines[index].replace(/^\s*>\s?/u, ""));
        index += 1;
      }
      blocks.push(`<blockquote>${inlineMarkup(quote.join("\n"))}</blockquote>`);
      continue;
    }
    const paragraph = [line];
    index += 1;
    while (index < lines.length
      && lines[index].trim()
      && !/^\s*```/u.test(lines[index])
      && !/^\s*#{1,3}\s+/u.test(lines[index])
      && !isListLine(lines[index])
      && !/^\s*>\s?/u.test(lines[index])) {
      paragraph.push(lines[index]);
      index += 1;
    }
    blocks.push(`<p>${inlineMarkup(paragraph.join("\n"))}</p>`);
  }
  return `<div class="markdown-content">${blocks.join("")}</div>`;
}
