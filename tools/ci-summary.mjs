import crypto from "node:crypto";
import fs from "node:fs";
import path from "node:path";
import { fileURLToPath } from "node:url";
import {
  CI_DOMAINS,
  CI_EXECUTION_PLAN_SCHEMA_VERSION,
  CI_JOB_CONTRACTS,
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
import { validateArtifactManifestShape } from "./artifact-manifest.mjs";
import { expectedTestFiles } from "./test-selection.mjs";

/**
 * 选择性 CI 的最终汇总器。
 *
 * 计划枚举逻辑组，物理 Job 只负责承载资源和并发。Required Summary 必须同时核对
 * 计划、selector、Job manifest、按组引擎结果、运行模式、完整性、双仓库候选和构建身份。
 * 任何一组漏跑都不能被同一 Job 中另一组的通过数量抵消。
 */
export const CI_SUMMARY_SCHEMA_VERSION = CI_EXECUTION_PLAN_SCHEMA_VERSION;

const VALID_RESULTS = new Set(["success", "failure", "cancelled", "skipped"]);
const VALID_MODES = new Set(["ci", "admin", "test-host"]);
const DOMAIN_KEYS = new Set(CI_DOMAINS.map(domain => domain.key));
const JOB_KEYS = new Set(Object.keys(CI_JOB_CONTRACTS));
const HOST_GROUPS = new Map(HOST_TEST_AREAS.map(group => [logicalGroupId("host", group.key), group]));
const FRONTEND_GROUPS = new Map(FRONTEND_TEST_GROUPS.map(group => [logicalGroupId("frontend", group.key), group]));
const SYSTEM_GROUPS = new Map(SYSTEM_TEST_GROUPS.map(group => [logicalGroupId("system", group.key), group]));
const GOVERNANCE_GROUPS = new Map(GOVERNANCE_DOMAINS.map(group => [logicalGroupId("governance", group.key), group]));
const DOMAIN_GROUPS = new Map(CI_DOMAINS.map(group => [logicalGroupId("domain", group.key), group]));

function asObject(value) {
  return value && typeof value === "object" && !Array.isArray(value) ? value : null;
}

function count(value) {
  if (!Number.isSafeInteger(value) || value < 0) return null;
  return value;
}

function sha256Json(value) {
  return crypto.createHash("sha256").update(JSON.stringify(value), "utf8").digest("hex");
}

function isDigest(value) {
  return typeof value === "string" && /^[0-9a-f]{64}$/u.test(value);
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

function groupDefinition(groupId) {
  return HOST_GROUPS.get(groupId)
    || FRONTEND_GROUPS.get(groupId)
    || SYSTEM_GROUPS.get(groupId)
    || GOVERNANCE_GROUPS.get(groupId)
    || DOMAIN_GROUPS.get(groupId)
    || null;
}

function normalizedPlanPath(value) {
  if (typeof value !== "string") return "";
  return value.trim().replaceAll("\\", "/").replace(/^\.\//u, "");
}

function canonicalTestGroupKeys(kind, plan) {
  const registry = kind === "host" ? HOST_TEST_AREAS : FRONTEND_TEST_GROUPS;
  const domainKey = kind;
  if (!plan?.domains?.[domainKey]?.affected) return [];
  const allKeys = registry.map(group => group.key);
  if (plan.failOpen) return allKeys;
  const relevant = (Array.isArray(plan.changedFiles) ? plan.changedFiles : [])
    .map(normalizedPlanPath)
    .filter(file => file && (kind === "host"
      ? file.startsWith("src/") || file.startsWith("tests/NexusPipeline.Tests/")
      : file.startsWith("frontend/")));
  if (relevant.length === 0) return allKeys;
  const selected = new Set();
  for (const file of relevant) {
    const hits = registry.filter(group => [
      ...(group.paths || []),
      ...(group.testPaths || []).map(pattern => kind === "host" ? `tests/NexusPipeline.Tests/${pattern}` : pattern),
    ].some(pattern => globToRegExp(pattern).test(file)));
    if (hits.length === 0) return allKeys;
    for (const group of hits) selected.add(group.key);
  }
  if (selected.size === 0) return allKeys;
  for (const key of selected) {
    for (const consumer of TEST_DOMAIN_CONSUMERS[kind]?.[key] || []) selected.add(consumer);
  }
  return allKeys.filter(key => selected.has(key));
}

function expectedSelectedGroups(plan) {
  const expected = [];
  const add = (kind, group, physicalJobId) => {
    if (!group) return;
    const contractJob = plan.all ? "full-regression" : physicalJobId;
    const contract = CI_JOB_CONTRACTS[contractJob];
    expected.push({
      groupId: logicalGroupId(kind, group.key),
      kind,
      key: group.key,
      physicalJobId: contractJob,
      mode: contract?.mode || "",
      requiredIntegrity: contract?.requiredIntegrity || "",
      requiredChecks: [...(contract?.requiredChecks || [])],
      testSelectionIdentity: testSelectionIdentity(kind, group),
      expectedTests: expectedTestSelectors(kind, group),
      expectedFiles: expectedTestFiles(kind, group),
      exclusions: plannedExclusions(kind, group.key, contract?.mode || ""),
    });
  };

  if (plan.all) {
    HOST_TEST_AREAS.forEach(group => add("host", group, "host-core"));
    FRONTEND_TEST_GROUPS.forEach(group => add("frontend", group, "frontend-unit"));
    for (const key of ["docs", "plugin", "ui"]) {
      const domain = CI_DOMAINS.find(item => item.key === key);
      if (domain) add("domain", { key, paths: domain.paths, testPaths: domain.testPaths || [] }, domain.jobs[0]);
    }
    SYSTEM_TEST_GROUPS.forEach(group => add("system", group, ""));
    GOVERNANCE_DOMAINS.forEach(group => add("governance", group, "docs-i18n"));
    return expected;
  }

  if (plan.domains?.host?.affected) {
    for (const key of plan.testGroups?.host || []) {
      const group = HOST_TEST_AREAS.find(item => item.key === key);
      if (group) add("host", group, "host-core");
    }
  }
  if (plan.domains?.frontend?.affected) {
    for (const key of plan.testGroups?.frontend || []) {
      const group = FRONTEND_TEST_GROUPS.find(item => item.key === key);
      if (group) add("frontend", group, "frontend-unit");
    }
  }
  if (plan.domains?.plugin?.affected) {
    if (!plan.domains?.host?.affected) {
      for (const key of PLUGIN_CONTRACT_FALLBACKS.host) {
        const group = HOST_TEST_AREAS.find(item => item.key === key);
        if (group) add("host", group, "plugin-contract");
      }
    }
    if (!plan.domains?.frontend?.affected) {
      for (const key of PLUGIN_CONTRACT_FALLBACKS.frontend) {
        const group = FRONTEND_TEST_GROUPS.find(item => item.key === key);
        if (group) add("frontend", group, "plugin-contract");
      }
    }
  }
  const addDomain = (key, physicalJobId) => {
    if (!plan.domains?.[key]?.affected) return;
    const domain = CI_DOMAINS.find(item => item.key === key);
    if (!domain) return;
    add("domain", { key, paths: domain.paths, testPaths: domain.testPaths || [] }, physicalJobId);
  };
  addDomain("docs", "docs-i18n");
  addDomain("plugin", "plugin-contract");
  addDomain("ui", "ui-smoke");
  for (const group of SYSTEM_TEST_GROUPS) {
    if (!plan.domains?.[group.ciDomain]?.affected) continue;
    const job = CI_DOMAINS.find(domain => domain.key === group.ciDomain)?.jobs?.[0] || "";
    add("system", group, job);
  }
  return expected;
}

function validateSelectedGroup(group, plan, errors) {
  const value = asObject(group);
  if (!value) {
    errors.push("execution-plan selectedGroups 含有非对象项");
    return;
  }
  const groupId = String(value.groupId || "");
  if (!groupId || !groupDefinition(groupId)) {
    errors.push(`selectedGroups 包含未知逻辑组：${groupId || "空"}`);
    return;
  }
  if (typeof value.kind !== "string" || typeof value.key !== "string" || value.groupId !== logicalGroupId(value.kind, value.key)) {
    errors.push(`逻辑组身份不一致：${groupId || "空"}`);
  }
  if (!JOB_KEYS.has(value.physicalJobId)) errors.push(`逻辑组 ${groupId} 绑定未知物理 Job：${value.physicalJobId || "空"}`);
  const contract = CI_JOB_CONTRACTS[value.physicalJobId];
  if (contract && value.mode !== contract.mode) errors.push(`逻辑组 ${groupId} 的 mode 与 Job 不符`);
  if (contract && value.requiredIntegrity !== contract.requiredIntegrity) errors.push(`逻辑组 ${groupId} 的 requiredIntegrity 与 Job 不符`);
  if (contract && JSON.stringify(value.requiredChecks) !== JSON.stringify(contract.requiredChecks || [])) errors.push(`逻辑组 ${groupId} 的 requiredChecks 与 Job 不符`);
  if (!VALID_MODES.has(value.mode)) errors.push(`逻辑组 ${groupId} 的 mode 无效：${value.mode || "空"}`);
  if (typeof value.requiredIntegrity !== "string" || !value.requiredIntegrity) errors.push(`逻辑组 ${groupId} 缺少 requiredIntegrity`);
  if (!isDigest(value.testSelectionIdentity)) errors.push(`逻辑组 ${groupId} 缺少有效 testSelectionIdentity`);
  if (!Array.isArray(value.expectedTests) || value.expectedTests.length === 0) errors.push(`逻辑组 ${groupId} 缺少 expectedTests`);
  if (!Array.isArray(value.expectedFiles) || value.expectedFiles.length === 0) errors.push(`逻辑组 ${groupId} 缺少 expectedFiles`);
  if (!Array.isArray(value.exclusions)) errors.push(`逻辑组 ${groupId} 缺少 exclusions`);

  const definition = groupDefinition(groupId);
  if (definition && value.testSelectionIdentity !== testSelectionIdentity(value.kind, definition)) {
    errors.push(`逻辑组 ${groupId} 的 testSelectionIdentity 与注册表不符`);
  }
  if (value.kind === "domain" && !DOMAIN_KEYS.has(value.key)) errors.push(`selectedGroups 引用了未知 domain：${value.key}`);
  const expected = expectedSelectedGroups(plan).find(item => item.groupId === groupId);
  if (expected) {
    for (const key of ["physicalJobId", "mode", "requiredIntegrity", "requiredChecks", "testSelectionIdentity", "expectedTests", "expectedFiles", "exclusions"]) {
      const matches = Array.isArray(value[key]) || Array.isArray(expected[key])
        ? JSON.stringify(value[key]) === JSON.stringify(expected[key])
        : value[key] === expected[key];
      if (!matches) errors.push(`逻辑组 ${groupId} 的 ${key} 与注册表不符`);
    }
  }
}

export function validateExecutionPlan(plan, { expectedHeadSha = "" } = {}) {
  const errors = [];
  const value = asObject(plan);
  if (!value) return { ok: false, errors: ["execution-plan 不是对象"] };
  if (value.schemaVersion !== CI_EXECUTION_PLAN_SCHEMA_VERSION) errors.push("execution-plan schemaVersion 不受支持");
  if (typeof value.all !== "boolean") errors.push("execution-plan all 必须是布尔值");
  if (typeof value.base !== "string" || typeof value.head !== "string") errors.push("execution-plan 缺少 base/head");
  if (expectedHeadSha && value.head !== expectedHeadSha) errors.push(`execution-plan head SHA 不匹配：${value.head || "空"} != ${expectedHeadSha}`);
  if (typeof value.failOpen !== "boolean") errors.push("execution-plan failOpen 必须是布尔值");
  if (!Array.isArray(value.changedFiles)) errors.push("execution-plan changedFiles 必须是数组");
  if (!Array.isArray(value.unknown)) errors.push("execution-plan unknown 必须是数组");
  if (!Array.isArray(value.shared)) errors.push("execution-plan shared 必须是数组");
  if (typeof value.counterpartRepository !== "string" || !value.counterpartRepository) errors.push("execution-plan 缺少 counterpartRepository");
  if (typeof value.counterpartSha !== "string" || !/^[0-9a-f]{40}$/u.test(value.counterpartSha)) errors.push("execution-plan 缺少有效 counterpartSha");

  const build = asObject(value.buildInputs);
  if (!build) {
    errors.push("execution-plan 缺少 buildInputs");
  } else {
    for (const key of ["sourceRepository", "sourceSha", "sourceDigest", "targetFramework", "runtimeIdentifier", "frontendResourceSource", "applicationManifest", "testHostManifest", "counterpartRepository", "counterpartSha"]) {
      if (typeof build[key] !== "string" || !build[key]) errors.push(`buildInputs 缺少 ${key}`);
    }
    if (!isDigest(build.sourceDigest)) errors.push("buildInputs sourceDigest 无效");
    if (!asObject(build.toolchain)) errors.push("buildInputs 缺少 toolchain");
    const copy = { ...build };
    delete copy.buildInputsDigest;
    if (!isDigest(build.buildInputsDigest) || build.buildInputsDigest !== sha256Json(copy)) errors.push("buildInputs buildInputsDigest 校验失败");
    if (build.counterpartRepository !== value.counterpartRepository || build.counterpartSha !== value.counterpartSha) errors.push("buildInputs 与双仓库候选不一致");
  }

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
      if (!Array.isArray(item.jobs) || item.jobs.length === 0 || item.jobs.some(job => typeof job !== "string" || !JOB_KEYS.has(job))) {
        errors.push(`域 ${domain.key} 缺少有效 jobs`);
      }
      const expectedJobs = value.all ? ["full-regression"] : domain.jobs;
      if (JSON.stringify(item.jobs) !== JSON.stringify(expectedJobs)) errors.push(`域 ${domain.key} 的 jobs 与注册表不符`);
      if (!Array.isArray(item.files) || !Array.isArray(item.reasons)) errors.push(`域 ${domain.key} 缺少 files/reasons`);
    }
    for (const key of Object.keys(domains)) if (!DOMAIN_KEYS.has(key)) errors.push(`execution-plan 包含未知域：${key}`);
  }

  const testGroups = asObject(value.testGroups);
  if (!testGroups) {
    errors.push("execution-plan 缺少 testGroups");
  } else {
    for (const [kind, registry] of [["host", HOST_TEST_AREAS], ["frontend", FRONTEND_TEST_GROUPS]]) {
      if (!Array.isArray(testGroups[kind])) {
        errors.push(`execution-plan testGroups.${kind} 必须是数组`);
        continue;
      }
      const known = new Set(registry.map(group => group.key));
      const duplicates = testGroups[kind].filter((key, index) => testGroups[kind].indexOf(key) !== index);
      if (duplicates.length) errors.push(`testGroups.${kind} 存在重复组：${[...new Set(duplicates)].join(", ")}`);
      for (const key of testGroups[kind]) if (!known.has(key)) errors.push(`testGroups.${kind} 包含未知组：${key}`);
    }
    for (const kind of ["host", "frontend"]) {
      if (!Array.isArray(testGroups[kind])) continue;
      const expected = canonicalTestGroupKeys(kind, value);
      const actual = [...new Set(testGroups[kind])];
      if (JSON.stringify(actual) !== JSON.stringify(expected)) {
        errors.push(`testGroups.${kind} 与影响域选择不一致`);
      }
    }
  }

  if (!Array.isArray(value.selectedGroups) || value.selectedGroups.length === 0) {
    errors.push("execution-plan 缺少 selectedGroups");
  } else {
    const ids = new Set();
    for (const group of value.selectedGroups) {
      validateSelectedGroup(group, value, errors);
      if (group?.groupId) {
        if (ids.has(group.groupId)) errors.push(`selectedGroups 存在重复逻辑组：${group.groupId}`);
        ids.add(group.groupId);
      }
    }
    const expectedIds = expectedSelectedGroups(value).map(group => group.groupId).sort();
    const actualIds = [...ids].sort();
    if (JSON.stringify(actualIds) !== JSON.stringify(expectedIds)) {
      const missing = expectedIds.filter(id => !ids.has(id));
      const extra = actualIds.filter(id => !expectedIds.includes(id));
      if (missing.length) errors.push(`selectedGroups 缺少逻辑组：${missing.join(", ")}`);
      if (extra.length) errors.push(`selectedGroups 包含未计划逻辑组：${extra.join(", ")}`);
    }
    const expectedById = new Map(expectedSelectedGroups(value).map(group => [group.groupId, group]));
    for (const group of value.selectedGroups) {
      const expected = expectedById.get(group?.groupId);
      if (!expected) continue;
      for (const key of ["kind", "key", "physicalJobId", "mode", "requiredIntegrity", "requiredChecks", "testSelectionIdentity", "expectedTests", "expectedFiles", "exclusions"]) {
        const matches = Array.isArray(group[key]) || Array.isArray(expected[key])
          ? JSON.stringify(group[key]) === JSON.stringify(expected[key])
          : group[key] === expected[key];
        if (!matches) errors.push(`逻辑组 ${group.groupId} 的 ${key} 与计划选择不一致`);
      }
    }
  }
  if (!isDigest(value.planDigest) || value.planDigest !== executionPlanDigest(value)) errors.push("execution-plan planDigest 校验失败");
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

function integritySatisfies(required, actual) {
  if (required === "none") return true;
  const value = String(actual || "").toLowerCase();
  return required === "high-or-system" && ["high", "system", "high-or-system"].includes(value);
}

function checkChecks(jobId, item, issues, requiredChecks = [], delegatedChecks = new Set()) {
  const checks = Array.isArray(item.checks)
    ? item.checks
    : asObject(item.checks)
      ? Object.entries(item.checks).map(([checkId, value]) => ({ checkId, ...(asObject(value) || { status: value }) }))
      : [];
  const statuses = new Map();
  for (const check of checks) {
    const status = String(check?.status || check?.result || "").toLowerCase();
    const checkId = String(check?.checkId || check?.id || "");
    if (!checkId) {
      issues.push(`${jobId}: check 缺少身份`);
      continue;
    }
    if (statuses.has(checkId)) issues.push(`${jobId}: check ${checkId} 重复`);
    statuses.set(checkId, status);
    if (!status) issues.push(`${jobId}: check ${checkId} 缺少状态`);
    else if (status !== "success" && status !== "passed" && status !== "pass") issues.push(`${jobId}: check ${checkId} 未通过（${status}）`);
  }
  for (const checkId of requiredChecks) {
    const status = statuses.get(checkId);
    if (!status) {
      if (!delegatedChecks.has(checkId)) issues.push(`${jobId}: 缺少必需 check ${checkId}`);
    } else if (!["success", "passed", "pass"].includes(status)) {
      issues.push(`${jobId}: 必需 check ${checkId} 未通过（${status}）`);
    }
  }
  if (item.buildStatus !== undefined && String(item.buildStatus).toLowerCase() !== "success") issues.push(`${jobId}: buildStatus 未通过`);
  if (item.typecheckStatus !== undefined && String(item.typecheckStatus).toLowerCase() !== "success") issues.push(`${jobId}: typecheckStatus 未通过`);
}

function hasSuccessfulCheck(value, checkId) {
  const item = asObject(value) || {};
  const checks = Array.isArray(item.checks)
    ? item.checks
    : asObject(item.checks)
      ? Object.entries(item.checks).map(([id, status]) => ({ checkId: id, ...(asObject(status) || { status }) }))
      : [];
  return checks.some(check => {
    const id = String(check?.checkId || check?.id || "");
    const status = String(check?.status || check?.result || "").toLowerCase();
    return id === checkId && ["success", "passed", "pass"].includes(status);
  });
}

/**
 * Gate D 在宿主与前端 Gate 都已选中时复用两者的 build/tests 结果；
 * 只有两个 provider Job 均已计划且成功时，才允许把缺失项作为已委托检查。
 */
function delegatedChecksForJob(jobId, expectedGroups, expectedByJob, records) {
  if (jobId !== "plugin-contract" || !expectedGroups.some(group => group.groupId === "domain:plugin")) return new Set();
  const providerJobIds = ["host-core", "frontend-unit"];
  if (!providerJobIds.every(providerId => (expectedByJob.get(providerId) || []).length > 0)) return new Set();
  const providers = providerJobIds.map(providerId => records.get(providerId));
  if (!providers.every(provider => (
    jobResult(provider) === "success"
    && hasSuccessfulCheck(provider, "build")
    && hasSuccessfulCheck(provider, "tests")
  ))) return new Set();
  return new Set(["build", "tests"]);
}

function expectedJobDomainId(jobId, groups) {
  if (jobId === "full-regression") return "full-regression";
  const ids = new Set(groups.map(group => {
    if (group.kind === "system") return groupDefinition(group.groupId)?.ciDomain || "";
    if (group.kind === "governance") return "docs";
    if (group.kind === "domain") return group.key;
    return group.kind;
  }).filter(Boolean));
  return ids.size === 1 ? [...ids][0] : "";
}

function checkJobIdentity(jobId, value, { plan, expectedHeadSha, expectedRepositorySha, expectedPlanDigest, expectedGroups, delegatedChecks = new Set() }) {
  const issues = [];
  const result = jobResult(value);
  if (!VALID_RESULTS.has(result)) issues.push(`${jobId}: 缺少或无效 result`);
  if (result !== "success") issues.push(`${jobId}: selected job 结果为 ${result || "missing"}`);
  const item = asObject(value) || {};
  if (item.schemaVersion !== CI_SUMMARY_SCHEMA_VERSION) issues.push(`${jobId}: manifest schemaVersion 不受支持`);
  if (item.jobId !== jobId) issues.push(`${jobId}: manifest jobId 不匹配`);
  if (expectedHeadSha && item.headSha !== expectedHeadSha) issues.push(`${jobId}: headSha 不匹配`);
  if (expectedRepositorySha && item.repositorySha !== expectedRepositorySha) issues.push(`${jobId}: repositorySha 不匹配`);
  if (expectedPlanDigest && item.planDigest !== expectedPlanDigest) issues.push(`${jobId}: planDigest 不匹配`);
  if (!isDigest(item.buildFingerprint)) issues.push(`${jobId}: 缺少有效 buildFingerprint`);
  if (!manifestPresent(item)) issues.push(`${jobId}: 缺少结果 manifest`);
  if (item.sourceDigest !== plan.buildInputs.sourceDigest) issues.push(`${jobId}: sourceDigest 不匹配`);
  if (item.buildInputsDigest !== plan.buildInputs.buildInputsDigest) issues.push(`${jobId}: buildInputsDigest 不匹配`);
  if (item.counterpartRepository !== plan.counterpartRepository) issues.push(`${jobId}: counterpartRepository 不匹配`);
  if (item.counterpartSha !== plan.counterpartSha) issues.push(`${jobId}: counterpartSha 不匹配`);
  const artifactManifest = asObject(item.artifactManifest);
  if (!artifactManifest) {
    issues.push(`${jobId}: 缺少 artifactManifest`);
  } else {
    issues.push(...validateArtifactManifestShape(artifactManifest).map(message => `${jobId}: ${message}`));
    const binaryRequired = expectedGroups.some(group => group.mode === "admin" || ["host", "frontend"].includes(group.kind));
    if (binaryRequired && artifactManifest.present !== true) issues.push(`${jobId}: 选定作业缺少真实产物`);
    if (jobId === "docs-i18n" && artifactManifest.present === true) issues.push(`${jobId}: 文档作业不应伪造二进制产物`);
    if (artifactManifest.present === true) {
      if (item.artifactDigest !== artifactManifest.artifactSetDigest) issues.push(`${jobId}: artifactDigest 未绑定实际产物清单`);
      if (artifactManifest.mode !== item.mode) issues.push(`${jobId}: artifactManifest mode 与 Job 不匹配`);
    } else if (item.artifactDigest !== null) {
      issues.push(`${jobId}: 无产物作业的 artifactDigest 必须为 null`);
    }
  }
  const expectedDomainId = expectedJobDomainId(jobId, expectedGroups);
  if (expectedDomainId && item.domainId !== expectedDomainId) issues.push(`${jobId}: domainId 不匹配`);
  const requiredChecks = [...new Set(expectedGroups.flatMap(group => group.requiredChecks || []))];
  checkChecks(jobId, item, issues, requiredChecks, delegatedChecks);

  const expectedModes = new Set(expectedGroups.map(group => group.mode));
  if (expectedModes.size === 1 && item.mode !== [...expectedModes][0]) issues.push(`${jobId}: mode 不匹配`);
  if (expectedGroups.some(group => group.requiredIntegrity === "high-or-system") && !integritySatisfies("high-or-system", item.integrityLevel || item.integrity)) issues.push(`${jobId}: 缺少 High/System Integrity 证据`);
  if (expectedGroups.some(group => group.mode === "admin") && (item.testHost === true || item.mode === "test-host")) issues.push(`${jobId}: Test Host 不能替代 admin 生产模式`);
  const testCount = count(item.testCount);
  const passed = count(item.passed ?? item.passedCount);
  const failed = count(item.failed ?? item.failedCount);
  const skipped = count(item.skipped ?? item.skippedCount);
  if (testCount === null || testCount === 0) issues.push(`${jobId}: testCount 必须是正整数`);
  else if (passed === null || failed === null || skipped === null) issues.push(`${jobId}: 缺少 passed/failed/skipped 计数`);
  else {
    if (passed + failed + skipped !== testCount) issues.push(`${jobId}: 测试计数不守恒`);
    if (passed === 0) issues.push(`${jobId}: 没有通过的测试`);
    if (failed > 0) issues.push(`${jobId}: 存在失败测试`);
  }
  if (count(item.exitCode) !== 0) issues.push(`${jobId}: exitCode 不是 0`);
  return issues;
}

function groupRecords(value) {
  const item = asObject(value) || {};
  if (Array.isArray(item.groups)) return item.groups.map(group => asObject(group) || {});
  if (Array.isArray(item.executions)) return item.executions.map(group => asObject(group) || {});
  return [];
}

function exclusionIds(value) {
  const entries = Array.isArray(value) ? value : [];
  return entries.map(item => typeof item === "string" ? item : String(item?.testId || item?.id || "")).filter(Boolean).sort();
}

function normalizeSelectedPath(value) {
  if (typeof value !== "string") return "";
  const normalized = value.trim().replaceAll("\\", "/").replace(/^\.\//u, "");
  if (!normalized || normalized.startsWith("/") || /^[A-Za-z]:\//u.test(normalized)) return "";
  if (normalized.split("/").some(segment => segment === "..")) return "";
  return normalized;
}

function selectionMatchesExpected(actualPaths, expectedPaths) {
  const normalized = (Array.isArray(actualPaths) ? actualPaths : []).map(normalizeSelectedPath);
  const expected = (Array.isArray(expectedPaths) ? expectedPaths : []).map(normalizeSelectedPath);
  const invalid = normalized.some(value => !value);
  const expectedSet = new Set(expected);
  const actualSet = new Set(normalized.filter(Boolean));
  return {
    normalized,
    invalid,
    duplicate: normalized.length !== actualSet.size,
    missing: expected.filter(value => value && !actualSet.has(value)),
    unexpected: normalized.filter(value => value && !expectedSet.has(value)),
  };
}

function observedSkipIdentities(record) {
  return (Array.isArray(record.observedCases) ? record.observedCases : [])
    .filter(item => ["skipped-by-engine", "required-in-other-mode", "excluded-before-run"].includes(String(item?.status || "")))
    .map(item => String(item?.id || item?.testId || "").trim())
    .filter(Boolean);
}

function checkSelectedGroup(jobId, record, expected, manifest) {
  const issues = [];
  const groupId = expected.groupId;
  if (record.groupId !== groupId) issues.push(`${jobId}/${groupId}: groupId 不匹配`);
  if (record.physicalJobId !== expected.physicalJobId) issues.push(`${jobId}/${groupId}: physicalJobId 不匹配`);
  if (record.mode !== expected.mode) issues.push(`${jobId}/${groupId}: mode 不匹配`);
  if (expected.kind === "system") {
    if (record.observedMode !== expected.mode) issues.push(`${jobId}/${groupId}: 实际执行模式不匹配`);
    if (record.timeScale === undefined || record.timeScale === null || String(record.timeScale).trim() === "") {
      issues.push(`${jobId}/${groupId}: 缺少实际 timeScale 记录`);
    }
  }
  if (expected.requiredIntegrity !== "none" && !integritySatisfies(expected.requiredIntegrity, record.integrityLevel || manifest.integrityLevel || manifest.integrity)) {
    issues.push(`${jobId}/${groupId}: 完整性级别不满足要求`);
  }
  if (record.testSelectionIdentity !== expected.testSelectionIdentity) issues.push(`${jobId}/${groupId}: testSelectionIdentity 不匹配`);
  const selection = asObject(record.selection);
  const invokedFiles = Array.isArray(record.invokedFiles) ? record.invokedFiles : record.selectedTests;
  const observedFiles = record.observedFiles;
  const invokedResult = selectionMatchesExpected(invokedFiles, expected.expectedFiles);
  const observedResult = selectionMatchesExpected(observedFiles, expected.expectedFiles);
  if (!Array.isArray(invokedFiles) || invokedFiles.length === 0) {
    issues.push(`${jobId}/${groupId}: 缺少实际调用文件记录`);
  } else {
    if (invokedResult.invalid) issues.push(`${jobId}/${groupId}: 实际调用文件包含非法路径`);
    if (invokedResult.duplicate) issues.push(`${jobId}/${groupId}: 实际调用文件存在重复`);
    if (invokedResult.missing.length) issues.push(`${jobId}/${groupId}: 实际调用文件缺失：${invokedResult.missing.join(", ")}`);
    if (invokedResult.unexpected.length) issues.push(`${jobId}/${groupId}: 实际调用文件超出计划：${invokedResult.unexpected.join(", ")}`);
  }
  if (!Array.isArray(observedFiles) || observedFiles.length === 0) {
    issues.push(`${jobId}/${groupId}: 缺少测试引擎实际观测文件记录`);
  } else {
    if (observedResult.invalid) issues.push(`${jobId}/${groupId}: 实际观测文件包含非法路径`);
    if (observedResult.duplicate) issues.push(`${jobId}/${groupId}: 实际观测文件存在重复`);
    if (observedResult.missing.length) issues.push(`${jobId}/${groupId}: 实际观测文件缺失：${observedResult.missing.join(", ")}`);
    if (observedResult.unexpected.length) issues.push(`${jobId}/${groupId}: 实际观测文件超出计划：${observedResult.unexpected.join(", ")}`);
  }
  if (!selection || !Array.isArray(selection.plannedTests) || !Array.isArray(selection.actualTests)) {
    issues.push(`${jobId}/${groupId}: 缺少结构化测试选择记录`);
  } else {
    if (JSON.stringify(selection.plannedTests) !== JSON.stringify(expected.expectedTests)) {
      issues.push(`${jobId}/${groupId}: selection.plannedTests 与计划不一致`);
    }
    if (!Array.isArray(invokedFiles) || JSON.stringify(selection.actualTests) !== JSON.stringify(invokedFiles)) {
      issues.push(`${jobId}/${groupId}: selection.actualTests 与实际选择不一致`);
    }
    if (!Array.isArray(selection.expectedFiles) || JSON.stringify(selection.expectedFiles) !== JSON.stringify(expected.expectedFiles)) {
      issues.push(`${jobId}/${groupId}: selection.expectedFiles 与计划不一致`);
    }
    if (!Array.isArray(selection.invokedFiles) || JSON.stringify(selection.invokedFiles) !== JSON.stringify(invokedFiles)) {
      issues.push(`${jobId}/${groupId}: selection.invokedFiles 与实际调用不一致`);
    }
    if (!Array.isArray(selection.observedFiles) || JSON.stringify(selection.observedFiles) !== JSON.stringify(observedFiles)) {
      issues.push(`${jobId}/${groupId}: selection.observedFiles 与实际观测不一致`);
    }
  }
  if (!Array.isArray(record.plannedTests) || JSON.stringify(record.plannedTests) !== JSON.stringify(expected.expectedTests)) {
    issues.push(`${jobId}/${groupId}: plannedTests 与计划不一致`);
  }
  const result = jobResult(record);
  if (result !== "success") issues.push(`${jobId}/${groupId}: 逻辑组结果为 ${result || "missing"}`);

  const testCount = count(record.testCount);
  const passed = count(record.passed ?? record.passedCount);
  const failed = count(record.failed ?? record.failedCount);
  const skipped = count(record.skipped ?? record.skippedCount);
  if (testCount === null || testCount === 0) issues.push(`${jobId}/${groupId}: testCount 必须是正整数`);
  else if (passed === null || failed === null || skipped === null || passed + failed + skipped !== testCount) issues.push(`${jobId}/${groupId}: 测试计数不守恒`);
  else {
    if (passed === 0) issues.push(`${jobId}/${groupId}: 没有通过的测试`);
    if (failed > 0) issues.push(`${jobId}/${groupId}: 存在失败测试`);
  }
  if (record.nativeTotal !== undefined && record.nativeTotal !== testCount) issues.push(`${jobId}/${groupId}: native total 与规范化计数不一致`);
  if (record.exitCode !== 0) issues.push(`${jobId}/${groupId}: exitCode 不是 0`);

  const declaredExclusions = exclusionIds(record.exclusions || record.excludedTests);
  const expectedExclusions = exclusionIds(expected.exclusions);
  const observedSkips = observedSkipIdentities(record);
  const exclusionObservations = new Set((Array.isArray(record.exclusionObservations) ? record.exclusionObservations : [])
    .map(item => String(item?.testId || item?.id || "").trim())
    .filter(Boolean));
  const missingExclusions = expectedExclusions.filter(id => !declaredExclusions.includes(id));
  if (missingExclusions.length) issues.push(`${jobId}/${groupId}: 缺少计划声明的合法排除：${missingExclusions.join(", ")}`);
  if (skipped > 0 && observedSkips.length === 0) issues.push(`${jobId}/${groupId}: 测试引擎 skip 缺少实际身份`);
  if (skipped > 0 && declaredExclusions.length === 0) issues.push(`${jobId}/${groupId}: 存在未按身份声明的 skip`);
  for (const id of expectedExclusions) {
    if (!observedSkips.includes(id) && !exclusionObservations.has(id)) {
      issues.push(`${jobId}/${groupId}: 计划排除未在实际结果中闭合：${id}`);
    }
  }
  const unexpectedObservedSkips = observedSkips.filter(id => !expectedExclusions.includes(id));
  if (unexpectedObservedSkips.length) issues.push(`${jobId}/${groupId}: 实际结果出现未计划 skip：${unexpectedObservedSkips.join(", ")}`);
  const unexpectedExclusions = declaredExclusions.filter(id => !expectedExclusions.includes(id));
  if (unexpectedExclusions.length) issues.push(`${jobId}/${groupId}: 出现未计划排除：${unexpectedExclusions.join(", ")}`);
  return issues;
}

function normalizeGroupMap(manifest) {
  const map = new Map();
  for (const record of groupRecords(manifest)) {
    const groupId = String(record.groupId || "");
    if (!groupId) continue;
    const list = map.get(groupId) || [];
    list.push(record);
    map.set(groupId, list);
  }
  return map;
}

/** @param {{plan: object, selector?: object, jobs: object|Array, expectedHeadSha?: string, expectedRepositorySha?: string}} input */
export function evaluateRequiredSummary(input) {
  const value = asObject(input) || {};
  const plan = value.plan;
  const expectedHeadSha = String(value.expectedHeadSha || "");
  const planValidation = validateExecutionPlan(plan, { expectedHeadSha });
  const issues = [...planValidation.errors];
  const selector = asObject(value.selector);
  const planDigest = asObject(plan)?.planDigest || "";

  if (!selector) issues.push("缺少 changes selector 结果");
  if (selector) {
    if (jobResult(selector) !== "success") issues.push(`changes selector 结果为 ${jobResult(selector) || "missing"}`);
    if (selector.planValid !== true) issues.push("changes selector 报告计划无效");
    if (expectedHeadSha && selector.headSha !== expectedHeadSha) issues.push("changes selector headSha 不匹配");
    if (planDigest && selector.planDigest !== planDigest) issues.push("changes selector planDigest 不匹配");
  }

  const expectedGroups = planValidation.ok ? plan.selectedGroups : [];
  const expectedByJob = new Map();
  for (const group of expectedGroups) {
    const list = expectedByJob.get(group.physicalJobId) || [];
    list.push(group);
    expectedByJob.set(group.physicalJobId, list);
  }
  const selectedJobs = [...expectedByJob.keys()].sort();
  const entries = normalizeJobs(value.jobs);
  const records = new Map();
  const duplicates = [];
  for (const [jobId, item] of entries) {
    if (records.has(jobId)) duplicates.push(jobId);
    records.set(jobId, item);
  }
  if (duplicates.length) issues.push(`结果中存在重复 job：${[...new Set(duplicates)].join(", ")}`);

  const groupResults = [];
  const missingGroups = [];
  const invalidGroups = [];
  if (planValidation.ok) {
    for (const jobId of selectedJobs) {
      const item = records.get(jobId);
      if (!item) {
        issues.push(`${jobId}: selected job 缺失`);
        for (const group of expectedByJob.get(jobId) || []) {
          missingGroups.push(group.groupId);
          groupResults.push({ groupId: group.groupId, physicalJobId: jobId, status: "missing" });
        }
        continue;
      }
      const expectedForJob = expectedByJob.get(jobId) || [];
      const delegatedChecks = delegatedChecksForJob(jobId, expectedForJob, expectedByJob, records);
      issues.push(...checkJobIdentity(jobId, item, {
        plan,
        expectedHeadSha,
        expectedRepositorySha: String(value.expectedRepositorySha || ""),
        expectedPlanDigest: planDigest,
        expectedGroups: expectedForJob,
        delegatedChecks,
      }));
      const groupMap = normalizeGroupMap(item);
      const expectedIds = new Set(expectedForJob.map(group => group.groupId));
      for (const group of expectedForJob) {
        const candidates = groupMap.get(group.groupId) || [];
        if (candidates.length === 0) {
          const message = `${jobId}/${group.groupId}: 缺少逻辑组执行记录`;
          issues.push(message);
          missingGroups.push(group.groupId);
          groupResults.push({ groupId: group.groupId, physicalJobId: jobId, status: "missing", issues: [message] });
        } else if (candidates.length > 1) {
          const message = `${jobId}/${group.groupId}: 逻辑组存在重复执行记录`;
          issues.push(message);
          invalidGroups.push(group.groupId);
          groupResults.push({ groupId: group.groupId, physicalJobId: jobId, status: "invalid", issues: [message] });
        } else {
          const groupIssues = checkSelectedGroup(jobId, candidates[0], group, item);
          issues.push(...groupIssues);
          const status = groupIssues.length ? "invalid" : "success";
          if (status !== "success") invalidGroups.push(group.groupId);
          groupResults.push({ groupId: group.groupId, physicalJobId: jobId, status, issues: groupIssues });
        }
      }
      for (const groupId of groupMap.keys()) {
        if (!expectedIds.has(groupId)) {
          issues.push(`${jobId}/${groupId}: 结果包含未计划逻辑组`);
          invalidGroups.push(groupId);
        }
      }
    }
  }

  const duplicateExecutions = [];
  for (const [jobId, item] of records) {
    if (["failure", "cancelled"].includes(jobResult(item)) && !expectedByJob.has(jobId)) issues.push(`${jobId}: 未选作业失败或取消`);
    if (!expectedByJob.has(jobId) && jobResult(item) !== "skipped") duplicateExecutions.push(jobId);
  }
  const unselectedJobs = [...JOB_KEYS].filter(jobId => !expectedByJob.has(jobId)).sort();
  const unselectedJobResults = unselectedJobs.map(jobId => ({
    jobId,
    result: jobResult(records.get(jobId)) || "missing",
  }));
  return {
    schemaVersion: CI_SUMMARY_SCHEMA_VERSION,
    ok: issues.length === 0,
    issues,
    selectedJobs,
    unselectedJobs,
    unselectedJobResults,
    missingJobs: selectedJobs.filter(jobId => !records.has(jobId)),
    duplicateExecutions: duplicateExecutions.sort(),
    missingGroups: [...new Set(missingGroups)].sort(),
    invalidGroups: [...new Set(invalidGroups)].sort(),
    excludedGroups: expectedGroups.filter(group => group.exclusions?.length > 0).map(group => group.groupId),
    groupResults,
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
