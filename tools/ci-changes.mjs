import crypto from "node:crypto";
import { spawnSync } from "node:child_process";
import fs from "node:fs";
import path from "node:path";
import { fileURLToPath } from "node:url";
import { CI_DOMAINS, CI_SHARED_PATHS, HOST_TEST_AREAS, FRONTEND_TEST_GROUPS, TEST_DOMAIN_CONSUMERS } from "./ci-domains.mjs";

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
  if (typeof value !== "string") return null;
  const normalized = value.trim().replaceAll("\\", "/").replace(/^\.\//u, "");
  if (!normalized || normalized.startsWith("/") || /^[A-Za-z]:\//u.test(normalized)) return null;
  const segments = normalized.split("/");
  if (segments.some(segment => segment === "..")) return null;
  return segments.filter(Boolean).join("/") || null;
}

export function matchDomains(changedFiles) {
  const matched = new Map(CI_DOMAINS.map(domain => [domain.key, []]));
  const shared = [];
  const unknown = [];
  for (const rawFile of changedFiles) {
    const file = normalizeChangePath(rawFile);
    if (!file) {
      unknown.push({ file: String(rawFile ?? ""), reason: "路径为空或包含绝对/父级路径" });
      continue;
    }
    const sharedHit = SHARED_MATCHERS.find(entry => entry.matcher.test(file));
    if (sharedHit) {
      shared.push({ file, pattern: sharedHit.pattern });
      continue;
    }
    let matchedFile = false;
    for (const domain of DOMAIN_MATCHERS) {
      const hit = domain.patterns.find(entry => entry.matcher.test(file));
      if (hit) {
        matched.get(domain.key).push({ file, pattern: hit.pattern });
        matchedFile = true;
      }
    }
    if (!matchedFile) unknown.push({ file, reason: "未命中影响域规则" });
  }
  return { matched, shared, unknown };
}

export function evaluateDomains(changedFiles, { failOpen = false, reason = "" } = {}) {
  const { matched, shared, unknown } = matchDomains(changedFiles);
  const hits = Object.fromEntries([...matched.entries()].map(([key, list]) => [key, list.length]));
  const unmatchedChange = !failOpen && unknown.length > 0;
  const effectiveFailOpen = failOpen || unmatchedChange || shared.length > 0;
  const effectiveReason = failOpen
    ? reason || "无法取得改动列表"
    : unmatchedChange
      ? `存在未分类改动（${unknown[0].file || "空路径"}，未命中任何影响域），按全量门禁处理`
      : shared.length > 0
        ? `改动包含共享路径 ${shared[0].pattern}，按全量门禁处理`
        : "";
  const domains = {};
  for (const domain of CI_DOMAINS) {
    domains[domain.key] = {
      affected: effectiveFailOpen || hits[domain.key] > 0,
      files: (matched.get(domain.key) ?? []).map(hit => hit.file),
      pattern: matched.get(domain.key)?.[0]?.pattern ?? "",
      reasons: (matched.get(domain.key) ?? []).map(hit => `${hit.file} ← ${hit.pattern}`),
    };
  }
  return { domains, failOpen: effectiveFailOpen, reason: effectiveReason, hits, shared, unknown };
}

export function createExecutionPlan(changedFiles, {
  base = "",
  head = "",
  failOpen = false,
  reason = "",
  all = false,
} = {}) {
  const result = evaluateDomains(changedFiles, { failOpen: failOpen || all, reason });
  const plan = {
    schemaVersion: 1,
    all,
    base,
    head,
    changedFiles: changedFiles.map(file => normalizeChangePath(file) ?? String(file ?? "")),
    failOpen: result.failOpen,
    reason: result.reason,
    unknown: result.unknown,
    shared: result.shared,
    testGroups: {
      host: selectTestGroups("host", changedFiles, result.failOpen),
      frontend: selectTestGroups("frontend", changedFiles, result.failOpen),
    },
    domains: Object.fromEntries(CI_DOMAINS.map(domain => [domain.key, {
      affected: result.domains[domain.key].affected,
      files: result.domains[domain.key].files,
      reasons: result.domains[domain.key].reasons,
      jobs: all ? ["full-regression"] : domain.jobs ?? [domain.key],
    }])),
  };
  plan.planDigest = crypto.createHash("sha256")
    .update(JSON.stringify(plan), "utf8")
    .digest("hex");
  return plan;
}

export function selectTestGroups(kind, changedFiles, all = false) {
  const registry = kind === "host" ? HOST_TEST_AREAS : FRONTEND_TEST_GROUPS;
  const keys = registry.map(group => group.key);
  if (all) return keys;
  const relevant = changedFiles.map(normalizeChangePath).filter(Boolean).filter(file => kind === "host"
    ? file.startsWith("src/") || file.startsWith("tests/NexusPipeline.Tests/")
    : file.startsWith("frontend/"));
  const selected = new Set();
  for (const file of relevant) {
    const hits = registry.filter(group => [...group.paths, ...(group.testPaths || []).map(pattern => kind === "host" ? `tests/NexusPipeline.Tests/${pattern}` : pattern)].some(pattern => globToRegExp(pattern).test(file)));
    if (!hits.length) return keys;
    hits.forEach(group => selected.add(group.key));
  }
  // 构建或工作流路径触发某 Gate 时，以完整集合承担验证。
  if (!selected.size) return keys;
  for (const key of selected) for (const consumer of TEST_DOMAIN_CONSUMERS[kind][key] || []) selected.add(consumer);
  return keys.filter(key => selected.has(key));
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

/** Parse `git diff --name-status -z` while retaining both sides of renames/copies. */
export function parseNameStatusZ(output) {
  const tokens = String(output ?? "").split("\0");
  const files = [];
  let index = 0;
  while (index < tokens.length) {
    const status = tokens[index++];
    if (!status) continue;
    if (status.startsWith("R") || status.startsWith("C")) {
      const oldPath = tokens[index++];
      const newPath = tokens[index++];
      if (oldPath) files.push(oldPath);
      if (newPath) files.push(newPath);
      continue;
    }
    const file = tokens[index++];
    if (file) files.push(file);
  }
  return files;
}

function parseArguments(argv) {
  const options = { base: null, head: null, all: false, githubOutput: null, planFile: null, dryRun: false };
  for (let index = 0; index < argv.length; index++) {
    const arg = argv[index];
    switch (arg) {
      case "--base":
        options.base = argv[++index];
        if (!options.base) throw new Error("--base 需要提交 SHA");
        break;
      case "--head":
        options.head = argv[++index];
        if (!options.head) throw new Error("--head 需要提交 SHA");
        break;
      case "--github-output":
        options.githubOutput = argv[++index];
        if (!options.githubOutput) throw new Error("--github-output 需要文件路径");
        break;
      case "--plan-file":
        options.planFile = argv[++index];
        if (!options.planFile) throw new Error("--plan-file 需要文件路径");
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
  let base = options.base || process.env.NEXUS_CI_BASE_SHA || process.env.GITHUB_BASE_SHA || "";
  let head = options.head || process.env.NEXUS_CI_HEAD_SHA || process.env.GITHUB_SHA || "";

  if (!options.all) {
    try {
      if (!base || !head) throw new Error("缺少 base/head 提交");
      changedFiles = collectChangedFiles(base, head);
      console.log(`[影响域] 改动文件 ${changedFiles.length} 个（${base.slice(0, 8)}..${head.slice(0, 8)}）。`);
    } catch (error) {
      failOpen = true;
      reason = `改动列表不可用：${error.message}`;
    }
  }

  const result = evaluateDomains(changedFiles, { failOpen, reason });
  const plan = createExecutionPlan(changedFiles, { base, head, failOpen, reason, all: options.all });
  for (const line of describeReport(result.domains, result)) console.log(line);
  const lines = renderDomainLines(result.domains);
  const outputPath = options.githubOutput || process.env.GITHUB_OUTPUT;
  if (outputPath && !options.dryRun) {
    fs.appendFileSync(outputPath, `${lines.join("\n")}\n`, "utf8");
    fs.appendFileSync(outputPath, `plan_digest=${plan.planDigest}\nplan_valid=true\nexecution_plan<<NEXUS_EXECUTION_PLAN\n${JSON.stringify(plan)}\nNEXUS_EXECUTION_PLAN\n`, "utf8");
    console.log(`[影响域] 已写入 ${outputPath}。`);
  }
  if (options.planFile && !options.dryRun) {
    fs.writeFileSync(options.planFile, `${JSON.stringify(plan, null, 2)}\n`, "utf8");
    console.log(`[影响域] 已写入执行计划 ${options.planFile}。`);
  }
  if (options.dryRun) console.log(`[影响域] 干跑：不写入输出文件、执行计划或其他状态。`);
  return 0;
}

export function collectChangedFiles(base, head) {
  const result = spawnSync("git", ["diff", "--name-status", "--find-renames", "-z", base, head], {
    cwd: projectRoot,
    encoding: "buffer",
    maxBuffer: 32 * 1024 * 1024,
  });
  if (result.error) throw new Error(result.error.message);
  if (result.status !== 0) {
    throw new Error((result.stderr?.toString("utf8") || "").trim() || `git diff 退出码 ${result.status}`);
  }
  return parseNameStatusZ(result.stdout.toString("utf8"));
}

if (process.argv[1] && path.resolve(process.argv[1]) === fileURLToPath(import.meta.url)) {
  try {
    process.exitCode = main();
  } catch (error) {
    console.error(`[影响域] 失败：${error.message}`);
    process.exitCode = 1;
  }
}
