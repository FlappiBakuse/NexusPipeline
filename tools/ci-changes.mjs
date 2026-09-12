import { spawnSync } from "node:child_process";
import fs from "node:fs";
import path from "node:path";
import { fileURLToPath } from "node:url";
import { CI_DOMAINS, CI_SHARED_PATHS } from "./ci-domains.mjs";

/**
 * CI 影响域判定：把改动文件集合映射为各 Gate 的影响域标记。
 *
 * 用法：
 *   node tools/ci-changes.mjs --base <sha> --head <sha> [--github-output <file>]
 *   node tools/ci-changes.mjs --all [--github-output <file>]
 *   node tools/ci-changes.mjs --dry-run --base <sha> --head <sha>
 *
 * 输出 `key=true|false` 行；`--github-output` 同时写入 GITHUB_OUTPUT 风格文件。
 * 无法取得改动列表或改动未命中任何影响域时按全量门禁处理（fail-open）。
 */

const projectRoot = path.resolve(path.dirname(fileURLToPath(import.meta.url)), "..");

function escapeRegExp(value) {
  return value.replace(/[.*+?^${}()|[\]\\]/gu, "\\$&");
}

export function globToRegExp(glob) {
  const segments = glob.split("/");
  let source = "^";
  for (let index = 0; index < segments.length; index++) {
    const segment = segments[index];
    const isLast = index === segments.length - 1;
    if (segment === "**") {
      if (isLast) {
        source += ".*";
        break;
      }
      source += "(?:[^/]+/)*";
      continue;
    }
    source += escapeRegExp(segment).replaceAll("\\*", "[^/]*");
    if (!isLast) source += "/";
  }
  return new RegExp(`${source}$`, "u");
}

const DOMAIN_MATCHERS = CI_DOMAINS.map(domain => ({
  key: domain.key,
  patterns: domain.paths.map(pattern => ({ pattern, matcher: globToRegExp(pattern) })),
}));

const SHARED_MATCHERS = CI_SHARED_PATHS.map(pattern => ({ pattern, matcher: globToRegExp(pattern) }));

export function normalizeChangePath(value) {
  return value.trim().replaceAll("\\", "/").replace(/^\.\//u, "").replace(/^\/+/u, "");
}

export function matchDomains(changedFiles) {
  const matched = new Map(CI_DOMAINS.map(domain => [domain.key, []]));
  const shared = [];
  for (const rawFile of changedFiles) {
    const file = normalizeChangePath(rawFile);
    if (!file) continue;
    const sharedHit = SHARED_MATCHERS.find(entry => entry.matcher.test(file));
    if (sharedHit) {
      shared.push({ file, pattern: sharedHit.pattern });
      continue;
    }
    for (const domain of DOMAIN_MATCHERS) {
      const hit = domain.patterns.find(entry => entry.matcher.test(file));
      if (hit) matched.get(domain.key).push({ file, pattern: hit.pattern });
    }
  }
  return { matched, shared };
}

export function evaluateDomains(changedFiles, { failOpen = false, reason = "" } = {}) {
  const { matched, shared } = matchDomains(changedFiles);
  const hits = Object.fromEntries([...matched.entries()].map(([key, list]) => [key, list.length]));
  const totalHits = Object.values(hits).reduce((sum, value) => sum + value, 0);
  const unmatchedChange = !failOpen && changedFiles.length > 0 && totalHits === 0 && shared.length === 0;
  const effectiveFailOpen = failOpen || unmatchedChange || shared.length > 0;
  const effectiveReason = failOpen
    ? reason || "无法取得改动列表"
    : unmatchedChange
      ? "改动未命中任何影响域，按全量门禁处理"
      : shared.length > 0
        ? `改动包含共享路径 ${shared[0].pattern}，按全量门禁处理`
        : "";
  const domains = {};
  for (const domain of CI_DOMAINS) {
    domains[domain.key] = {
      affected: effectiveFailOpen || hits[domain.key] > 0,
      files: (matched.get(domain.key) ?? []).map(hit => hit.file),
      pattern: matched.get(domain.key)?.[0]?.pattern ?? "",
    };
  }
  return { domains, failOpen: effectiveFailOpen, reason: effectiveReason, hits, shared };
}

export function renderDomainLines(domains) {
  return CI_DOMAINS.map(domain => `${domain.key}=${domains[domain.key].affected ? "true" : "false"}`);
}

function describeReport(domains, { failOpen, reason, hits, shared }) {
  const lines = [];
  if (failOpen) lines.push(`[影响域] ${reason}，全部 Gate 视为受影响。`);
  for (const domain of CI_DOMAINS) {
    const value = domains[domain.key];
    const state = value.affected ? "affected" : "skipped";
    const detail = hits[domain.key] > 0
      ? `${hits[domain.key]} 个文件，触发规则 ${value.pattern}`
      : shared.length > 0
        ? `共享路径 ${shared[0].file}`
        : failOpen
          ? "fail-open 兜底"
          : "无命中文件";
    lines.push(`[影响域] ${domain.key} (${domain.gate})：${state}，${detail}`);
  }
  return lines;
}

function parseArguments(argv) {
  const options = { base: null, head: null, all: false, githubOutput: null, dryRun: false };
  for (let index = 0; index < argv.length; index++) {
    const arg = argv[index];
    switch (arg) {
      case "--base":
        options.base = argv[++index];
        break;
      case "--head":
        options.head = argv[++index];
        break;
      case "--github-output":
        options.githubOutput = argv[++index];
        break;
      case "--all":
        options.all = true;
        break;
      case "--dry-run":
        options.dryRun = true;
        break;
      default:
        throw new Error(`未知参数：${arg}`);
    }
  }
  return options;
}

function main() {
  const options = parseArguments(process.argv.slice(2));
  let changedFiles = [];
  let failOpen = options.all;
  let reason = options.all ? "按全量门禁执行" : "";
  let base = options.base;
  let head = options.head;

  if (!options.all) {
    try {
      base = base || process.env.NEXUS_CI_BASE_SHA || process.env.GITHUB_BASE_SHA || "";
      head = head || process.env.NEXUS_CI_HEAD_SHA || process.env.GITHUB_SHA || "";
      if (!base || !head) throw new Error("缺少 base/head 提交");
      changedFiles = collectChangedFiles(base, head);
      console.log(`[影响域] 改动文件 ${changedFiles.length} 个（${base.slice(0, 8)}..${head.slice(0, 8)}）。`);
    } catch (error) {
      failOpen = true;
      reason = `改动列表不可用：${error.message}`;
    }
  }

  const result = evaluateDomains(changedFiles, { failOpen, reason });
  for (const line of describeReport(result.domains, result)) console.log(line);
  const lines = renderDomainLines(result.domains);
  const outputPath = options.githubOutput || process.env.GITHUB_OUTPUT;
  if (outputPath) {
    fs.appendFileSync(outputPath, `${lines.join("\n")}\n`, "utf8");
    console.log(`[影响域] 已写入 ${outputPath}。`);
  }
  if (options.dryRun) console.log(`[影响域] 干跑：不写入输出文件，不改变任何状态。`);
  return 0;
}

function collectChangedFiles(base, head) {
  const result = spawnSync("git", ["diff", "--name-only", "--no-renames", base, head], {
    cwd: projectRoot,
    encoding: "utf8",
    maxBuffer: 32 * 1024 * 1024,
  });
  if (result.error) throw new Error(result.error.message);
  if (result.status !== 0) {
    throw new Error((result.stderr || "").trim() || `git diff 退出码 ${result.status}`);
  }
  return result.stdout.split(/\r?\n/u).map(line => line.trim()).filter(Boolean);
}

if (process.argv[1] && path.resolve(process.argv[1]) === fileURLToPath(import.meta.url)) {
  try {
    process.exitCode = main();
  } catch (error) {
    console.error(`[影响域] 失败：${error.message}`);
    process.exitCode = 1;
  }
}
