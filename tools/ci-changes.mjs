import crypto from "node:crypto";
import { spawnSync } from "node:child_process";
import fs from "node:fs";
import path from "node:path";
import { fileURLToPath } from "node:url";
import {
  CI_DOMAINS,
  CI_EXECUTION_PLAN_SCHEMA_VERSION,
  CI_JOB_CONTRACTS,
  CI_SHARED_PATHS,
  FRONTEND_TEST_GROUPS,
  GOVERNANCE_DOMAINS,
  HOST_TEST_AREAS,
  PLUGIN_CONTRACT_FALLBACKS,
  SYSTEM_TEST_GROUPS,
  TEST_DOMAIN_CONSUMERS,
  expectedTestSelectors,
  globToRegExp,
  logicalGroupId,
  plannedExclusions,
  testSelectionIdentity,
} from "./ci-domains.mjs";
import { expectedTestFiles } from "./test-selection.mjs";
import { validateExecutionPlan } from "./ci-summary.mjs";

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

function sha256Json(value) {
  return crypto.createHash("sha256").update(JSON.stringify(value), "utf8").digest("hex");
}

function readCounterpartCandidate() {
  const lockPath = path.join(projectRoot, "plugins.lock.json");
  let lock = {};
  try {
    lock = JSON.parse(fs.readFileSync(lockPath, "utf8"));
  } catch {
    // 计划仍会明确记录缺失候选，Required Summary 会拒绝无法配对的正式结果。
  }
  const repository = String(lock.repository || "");
  const ref = String(process.env.NEXUS_CANDIDATE_REF || lock.ref || "").trim();
  return {
    repository: /^[A-Za-z0-9_.-]+\/[A-Za-z0-9_.-]+$/u.test(repository) ? repository : "",
    sha: /^[0-9a-f]{40}$/u.test(ref) ? ref : "",
  };
}

function buildInputs({ base, head, changedFiles, counterpart }) {
  const sourceDigest = sha256Json({
    repository: process.env.GITHUB_REPOSITORY || "FlappiBakuse/NexusPipeline",
    base,
    head,
    changedFiles: [...changedFiles].map(file => normalizeChangePath(file) ?? String(file ?? "")),
  });
  const inputs = {
    sourceRepository: process.env.GITHUB_REPOSITORY || "FlappiBakuse/NexusPipeline",
    sourceSha: head,
    baseSha: base,
    sourceDigest,
    targetFramework: "net8.0-windows",
    runtimeIdentifier: "win-x64",
    frontendResourceSource: "frontend/src -> frontend/dist -> release/wwwroot",
    applicationManifest: "src/app.manifest",
    testHostManifest: "src/app.test.manifest",
    toolchain: { nodeMajor: 24, dotnetMajor: 8, npmLock: "frontend/package-lock.json" },
    counterpartRepository: counterpart.repository,
    counterpartSha: counterpart.sha,
  };
  return {
    ...inputs,
    buildInputsDigest: sha256Json(inputs),
  };
}

function jobContract(jobId, all) {
  const contract = CI_JOB_CONTRACTS[jobId] || { mode: "ci", requiredIntegrity: "none" };
  if (all) return CI_JOB_CONTRACTS["full-regression"];
  return contract;
}

function logicalGroup({ kind, group, physicalJobId, all = false }) {
  const contract = jobContract(physicalJobId, all);
  const mode = contract.mode;
  return {
    groupId: logicalGroupId(kind, group.key),
    kind,
    key: group.key,
    physicalJobId: all ? "full-regression" : physicalJobId,
    mode,
    requiredIntegrity: contract.requiredIntegrity,
    requiredChecks: [...(contract.requiredChecks || [])],
    testSelectionIdentity: testSelectionIdentity(kind, group),
    expectedTests: expectedTestSelectors(kind, group),
    expectedFiles: expectedTestFiles(kind, group, { root: projectRoot }),
    exclusions: plannedExclusions(kind, group.key, mode),
  };
}

function selectedLogicalGroups({ result, testGroups, all }) {
  if (all) {
    return [
      ...HOST_TEST_AREAS.map(group => logicalGroup({ kind: "host", group, physicalJobId: "host-core", all })),
      ...FRONTEND_TEST_GROUPS.map(group => logicalGroup({ kind: "frontend", group, physicalJobId: "frontend-unit", all })),
      ...["docs", "plugin", "ui"].map(key => {
        const domain = CI_DOMAINS.find(item => item.key === key);
        return logicalGroup({
          kind: "domain",
          group: { key, paths: domain.paths, testPaths: domain.testPaths || [] },
          physicalJobId: domain.jobs[0],
          all,
        });
      }),
      ...SYSTEM_TEST_GROUPS.map(group => logicalGroup({ kind: "system", group, physicalJobId: group.ciDomain ? CI_DOMAINS.find(domain => domain.key === group.ciDomain)?.jobs?.[0] : "", all })),
      ...GOVERNANCE_DOMAINS.map(group => logicalGroup({ kind: "governance", group, physicalJobId: "docs-i18n", all })),
    ];
  }

  const groups = [];
  if (result.domains.host?.affected) {
    for (const key of testGroups.host) {
      const definition = HOST_TEST_AREAS.find(group => group.key === key);
      if (definition) groups.push(logicalGroup({ kind: "host", group: definition, physicalJobId: "host-core" }));
    }
  }
  if (result.domains.frontend?.affected) {
    for (const key of testGroups.frontend) {
      const definition = FRONTEND_TEST_GROUPS.find(group => group.key === key);
      if (definition) groups.push(logicalGroup({ kind: "frontend", group: definition, physicalJobId: "frontend-unit" }));
    }
  }
  if (result.domains.plugin?.affected) {
    if (!result.domains.host?.affected) {
      for (const key of PLUGIN_CONTRACT_FALLBACKS.host) {
        const definition = HOST_TEST_AREAS.find(group => group.key === key);
        if (definition) groups.push(logicalGroup({ kind: "host", group: definition, physicalJobId: "plugin-contract" }));
      }
    }
    if (!result.domains.frontend?.affected) {
      for (const key of PLUGIN_CONTRACT_FALLBACKS.frontend) {
        const definition = FRONTEND_TEST_GROUPS.find(group => group.key === key);
        if (definition) groups.push(logicalGroup({ kind: "frontend", group: definition, physicalJobId: "plugin-contract" }));
      }
    }
  }
  const addDomain = (key, physicalJobId) => {
    const domain = CI_DOMAINS.find(item => item.key === key);
    if (!domain || !result.domains[key]?.affected) return;
    groups.push(logicalGroup({
      kind: "domain",
      group: { key, paths: domain.paths, testPaths: domain.testPaths || [] },
      physicalJobId,
    }));
  };
  addDomain("docs", "docs-i18n");
  addDomain("plugin", "plugin-contract");
  addDomain("ui", "ui-smoke");
  for (const group of SYSTEM_TEST_GROUPS) {
    if (result.domains[group.ciDomain]?.affected) {
      const physicalJobId = CI_DOMAINS.find(domain => domain.key === group.ciDomain)?.jobs?.[0];
      groups.push(logicalGroup({ kind: "system", group, physicalJobId }));
    }
  }
  return groups;
}

export { globToRegExp };

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
  const files = Array.isArray(changedFiles) ? changedFiles : [];
  const emptyChangeSet = files.length === 0 && !all;
  const result = evaluateDomains(files, {
    failOpen: failOpen || all || emptyChangeSet,
    reason: reason || (emptyChangeSet ? "改动列表为空，按全量门禁处理" : ""),
  });
  const testGroups = {
    host: result.domains.host.affected ? selectTestGroups("host", files, result.failOpen) : [],
    frontend: result.domains.frontend.affected ? selectTestGroups("frontend", files, result.failOpen) : [],
  };
  const counterpart = readCounterpartCandidate();
  const normalizedChangedFiles = files.map(file => normalizeChangePath(file) ?? String(file ?? ""));
  const plan = {
    schemaVersion: CI_EXECUTION_PLAN_SCHEMA_VERSION,
    all,
    base,
    head,
    changedFiles: normalizedChangedFiles,
    failOpen: result.failOpen,
    reason: result.reason,
    unknown: result.unknown,
    shared: result.shared,
    counterpartRepository: counterpart.repository,
    counterpartSha: counterpart.sha,
    buildInputs: buildInputs({ base, head, changedFiles: normalizedChangedFiles, counterpart }),
    testGroups,
    domains: Object.fromEntries(CI_DOMAINS.map(domain => [domain.key, {
      affected: result.domains[domain.key].affected,
      files: result.domains[domain.key].files,
      reasons: result.domains[domain.key].reasons,
      jobs: all ? ["full-regression"] : domain.jobs ?? [domain.key],
    }])),
  };
  plan.selectedGroups = selectedLogicalGroups({ result, testGroups, all });
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

  const result = evaluateDomains(changedFiles, {
    failOpen: failOpen || options.all || changedFiles.length === 0,
    reason: reason || (changedFiles.length === 0 && !options.all ? "改动列表为空，按全量门禁处理" : ""),
  });
  const plan = createExecutionPlan(changedFiles, { base, head, failOpen, reason, all: options.all });
  const planValidation = options.dryRun
    ? { ok: true, errors: [] }
    : validateExecutionPlan(plan, { expectedHeadSha: head });
  if (!planValidation.ok) {
    console.error(`[影响域] 执行计划无效：${planValidation.errors.join("; ")}`);
  }
  for (const line of describeReport(result.domains, result)) console.log(line);
  const lines = renderDomainLines(result.domains);
  const outputPath = options.githubOutput || process.env.GITHUB_OUTPUT;
  if (outputPath && !options.dryRun) {
    fs.appendFileSync(outputPath, `${lines.join("\n")}\n`, "utf8");
    fs.appendFileSync(outputPath, [
      `plan_digest=${plan.planDigest}`,
      `plan_valid=${planValidation.ok ? "true" : "false"}`,
      `counterpart_repository=${plan.counterpartRepository}`,
      `counterpart_sha=${plan.counterpartSha}`,
      `source_digest=${plan.buildInputs.sourceDigest}`,
      `build_inputs_digest=${plan.buildInputs.buildInputsDigest}`,
      "execution_plan<<NEXUS_EXECUTION_PLAN",
      JSON.stringify(plan),
      "NEXUS_EXECUTION_PLAN",
      "",
    ].join("\n"), "utf8");
    console.log(`[影响域] 已写入 ${outputPath}。`);
  }
  if (options.planFile && !options.dryRun) {
    fs.writeFileSync(options.planFile, `${JSON.stringify(plan, null, 2)}\n`, "utf8");
    console.log(`[影响域] 已写入执行计划 ${options.planFile}。`);
  }
  if (options.dryRun) console.log(`[影响域] 干跑：不写入输出文件、执行计划或其他状态。`);
  return planValidation.ok ? 0 : 1;
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
