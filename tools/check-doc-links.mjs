import fs from "node:fs";
import path from "node:path";
import { fileURLToPath } from "node:url";

/**
 * Markdown 内链与片段校验。只检查本仓库内的相对链接；
 * 跨仓库链接（NexusPipeline-Plugins）与外部 URL 不在本入口的判定范围。
 */

const projectRoot = path.resolve(path.dirname(fileURLToPath(import.meta.url)), "..");
// tests/.artifacts 与 .generated 是运行产物，其中的插件 README 相对链接在 runtime 内必然失效。
const SKIP_DIRECTORIES = new Set([".git", "node_modules", "bin", "obj", "release", "dist", ".generated", ".artifacts"]);
const EXTERNAL = /^[a-z][a-z0-9+.-]*:/iu;
const LINK = /!?\[[^\]]*\]\(([^)\s]+)(?:\s+"[^"]*")?\)/gu;
const HEADING = /^(#{1,6})\s+(.+?)\s*#*\s*$/gmu;
const HTML_ANCHOR = /\b(?:id|name)\s*=\s*["']([^"']+)["']/giu;

function walkMarkdown(directory) {
  const files = [];
  for (const entry of fs.readdirSync(directory, { withFileTypes: true })) {
    if (entry.isDirectory()) {
      if (!SKIP_DIRECTORIES.has(entry.name)) files.push(...walkMarkdown(path.join(directory, entry.name)));
    } else if (entry.isFile() && entry.name.toLowerCase().endsWith(".md")) {
      files.push(path.join(directory, entry.name));
    }
  }
  return files;
}

// GitHub 的锚点规则：小写、去掉标点、空格转连字符；重复标题追加 -1、-2。
function slugify(value) {
  return value.normalize("NFKC").toLowerCase()
    .replace(/<[^>]*>/gu, "")
    .replace(/[^\p{L}\p{N}\s-]/gu, "")
    .trim().replace(/\s+/gu, "-");
}

function collectAnchors(text) {
  const anchors = new Set();
  const counts = new Map();
  for (const match of text.matchAll(HEADING)) {
    const base = slugify(match[2]);
    if (!base) continue;
    const count = counts.get(base) || 0;
    counts.set(base, count + 1);
    anchors.add(count ? `${base}-${count}` : base);
  }
  for (const match of text.matchAll(HTML_ANCHOR)) anchors.add(match[1]);
  return anchors;
}

function lineNumber(text, index) {
  return text.slice(0, index).split(/\r?\n/u).length;
}

const documents = walkMarkdown(projectRoot);
const anchorsByFile = new Map(documents.map(file => [file, collectAnchors(fs.readFileSync(file, "utf8"))]));
const failures = [];
let checked = 0;

for (const file of documents) {
  const relative = path.relative(projectRoot, file).replaceAll("\\", "/");
  const text = fs.readFileSync(file, "utf8");
  for (const match of text.matchAll(LINK)) {
    const raw = match[1];
    if (EXTERNAL.test(raw) || raw.startsWith("/") || raw.startsWith("//")) continue;
    const hash = raw.indexOf("#");
    const targetPath = decodeURIComponent((hash < 0 ? raw : raw.slice(0, hash)).split("?", 1)[0]);
    const fragment = hash < 0 ? "" : decodeURIComponent(raw.slice(hash + 1));
    const location = `${relative}:${lineNumber(text, match.index)}`;
    const resolved = path.resolve(path.dirname(file), targetPath || path.basename(file));
    if (targetPath && !fs.existsSync(resolved)) {
      failures.push(`${location} 链接目标不存在：${raw}`);
      continue;
    }
    checked += 1;
    if (!fragment) continue;
    const anchors = anchorsByFile.get(resolved);
    if (anchors && !anchors.has(fragment)) failures.push(`${location} 片段不存在：${raw}`);
  }
}

const navigation = JSON.parse(fs.readFileSync(path.join(projectRoot, "docs/map.json"), "utf8"));
const topicIds = new Set();
if (navigation.schemaVersion !== 1 || !Array.isArray(navigation.topics) || !navigation.topics.length)
  failures.push("docs/map.json: invalid navigation schema");
for (const topic of navigation.topics || []) {
  if (!topic.id || topicIds.has(topic.id)) failures.push(`docs/map.json: duplicate/empty topic ${topic.id}`);
  topicIds.add(topic.id);
  for (const [base, relative] of [["docs", topic.path], ...(topic.codePaths || []).map(value => ["", value])]) {
    const resolved = typeof relative === "string" ? path.resolve(projectRoot, base, relative) : projectRoot;
    if (typeof relative !== "string" || path.isAbsolute(relative) || path.relative(projectRoot, resolved).startsWith("..")
        || !fs.existsSync(path.join(projectRoot, base, relative))) failures.push(`docs/map.json: missing/invalid path ${relative}`);
  }
}

if (failures.length > 0) {
  console.error(`文档内链检查失败（${failures.length} 项）：`);
  for (const failure of failures) console.error(`  ${failure}`);
  process.exitCode = 1;
} else {
  console.error(`文档内链检查通过：校验 ${checked} 个本地链接`);
}
