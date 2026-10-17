import { readJsonInput } from "./json-parts.mjs";
import fs from "node:fs";
import path from "node:path";

const ignored = new Set([".git", "node_modules", "bin", "obj", "dist", ".generated", ".artifacts", "release"]);
export function markdownFiles(root) {
  const result = [];
  function visit(directory) {
    for (const entry of fs.readdirSync(directory, { withFileTypes: true })) {
      if (entry.isSymbolicLink()) continue;
      const file = path.join(directory, entry.name);
      if (entry.isDirectory() && !ignored.has(entry.name)) visit(file);
      else if (entry.isFile() && /\.md$/i.test(entry.name)) result.push(file);
    }
  }
  visit(root);
  return result;
}

export function exactPath(root, base, relative) {
  if (!relative || path.isAbsolute(relative) || /^[A-Za-z]:/.test(relative) || relative.includes("\\")) throw new Error(`Invalid repository path: ${relative}`);
  const target = path.resolve(root, base, relative), name = path.relative(root, target);
  if (name === ".." || name.startsWith(".." + path.sep) || path.isAbsolute(name)) throw new Error(`Outside repository: ${relative}`);
  let current = root;
  for (const part of name.split(path.sep).filter(Boolean)) {
    if (!fs.readdirSync(current).includes(part)) throw new Error(`Missing or case-mismatched path: ${relative}`);
    current = path.join(current, part);
  }
  const real = path.relative(fs.realpathSync(root), fs.realpathSync(target));
  if (real === ".." || real.startsWith(".." + path.sep) || path.isAbsolute(real)) throw new Error(`Outside repository link: ${relative}`);
  return target;
}

export function slug(value) {
  return value.toLowerCase().replace(/<[^>]*>/g, "").replace(/[\p{P}\p{S}]/gu, character => character === "-" || character === "_" ? character : "").replace(/ /g, "-");
}

function codeSpanEnd(line, start) {
  const run = line.slice(start).match(/^`+/)[0];
  const rest = line.slice(start + run.length);
  for (const match of rest.matchAll(/`+/g))
    if (match[0].length === run.length) return start + run.length + match.index + run.length;
  return -1;
}

function withoutInlineCode(line) {
  let result = "";
  for (let i = 0; i < line.length;) {
    if (line[i] === "\\" && i + 1 < line.length) { result += line.slice(i, i + 2); i += 2; continue; }
    if (line[i] === "`") {
      const end = codeSpanEnd(line, i);
      if (end >= 0) { result += " "; i = end; continue; }
      const run = line.slice(i).match(/^`+/)[0]; result += run; i += run.length; continue;
    }
    result += line[i++];
  }
  return result;
}

// Parse the repository's Markdown blocks before reading inline links; code is never navigation.
export function parseMarkdown(text) {
  const headings = [], links = [], errors = [], anchors = new Set(), counts = new Map(), definitions = new Map(), prose = [];
  let comment = false, maskingFence = null;
  const visible = text.split(/\r?\n/).map(line => {
    const marker = line.match(/^ {0,3}(`{3,}|~{3,})(.*)$/);
    if (maskingFence) {
      if (marker && marker[1][0] === maskingFence.char && marker[1].length >= maskingFence.length && !marker[2].trim()) maskingFence = null;
      return line;
    }
    if (!comment && marker) { maskingFence = { char: marker[1][0], length: marker[1].length }; return line; }
    if (!comment && /^( {4}|\t)/.test(line)) return line;
    let result = "";
    for (let i = 0; i < line.length;) {
      if (comment) {
        const end = line.indexOf("-->", i);
        if (end < 0) return result;
        comment = false; i = end + 3;
      } else {
        if (line[i] === "\\" && i + 1 < line.length) { result += line.slice(i, i + 2); i += 2; continue; }
        if (line[i] === "`") {
          const end = codeSpanEnd(line, i);
          if (end >= 0) { result += line.slice(i, end); i = end; continue; }
          const run = line.slice(i).match(/^`+/)[0]; result += run; i += run.length; continue;
        }
        if (line.startsWith("<!--", i)) { comment = true; i += 4; }
        else result += line[i++];
      }
    }
    return result;
  }).join("\n");
  const contentLines = new Set();
  let fence = null;
  for (const [index, line] of visible.split(/\r?\n/).entries()) {
    const marker = line.match(/^ {0,3}(`{3,}|~{3,})(.*)$/);
    if (fence) {
      if (marker && marker[1][0] === fence.char && marker[1].length >= fence.length && !marker[2].trim()) fence = null;
      else if (line.trim()) contentLines.add(index + 1);
      continue;
    }
    if (marker) {
      fence = { char: marker[1][0], length: marker[1].length };
      if (!marker[2].trim()) errors.push(`line ${index + 1}: code fence needs a language`);
      continue;
    }
    if (/^( {4}|\t)/.test(line)) { if (line.trim()) contentLines.add(index + 1); continue; }
    const heading = line.match(/^ {0,3}(#{1,6})(?:[ \t]+(.*)|$)/);
    if (heading) {
      const title = (heading[2] || "").replace(/(?:^|[ \t]+)#+[ \t]*$/, "").trim().replace(/!?\[([^\]]+)\]\([^)]*\)/g, "$1").replace(/[`*_~]/g, "");
      if (!title) errors.push(`line ${index + 1}: empty heading`);
      const base = slug(title);
      let count = counts.get(base) || 0, anchor = count ? `${base}-${count}` : base;
      while (anchors.has(anchor)) { count++; anchor = `${base}-${count}`; }
      counts.set(base, count + 1); anchors.add(anchor);
      headings.push({ level: heading[1].length, line: index + 1, title });
    }
    for (const match of line.matchAll(/<(?:a|h[1-6])\b[^>]*\b(?:id|name)=["']([^"']+)["'][^>]*>/gi)) anchors.add(match[1]);
    const definition = line.match(/^ {0,3}\[([^\]]+)\]:\s*(?:<([^>]+)>|(\S+))(?:\s+["'(].*)?$/);
    if (definition) { definitions.set(definition[1].trim().toLowerCase(), definition[2] || definition[3]); continue; }
    if (!heading && line.trim() && !/^(?:TODO|TBD|待补充|待完善|待编写)[。.!！]?$/i.test(line.trim())
      && !/^\s*(?:[-*_]\s*){3,}$/.test(line) && !/^\s*<a\b[^>]*>\s*<\/a>\s*$/i.test(line)) contentLines.add(index + 1);
    prose.push({ line: index + 1, text: line });
  }
  if (fence) errors.push("Unclosed code fence");
  for (const row of prose) {
    const s = withoutInlineCode(row.text);
    for (let i = 0; i < s.length; i++) {
      if (s[i] !== "[" || s[i - 1] === "\\") continue;
      let depth = 1, end = i + 1;
      for (; end < s.length && depth; end++) {
        if (s[end - 1] === "\\") continue;
        if (s[end] === "[") depth++;
        if (s[end] === "]") depth--;
      }
      if (depth) continue;
      const label = s.slice(i + 1, end - 1);
      for (const image of label.matchAll(/!\[[^\]]*\]\(([^)]+)\)/g)) links.push({ url: image[1], line: row.line });
      let url;
      if (s[end] === "(") {
        let cursor = end + 1, balance = 1, angle = false;
        for (; cursor < s.length && balance; cursor++) {
          if (s[cursor - 1] === "\\") continue;
          if (s[cursor] === "<") angle = true;
          if (s[cursor] === ">") angle = false;
          if (!angle && s[cursor] === "(") balance++;
          if (!angle && s[cursor] === ")") balance--;
        }
        if (balance) { errors.push(`line ${row.line}: unclosed link`); continue; }
        const destination = s.slice(end + 1, cursor - 1).trim();
        url = destination.startsWith("<") ? destination.slice(1, destination.indexOf(">")) : destination.split(/\s+["']/)[0];
        if (/\]\(/.test(label.replace(/!\[[^\]]*\]\([^)]*\)/g, ""))) errors.push(`line ${row.line}: nested link`);
        i = cursor - 1;
      } else if (s[end] === "[") {
        const close = s.indexOf("]", end + 1);
        if (close < 0) continue;
        const reference = (s.slice(end + 1, close) || label).trim().toLowerCase();
        url = definitions.get(reference);
        if (!url) errors.push(`line ${row.line}: undefined reference ${reference}`);
        i = close;
      } else {
        url = definitions.get(label.trim().toLowerCase()); i = end - 1;
      }
      if (url !== undefined) links.push({ url, line: row.line });
    }
    for (const match of s.matchAll(/<(?:a|img)\b[^>]*\b(?:href|src)=["']([^"']+)["'][^>]*>/gi)) links.push({ url: match[1], line: row.line });
  }
  const sections = headings.map((heading, index) => {
    const next = headings[index + 1];
    const end = next?.line || visible.split(/\r?\n/).length + 1;
    const hasBody = [...contentLines].some(line => line > heading.line && line < end);
    const hasChildren = Boolean(next && next.level > heading.level);
    return { ...heading, hasBody, hasChildren };
  });
  return { headings, links, anchors, errors, sections };
}

export function reviewStructure(text, doc = parseMarkdown(text)) {
  const notices = [];
  if (doc.headings.length === 1 && text.length > 2000) notices.push("Long document without sections");
  for (const [index, section] of doc.sections.entries()) {
    const descendants = doc.sections.slice(index + 1);
    const end = descendants.findIndex(item => item.level <= section.level);
    const children = (end < 0 ? descendants : descendants.slice(0, end)).filter(item => item.level === section.level + 1);
    if (section.level > 1 && children.length === 1) notices.push(`line ${section.line}: single child section`);
  }
  return notices;
}

export function checkDocuments(root, { partnerRoot } = {}) {
  root = path.resolve(root);
  const failures = [], notices = [], documents = markdownFiles(root), parsed = new Map(), paragraphs = new Map(); let checked = 0, crossChecked = 0;
  const legal = name => name.includes("/LICENSES/") || name.endsWith("THIRD-PARTY-NOTICES.md");
  for (const file of documents) {
    const name = path.relative(root, file).replaceAll("\\", "/");
    if (legal(name)) continue;
    let text;
    try { text = new TextDecoder("utf-8", { fatal: true, ignoreBOM: true }).decode(fs.readFileSync(file)); }
    catch { failures.push(`${name}: invalid UTF-8`); continue; }
    const doc = parseMarkdown(text); parsed.set(file, doc);
    failures.push(...doc.errors.map(error => `${name}: ${error}`));
    if (!name.startsWith(".github/")) {
      if (doc.headings.filter(h => h.level === 1).length !== 1) failures.push(`${name}: expected one H1`);
      let previous = 0;
      for (const heading of doc.headings) { if (heading.level > previous + 1) failures.push(`${name}:${heading.line}: skipped heading level`); previous = heading.level; }
      for (const section of doc.sections) if (!section.hasBody && !section.hasChildren) failures.push(`${name}:${section.line}: empty section`);
      notices.push(...reviewStructure(text, doc).map(message => `${name}: ${message}`));
      if (name.startsWith("docs/") && !name.startsWith("docs/history/") && !name.startsWith("docs/decisions/")) {
        for (const paragraph of text.split(/\n\s*\n/).map(value => value.trim())) {
          if (paragraph.length < 250 || /^(?:[#|`~]| {4})/.test(paragraph)) continue;
          const existing = paragraphs.get(paragraph);
          if (existing) notices.push(`${name}: repeated long paragraph also in ${existing}`);
          else paragraphs.set(paragraph, name);
        }
      }
    }
    if (name.startsWith("docs/") || /\/docs\//.test(name)) {
      if (!/^(README(?:\.en)?|[a-z0-9]+(?:-[a-z0-9]+)*)\.md$/.test(path.basename(file))) failures.push(`${name}: noncanonical filename`);
      if (text.includes("\r") || text.charCodeAt(0) === 0xfeff || !text.endsWith("\n") || text.endsWith("\n\n")) failures.push(`${name}: use UTF-8 without BOM, LF and one final newline`);
    }
  }
  for (const [file, doc] of parsed) for (const link of doc.links) {
    try {
      let targetRoot = root, base = path.dirname(path.relative(root, file)), raw = link.url;
      const remote = raw.match(/^https:\/\/github\.com\/FlappiBakuse\/(NexusPipeline(?:-Plugins)?)\/blob\/main\/(.+)$/);
      if (remote) {
        const self = fs.existsSync(path.join(root, "src/NexusPipeline.csproj")) ? "NexusPipeline" : "NexusPipeline-Plugins";
        if (remote[1] !== self) { if (!partnerRoot) continue; targetRoot = path.resolve(partnerRoot); }
        base = ""; raw = remote[2]; crossChecked++;
      } else if (/^[a-z][a-z0-9+.-]*:|^\/\//i.test(raw)) continue;
      else if (raw.startsWith("/")) throw new Error(`Use repository-relative links: ${raw}`);
      const [pathname, ...hash] = raw.split("#"), fragment = decodeURIComponent(hash.join("#"));
      const relative = decodeURIComponent(pathname.split("?")[0]) || path.basename(file);
      const target = exactPath(targetRoot, base, relative); checked++;
      if (fragment && target.endsWith(".md")) {
        const anchors = parsed.get(target)?.anchors || parseMarkdown(fs.readFileSync(target, "utf8")).anchors;
        if (!anchors.has(fragment)) throw new Error(`Missing fragment: ${raw}`);
      }
    } catch (error) { failures.push(`${path.relative(root, file)}:${link.line}: ${error.message}`); }
  }
  const navigation = readJsonInput(root, "docs/map.json").value;
  const ids = new Set(), authority = new Set(), mapped = new Set();
  if (navigation.schemaVersion !== 1 || !navigation.topics?.length) failures.push("GOV-DOC-02 invalid navigation schema");
  for (const topic of navigation.topics || []) {
    try {
      if (!topic.id || ids.has(topic.id)) throw new Error(`Duplicate/empty topic: ${topic.id}`);
      ids.add(topic.id);
      const documentPath = exactPath(root, "docs", topic.path);
      if (!documentPath.endsWith(".md") || !fs.statSync(documentPath).isFile()) throw new Error(`Not a Markdown document: ${topic.path}`);
      if (mapped.has(documentPath)) throw new Error(`Duplicate document: ${topic.path}`);
      mapped.add(documentPath);
      for (const code of [...topic.codePaths || [], ...topic.validationPaths || []]) exactPath(root, "", code);
      for (const owner of topic.authorityFor || []) { if (authority.has(owner)) throw new Error(`Duplicate authority: ${owner}`); authority.add(owner); }
    } catch (error) { failures.push(`GOV-DOC-02 ${error.message}`); }
  }
  for (const file of parsed.keys()) if (path.relative(root, file).startsWith("docs" + path.sep) && !mapped.has(file)) failures.push(`GOV-DOC-02 Unmapped document: ${path.relative(root, file)}`);
  return { failures, notices, checked, crossChecked, documents: parsed.size };
}
