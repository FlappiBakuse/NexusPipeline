import fs from "node:fs";
import path from "node:path";
import { spawnSync } from "node:child_process";
import { fileURLToPath } from "node:url";
import { TEST_DOMAIN_REGISTRY } from "./ci-domains.mjs";

export const DOCS_INDEX_SCHEMA_VERSION = 1;

function normalize(value) {
  return String(value || "").replaceAll("\\", "/").replace(/^\.\//u, "");
}

function isSafeRelativePath(value) {
  return Boolean(value)
    && !value.startsWith("../")
    && !value.includes("/../")
    && !value.endsWith("/..")
    && !path.isAbsolute(value)
    && !/^[A-Za-z]:\//u.test(value);
}

function documentationPath(root, relativePath) {
  return relativePath.startsWith("docs/")
    ? path.join(root, relativePath)
    : path.join(root, "docs", relativePath);
}

function registryDomainKeys(registry) {
  return new Set([
    ...(registry.hostAreas || []).map(item => `hostAreas.${item.key}`),
    ...(registry.frontendGroups || []).map(item => `frontendGroups.${item.key}`),
    ...(registry.systemGroups || []).map(item => `systemGroups.${item.key}`),
    ...(registry.governanceDomains || []).map(item => `governanceDomains.${item.key}`),
  ]);
}

export function loadMap(root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), "..")) {
  const mapPath = path.join(root, "docs", "map.json");
  return JSON.parse(fs.readFileSync(mapPath, "utf8"));
}

const PROJECT_REPOSITORIES = new Map([
  ["flappibakuse/nexuspipeline", "NexusPipeline"],
  ["flappibakuse/nexuspipeline-plugins", "NexusPipeline-Plugins"],
]);

function headingSlug(value) {
  return value
    .replace(/<[^>]*>/gu, "")
    .replace(/!?\[([^\]]+)\]\([^)]*\)/gu, "$1")
    .replace(/\x60([^\r\n]*?)\x60/gu, "$1")
    .normalize("NFKC")
    .toLocaleLowerCase("en-US")
    .replace(/[^\p{L}\p{N}\s-]/gu, "")
    .trim()
    .replace(/\s+/gu, "-");
}

function markdownAnchors(text) {
  const anchors = new Set();
  const explicit = /\b(?:id|name)\s*=\s*["']([^"']+)["']/giu;
  for (const match of text.matchAll(explicit)) anchors.add(match[1]);
  const counts = new Map();
  for (const line of text.split(/\r?\n/u)) {
    const match = line.match(/^\s{0,3}#{1,6}\s+(.+?)\s*#*\s*$/u);
    if (!match) continue;
    const base = headingSlug(match[1]);
    if (!base) continue;
    const count = counts.get(base) || 0;
    counts.set(base, count + 1);
    anchors.add(count === 0 ? base : base + "-" + count);
  }
  return anchors;
}

function repositoryTarget(rawUrl) {
  let url;
  try {
    url = new URL(rawUrl);
  } catch {
    return null;
  }
  if (url.protocol !== "https:" || url.hostname.toLowerCase() !== "github.com") return null;
  const parts = url.pathname.split("/").filter(Boolean);
  if (parts.length < 2) return null;
  const repository = PROJECT_REPOSITORIES.get(parts[0].toLowerCase() + "/" + parts[1].toLowerCase());
  if (!repository || parts.length < 4 || !["blob", "tree"].includes(parts[2])) return null;
  return {
    repository,
    ref: decodeURIComponent(parts[3]),
    path: parts.slice(4).map(part => decodeURIComponent(part)).join("/"),
    fragment: url.hash ? decodeURIComponent(url.hash.slice(1)) : "",
  };
}

function findCheckout(root, workspaceRoot, repository) {
  const candidates = [
    path.join(root, repository),
    path.join(workspaceRoot, repository),
    path.join(root, "..", repository),
  ];
  for (const candidate of [...new Set(candidates.map(item => path.resolve(item)))]) {
    if (fs.existsSync(path.join(candidate, ".git")) || fs.existsSync(path.join(candidate, "README.md"))) {
      return candidate;
    }
  }
  return null;
}

function gitHead(checkout) {
  try {
    const result = spawnSync("git", ["rev-parse", "HEAD"], {
      cwd: checkout,
      encoding: "utf8",
      windowsHide: true,
      timeout: 10_000,
    });
    if (result.status === 0) return String(result.stdout || "").trim();
  } catch {
    // 缺少 git 时由调用方报告无法记录固定基线。
  }
  return "";
}

/**
 * 校验宿主与官方插件仓库之间的 GitHub blob/tree 链接。
 * 远程链接不会触发网络请求；必须先找到本地对应 checkout，再按其当前固定 HEAD 校验文件和锚点。
 */
export function validateCrossRepositoryLinks(documents, {
  root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), ".."),
  workspaceRoot = path.resolve(root, ".."),
} = {}) {
  const issues = [];
  const baselines = {};
  const checked = [];
  const urlPattern = /https:\/\/github\.com\/FlappiBakuse\/(?:NexusPipeline-Plugins|NexusPipeline)(?![-\w])(?:\/[^\s)<>"]*)?/gu;
  for (const document of documents || []) {
    const text = String(document?.text || "");
    const fileLabel = String(document?.file || "文档");
    for (const match of text.matchAll(urlPattern)) {
      const target = repositoryTarget(match[0]);
      if (!target) continue;
      const checkout = findCheckout(root, workspaceRoot, target.repository);
      if (!checkout) {
        issues.push("无法完成跨仓库检查：未找到 " + target.repository + " checkout（" + fileLabel + " -> " + match[0] + "）");
        continue;
      }
      const baseline = baselines[target.repository] || gitHead(checkout);
      if (!baseline) {
        issues.push("无法完成跨仓库检查：无法读取 " + target.repository + " 固定基线（" + checkout + "）");
        continue;
      }
      baselines[target.repository] = baseline;
      const targetPath = path.resolve(checkout, target.path);
      const relative = path.relative(checkout, targetPath);
      if (relative.startsWith("..") || path.isAbsolute(relative)) {
        issues.push("跨仓库链接越界：" + fileLabel + " -> " + match[0]);
        continue;
      }
      if (!fs.existsSync(targetPath)) {
        issues.push("跨仓库链接目标不存在：" + fileLabel + " -> " + target.repository + "@" + target.ref + "/" + target.path);
        continue;
      }
      if (target.fragment && fs.statSync(targetPath).isFile()) {
        const anchors = markdownAnchors(fs.readFileSync(targetPath, "utf8"));
        if (!anchors.has(target.fragment)) {
          issues.push("跨仓库链接锚点不存在：" + fileLabel + " -> " + target.repository + "@" + target.ref + "/" + target.path + "#" + target.fragment);
          continue;
        }
      }
      checked.push({
        file: fileLabel,
        repository: target.repository,
        ref: target.ref,
        path: target.path,
        fragment: target.fragment,
        baseline,
      });
    }
  }
  return { ok: issues.length === 0, issues, checked, baselines };
}

export function validateMap(root, map, registry = TEST_DOMAIN_REGISTRY) {
  const errors = [];
  if (!map || map.schemaVersion !== DOCS_INDEX_SCHEMA_VERSION) errors.push("docs/map.json schemaVersion 无效");
  const topics = Array.isArray(map?.topics) ? map.topics : [];
  if (!topics.length) errors.push("docs/map.json 没有主题");
  const ids = new Set();
  const paths = new Set();
  const authorities = new Set();
  const domainKeys = registryDomainKeys(registry);
  for (const topic of topics) {
    if (!topic || typeof topic.id !== "string" || !topic.id) errors.push("主题缺少 id");
    if (ids.has(topic.id)) errors.push(`主题 id 重复：${topic.id}`);
    ids.add(topic.id);
    const relativePath = normalize(topic.path);
    if (!relativePath || paths.has(relativePath)) errors.push(`主题 path 缺失或重复：${relativePath || "空"}`);
    paths.add(relativePath);
    if (relativePath && !isSafeRelativePath(relativePath)) errors.push(`主题 path 越界：${relativePath}`);
    else if (relativePath && !fs.existsSync(documentationPath(root, relativePath))) errors.push(`主题文件不存在：${relativePath}`);
    if (!Array.isArray(topic.codePaths) || topic.codePaths.length === 0) errors.push(`主题缺少 codePaths：${topic.id}`);
    for (const codePath of topic.codePaths || []) {
      const normalizedCodePath = normalize(codePath);
      if (!isSafeRelativePath(normalizedCodePath)) errors.push(`主题 codePath 越界：${topic.id} -> ${codePath}`);
      else if (!fs.existsSync(path.join(root, normalizedCodePath))) errors.push(`主题 codePath 不存在：${topic.id} -> ${codePath}`);
    }
    if (!Array.isArray(topic.testDomains) || topic.testDomains.length === 0) errors.push(`主题缺少 testDomains：${topic.id}`);
    for (const domain of topic.testDomains || []) {
      if (!domainKeys.has(domain)) errors.push(`主题引用未知测试域：${topic.id} -> ${domain}`);
    }
    for (const authority of topic.authorityFor || []) {
      if (authorities.has(authority)) errors.push(`authorityFor 重复：${authority}`);
      authorities.add(authority);
    }
    if (!["current", "historical"].includes(topic.status)) errors.push(`主题状态无效：${topic.id}`);
  }
  return { ok: errors.length === 0, errors };
}

export function findTopics(map, query, { includeHistorical = false } = {}) {
  const needle = String(query || "").trim().toLocaleLowerCase("zh-CN");
  if (!needle) return [];
  return (map.topics || [])
    .filter(topic => includeHistorical || topic.status !== "historical")
    .map(topic => ({ topic, text: [topic.id, topic.path, topic.summary, ...(topic.topics || []), ...(topic.aliases || [])].join(" ").toLocaleLowerCase("zh-CN") }))
    .filter(({ text }) => text.includes(needle))
    .map(({ topic }) => topic);
}

function main(argv) {
  const root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), "..");
  const command = argv[0] || "validate";
  const json = argv.includes("--json");
  const includeHistorical = argv.includes("--include-history");
  const query = argv.filter(arg => !arg.startsWith("--")).slice(1).join(" ");
  const map = loadMap(root);
  const validation = validateMap(root, map);
  if (!validation.ok) {
    if (json) console.log(JSON.stringify({ ok: false, errors: validation.errors }, null, 2));
    else validation.errors.forEach(error => console.error(`[文档索引] ${error}`));
    return 1;
  }
  if (command === "validate") {
    const result = { ok: true, schemaVersion: DOCS_INDEX_SCHEMA_VERSION, topicCount: map.topics.length };
    console.log(json ? JSON.stringify(result, null, 2) : `[文档索引] ${result.topicCount} 个当前主题通过校验。`);
    return 0;
  }
  if (command !== "find") throw new Error(`未知命令：${command}`);
  const results = findTopics(map, query, { includeHistorical });
  console.log(json ? JSON.stringify(results, null, 2) : results.map(topic => `${topic.id} -> ${topic.path}：${topic.summary}`).join("\n"));
  return results.length ? 0 : 1;
}

if (process.argv[1] && path.resolve(process.argv[1]) === fileURLToPath(import.meta.url)) {
  try {
    process.exitCode = main(process.argv.slice(2));
  } catch (error) {
    console.error(`[文档索引] 失败：${error.message}`);
    process.exitCode = 1;
  }
}
