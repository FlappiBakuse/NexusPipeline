import fs from "node:fs";
import path from "node:path";
import { spawnSync } from "node:child_process";
import { fileURLToPath } from "node:url";
import { TEST_DOMAIN_REGISTRY } from "./ci-domains.mjs";
import { collectAnchors, findMarkdownLinks } from "./markdown.mjs";

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
  let ref;
  let targetPath;
  let fragment;
  try {
    ref = decodeURIComponent(parts[3]);
    targetPath = parts.slice(4).map(part => decodeURIComponent(part)).join("/");
    fragment = url.hash ? decodeURIComponent(url.hash.slice(1)) : "";
  } catch {
    return null;
  }
  return {
    repository,
    kind: parts[2],
    ref,
    path: targetPath,
    fragment,
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

function runGit(checkout, args) {
  try {
    return spawnSync("git", args, {
      cwd: checkout,
      encoding: "utf8",
      windowsHide: true,
      timeout: 10_000,
    });
  } catch {
    return null;
  }
}

function resolveGitRevision(checkout, ref) {
  if (!ref || ref.startsWith("-") || /\s/u.test(ref)) return "";
  const result = runGit(checkout, ["rev-parse", "--verify", `${ref}^{commit}`]);
  return result?.status === 0 ? String(result.stdout || "").trim() : "";
}

function listGitPaths(checkout, revision) {
  const result = runGit(checkout, ["ls-tree", "-r", "--name-only", revision]);
  if (!result || result.status !== 0) return null;
  return new Set(String(result.stdout || "").split(/\r?\n/u).map(value => value.trim()).filter(Boolean));
}

function readGitFile(checkout, revision, relativePath) {
  const result = runGit(checkout, ["show", `${revision}:${relativePath}`]);
  return result?.status === 0 ? String(result.stdout || "") : null;
}

function sourceLineRange(fragment) {
  const match = /^L([1-9]\d*)(?:-L?([1-9]\d*))?$/u.exec(fragment);
  if (!match) return null;
  return { start: Number(match[1]), end: Number(match[2] || match[1]) };
}

function fragmentExists(content, fragment) {
  const lineRange = sourceLineRange(fragment);
  if (lineRange) {
    const lineCount = String(content).split(/\r?\n/u).length;
    return lineRange.start <= lineCount && lineRange.end <= lineCount && lineRange.start <= lineRange.end;
  }
  return collectAnchors(content).has(fragment);
}

/**
 * 校验宿主与官方插件仓库之间的 GitHub blob/tree 链接。
 * 远程链接不会触发网络请求；必须先找到本地对应 checkout，再按链接声明的 ref 读取 Git 对象。
 */
export function validateCrossRepositoryLinks(documents, {
  root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), ".."),
  workspaceRoot = path.resolve(root, ".."),
} = {}) {
  const issues = [];
  const baselines = {};
  const checked = [];
  for (const document of documents || []) {
    const text = String(document?.text || "");
    const fileLabel = String(document?.file || "文档");
    for (const link of findMarkdownLinks(text)) {
      const target = repositoryTarget(link.rawTarget);
      if (!target) continue;
      const checkout = findCheckout(root, workspaceRoot, target.repository);
      if (!checkout) {
        issues.push("无法完成跨仓库检查：未找到 " + target.repository + " checkout（" + fileLabel + " -> " + link.rawTarget + "）");
        continue;
      }
      const baseline = baselines[target.repository] || gitHead(checkout);
      if (!baseline) {
        issues.push("无法完成跨仓库检查：无法读取 " + target.repository + " 固定基线（" + checkout + "）");
        continue;
      }
      baselines[target.repository] = baseline;
      const normalizedTargetPath = normalize(target.path);
      if (!isSafeRelativePath(normalizedTargetPath) || normalizedTargetPath === ".") {
        issues.push("跨仓库链接越界：" + fileLabel + " -> " + link.rawTarget);
        continue;
      }
      const resolvedSha = resolveGitRevision(checkout, target.ref);
      if (!resolvedSha) {
        issues.push("无法完成跨仓库检查：无法解析 " + target.repository + " ref " + target.ref + "（" + fileLabel + " -> " + link.rawTarget + "）");
        continue;
      }
      const gitPaths = listGitPaths(checkout, resolvedSha);
      if (!gitPaths) {
        issues.push("无法完成跨仓库检查：无法读取 " + target.repository + "@" + resolvedSha + " 文件树（" + fileLabel + " -> " + link.rawTarget + "）");
        continue;
      }
      const targetIsFile = gitPaths.has(normalizedTargetPath);
      const targetIsTree = target.kind === "tree" && [...gitPaths].some(item => item.startsWith(normalizedTargetPath + "/"));
      if (!targetIsFile && !targetIsTree) {
        issues.push("跨仓库链接目标不存在：" + fileLabel + " -> " + target.repository + "@" + target.ref + "/" + normalizedTargetPath);
        continue;
      }
      if (target.fragment) {
        if (!targetIsFile) {
          issues.push("跨仓库链接锚点目标不是文件：" + fileLabel + " -> " + target.repository + "@" + target.ref + "/" + normalizedTargetPath + "#" + target.fragment);
          continue;
        }
        const content = readGitFile(checkout, resolvedSha, normalizedTargetPath);
        if (content === null || !fragmentExists(content, target.fragment)) {
          issues.push("跨仓库链接锚点不存在：" + fileLabel + " -> " + target.repository + "@" + target.ref + "/" + normalizedTargetPath + "#" + target.fragment);
          continue;
        }
      }
      checked.push({
        file: fileLabel,
        repository: target.repository,
        kind: target.kind,
        ref: target.ref,
        path: normalizedTargetPath,
        fragment: target.fragment,
        baseline,
        resolvedSha,
        verification: "candidate-checkout",
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
