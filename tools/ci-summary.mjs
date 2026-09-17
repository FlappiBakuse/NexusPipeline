import fs from "node:fs";
import crypto from "node:crypto";
import path from "node:path";
import { fileURLToPath } from "node:url";
import { CI_DOMAINS } from "./ci-domains.mjs";

/**
 * 选择性 CI 的最终汇总器。
 *
 * 这个模块只根据 changes 生成的 execution-plan 和各 job 上传的结果 manifest
 * 做裁定。它不重新解析 git diff，也不把 skipped 当作 selected job 的成功。
 */
export const CI_SUMMARY_SCHEMA_VERSION = 1;

const VALID_RESULTS = new Set(["success", "failure", "cancelled", "skipped"]);
const DOMAIN_KEYS = new Set(CI_DOMAINS.map(domain => domain.key));

function asObject(value) {
  return value && typeof value === "object" && !Array.isArray(value) ? value : null;
}

function count(value) {
  if (typeof value !== "number") return null;
  const number = Number(value);
  return Number.isInteger(number) && number >= 0 ? number : null;
}

function jobResult(value) {
  return String(asObject(value)?.result || "").toLowerCase();
}

function planPayload(plan) {
  const copy = { ...plan };
  delete copy.planDigest;
  return JSON.stringify(copy);
}

export function executionPlanDigest(plan) {
  return crypto.createHash("sha256").update(planPayload(plan), "utf8").digest("hex");
}

export function validateExecutionPlan(plan, { expectedHeadSha = "" } = {}) {
  const errors = [];
  const value = asObject(plan);
  if (!value) return { ok: false, errors: ["execution-plan 不是对象"] };
  if (value.schemaVersion !== CI_SUMMARY_SCHEMA_VERSION) errors.push("execution-plan schemaVersion 不受支持");
  if (typeof value.all !== "boolean") errors.push("execution-plan all 必须是布尔值");
  if (typeof value.base !== "string" || typeof value.head !== "string") errors.push("execution-plan 缺少 base/head");
  if (expectedHeadSha && value.head !== expectedHeadSha) errors.push(`execution-plan head SHA 不匹配：${value.head || "空"} != ${expectedHeadSha}`);
  if (typeof value.failOpen !== "boolean") errors.push("execution-plan failOpen 必须是布尔值");
  if (!Array.isArray(value.changedFiles)) errors.push("execution-plan changedFiles 必须是数组");
  if (!Array.isArray(value.unknown)) errors.push("execution-plan unknown 必须是数组");
  if (!Array.isArray(value.shared)) errors.push("execution-plan shared 必须是数组");
  const domains = asObject(value.domains);
  if (!domains) {
    errors.push("execution-plan 缺少 domains");
  } else {
    for (const domain of CI_DOMAINS) {
      const item = asObject(domains[domain.key]);
      if (!item) {
        errors.push(`execution-plan 缺少域：${domain.key}`);
        continue;
      }
      if (typeof item.affected !== "boolean") errors.push(`域 ${domain.key} 的 affected 必须是布尔值`);
      if (!Array.isArray(item.jobs) || item.jobs.length === 0 || item.jobs.some(job => typeof job !== "string" || !job)) {
        errors.push(`域 ${domain.key} 缺少有效 jobs`);
      }
      const expectedJobs = value.all ? ["full-regression"] : domain.jobs;
      if (JSON.stringify(item.jobs) !== JSON.stringify(expectedJobs)) errors.push(`域 ${domain.key} 的 jobs 与注册表不符`);
      if (!Array.isArray(item.files) || !Array.isArray(item.reasons)) errors.push(`域 ${domain.key} 缺少 files/reasons`);
    }
    for (const key of Object.keys(domains)) {
      if (!DOMAIN_KEYS.has(key)) errors.push(`execution-plan 包含未知域：${key}`);
    }
  }
  if (value.planDigest && value.planDigest !== executionPlanDigest(value)) errors.push("execution-plan planDigest 校验失败");
  return { ok: errors.length === 0, errors };
}

function normalizeJobs(jobs) {
  const entries = [];
  if (Array.isArray(jobs)) {
    for (const item of jobs) {
      const value = asObject(item);
      if (value?.jobId) entries.push([String(value.jobId), value]);
    }
  } else if (asObject(jobs)) {
    for (const [jobId, value] of Object.entries(jobs)) entries.push([jobId, asObject(value) || {}]);
  }
  return entries;
}

function manifestPresent(value) {
  const item = asObject(value);
  return item?.manifestPresent === true || asObject(item?.manifest)?.present === true;
}

function checkSelectedJob(jobId, value, { expectedHeadSha, expectedRepositorySha, expectedPlanDigest }) {
  const issues = [];
  const result = jobResult(value);
  if (!VALID_RESULTS.has(result)) issues.push(`${jobId}: 缺少或无效 result`);
  if (result !== "success") issues.push(`${jobId}: selected job 结果为 ${result || "missing"}`);

  const item = asObject(value) || {};
  if (expectedHeadSha && item.headSha !== expectedHeadSha) issues.push(`${jobId}: headSha 不匹配`);
  if (expectedRepositorySha && item.repositorySha !== expectedRepositorySha) issues.push(`${jobId}: repositorySha 不匹配`);
  if (expectedPlanDigest && item.planDigest !== expectedPlanDigest) issues.push(`${jobId}: planDigest 不匹配`);
  if (!String(item.buildFingerprint || "")) issues.push(`${jobId}: 缺少 buildFingerprint`);
  if (!manifestPresent(item)) issues.push(`${jobId}: 缺少结果 manifest`);

  const testCount = count(item.testCount);
  const passed = count(item.passed ?? item.passedCount);
  const failed = count(item.failed ?? item.failedCount);
  const skipped = count(item.skipped ?? item.skippedCount);
  if (testCount === null || testCount === 0) {
    issues.push(`${jobId}: testCount 必须是正整数`);
  } else if (passed === null || failed === null || skipped === null) {
    issues.push(`${jobId}: 缺少 passed/failed/skipped 计数`);
  } else {
    if (passed + failed + skipped !== testCount) issues.push(`${jobId}: 测试计数不守恒`);
    if (passed === 0) issues.push(`${jobId}: 没有通过的测试`);
    if (failed > 0) issues.push(`${jobId}: 存在失败测试`);
  }
  if (count(item.exitCode) !== 0) issues.push(`${jobId}: exitCode 不是 0`);
  return issues;
}

/**
 * @param {{plan: object, selector?: object, jobs: object|Array, expectedHeadSha?: string, expectedRepositorySha?: string}} input
 */
export function evaluateRequiredSummary(input) {
  const value = asObject(input) || {};
  const plan = value.plan;
  const expectedHeadSha = String(value.expectedHeadSha || "");
  const planValidation = validateExecutionPlan(plan, { expectedHeadSha });
  const issues = [...planValidation.errors];
  const selector = asObject(value.selector);
  const planDigest = asObject(plan)?.planDigest || (planValidation.ok ? executionPlanDigest(plan) : "");

  if (!selector) issues.push("缺少 changes selector 结果");
  if (selector) {
    if (jobResult(selector) !== "success") issues.push(`changes selector 结果为 ${jobResult(selector) || "missing"}`);
    if (selector.planValid !== true) issues.push("changes selector 报告计划无效");
    if (expectedHeadSha && selector.headSha !== expectedHeadSha) issues.push("changes selector headSha 不匹配");
    if (planDigest && selector.planDigest !== planDigest) issues.push("changes selector planDigest 不匹配");
  }

  const selectedJobs = new Set();
  if (planValidation.ok) {
    for (const domain of CI_DOMAINS) {
      const item = plan.domains[domain.key];
      if (!item.affected) continue;
      for (const jobId of item.jobs) selectedJobs.add(jobId);
    }
  }
  const records = new Map(normalizeJobs(value.jobs));
  if (planValidation.ok) {
    for (const jobId of selectedJobs) {
      const item = records.get(jobId);
      if (!item) {
        issues.push(`${jobId}: selected job 缺失`);
        continue;
      }
      issues.push(...checkSelectedJob(jobId, item, {
        expectedHeadSha,
        expectedRepositorySha: String(value.expectedRepositorySha || ""),
        expectedPlanDigest: planDigest,
      }));
    }
  }

  const duplicates = [];
  const seen = new Set();
  for (const [jobId] of normalizeJobs(value.jobs)) {
    if (seen.has(jobId)) duplicates.push(jobId);
    seen.add(jobId);
  }
  if (duplicates.length) issues.push(`结果中存在重复 job：${[...new Set(duplicates)].join(", ")}`);

  const duplicateExecutions = [];
  for (const [jobId, item] of records) {
    if (["failure", "cancelled"].includes(jobResult(item))) issues.push(`${jobId}: 已执行作业失败或取消`);
    if (!selectedJobs.has(jobId) && jobResult(item) !== "skipped") duplicateExecutions.push(jobId);
  }

  const selected = [...selectedJobs].sort();
  const missing = selected.filter(jobId => !records.has(jobId));
  return {
    schemaVersion: CI_SUMMARY_SCHEMA_VERSION,
    ok: issues.length === 0,
    issues,
    selectedJobs: selected,
    missingJobs: missing,
    duplicateExecutions: duplicateExecutions.sort(),
    planDigest,
  };
}

function parseArgs(argv) {
  const options = { plan: "", results: "", output: "", headSha: "", repositorySha: "" };
  for (let index = 0; index < argv.length; index++) {
    const arg = argv[index];
    const name = arg.startsWith("--") ? arg.slice(2) : "";
    if (!(name in options)) throw new Error(`未知参数：${arg}`);
    options[name] = argv[++index];
    if (!options[name]) throw new Error(`${arg} 需要文件路径或 SHA`);
  }
  if (!options.plan || !options.results) throw new Error("需要 --plan 与 --results");
  return options;
}

function readJson(file) {
  return JSON.parse(fs.readFileSync(path.resolve(file), "utf8"));
}

function main(argv) {
  const options = parseArgs(argv);
  const result = evaluateRequiredSummary({
    plan: readJson(options.plan),
    ...readJson(options.results),
    expectedHeadSha: options.headSha,
    expectedRepositorySha: options.repositorySha,
  });
  const output = `${JSON.stringify(result, null, 2)}\n`;
  if (options.output) fs.writeFileSync(path.resolve(options.output), output, "utf8");
  process.stdout.write(output);
  return result.ok ? 0 : 1;
}

if (process.argv[1] && path.resolve(process.argv[1]) === fileURLToPath(import.meta.url)) {
  try {
    process.exitCode = main(process.argv.slice(2));
  } catch (error) {
    console.error(`[required-summary] 失败：${error.message}`);
    process.exitCode = 1;
  }
}
