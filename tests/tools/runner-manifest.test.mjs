import test from "node:test";
import assert from "node:assert/strict";
import fs from "node:fs";
import os from "node:os";
import path from "node:path";
import { spawnSync } from "node:child_process";
import { fileURLToPath } from "node:url";
import { createExecutionPlan } from "../../tools/ci-changes.mjs";

const root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), "../..");
const defaultOfficialPluginsRoot = process.env.NEXUS_OFFICIAL_PLUGINS_ROOT
  || (fs.existsSync(path.join(root, "NexusPipeline-Plugins"))
    ? path.join(root, "NexusPipeline-Plugins")
    : path.resolve(root, "..", "NexusPipeline-Plugins"));

test("exit 2 的子探针不创建或改写父 runner 的 manifest 与报告目录", () => {
  const fixtureRoot = fs.mkdtempSync(path.join(os.tmpdir(), "nxp-runner-manifest-probe-"));
  const manifestPath = path.join(fixtureRoot, "parent-manifest.json");
  const reportRoot = path.join(fixtureRoot, "parent-reports");
  const childEnvironment = {
    ...process.env,
    NEXUS_CI_MANIFEST: manifestPath,
    NEXUS_CI_REPORT_DIR: reportRoot,
  };

  try {
    const result = spawnSync(process.execPath, [path.join(root, "tests", "run.mjs"), "default"], {
      cwd: root,
      encoding: "utf8",
      env: childEnvironment,
      windowsHide: true,
    });
    assert.equal(result.status, 2, result.stderr);
    assert.equal(fs.existsSync(manifestPath), false, "无效模式不能占用父 manifest 路径");
    assert.equal(fs.existsSync(reportRoot), false, "无效模式不能创建父报告目录");
  } finally {
    fs.rmSync(fixtureRoot, { recursive: true, force: true });
  }
});

test("全量 docs runner 同时记录 domain 与治理逻辑组且不重复治理执行", () => {
  const fixtureRoot = fs.mkdtempSync(path.join(os.tmpdir(), "nxp-runner-docs-plan-"));
  const manifestPath = path.join(fixtureRoot, "full-docs-manifest.json");
  const reportRoot = path.join(fixtureRoot, "reports");
  const plan = createExecutionPlan([], {
    all: true,
    base: "test-base",
    head: "test-head",
  });
  const environment = {
    ...process.env,
    NEXUS_CI_MANIFEST: manifestPath,
    NEXUS_CI_REPORT_DIR: reportRoot,
    NEXUS_CI_JOB_ID: "full-regression",
    NEXUS_CI_DOMAIN: "full-regression",
    NEXUS_CI_MODE: "admin",
    NEXUS_CI_INTEGRITY_LEVEL: "high",
    NEXUS_CI_HEAD_SHA: "test-head",
    NEXUS_CI_REPOSITORY_SHA: "test-head",
    NEXUS_CI_PLAN_DIGEST: plan.planDigest,
    NEXUS_CI_COUNTERPART_REPOSITORY: plan.counterpartRepository,
    NEXUS_CI_COUNTERPART_SHA: plan.counterpartSha,
    NEXUS_CI_SOURCE_DIGEST: plan.buildInputs.sourceDigest,
    NEXUS_CI_BUILD_INPUTS_DIGEST: plan.buildInputs.buildInputsDigest,
    NEXUS_CI_EXECUTION_PLAN: JSON.stringify(plan),
  };
  delete environment.NODE_TEST_CONTEXT;

  try {
    const result = spawnSync(process.execPath, [path.join(root, "tests", "run.mjs"), "docs"], {
      cwd: root,
      encoding: "utf8",
      env: environment,
      windowsHide: true,
    });
    assert.equal(result.status, 0, `${result.stdout}\n${result.stderr}`);
    const manifest = JSON.parse(fs.readFileSync(manifestPath, "utf8"));
    const groupIds = manifest.groups.map(group => group.groupId);
    assert.ok(groupIds.includes("domain:docs"), "全量 docs 必须记录 domain:docs");
    for (const key of ["docs-links-contracts", "i18n-functional", "test-policy"]) {
      assert.equal(groupIds.filter(groupId => groupId === `governance:${key}`).length, 1, `治理组 ${key} 应执行一次`);
    }
    for (const key of ["architecture-boundaries", "ci-tooling"]) {
      assert.equal(groupIds.includes(`governance:${key}`), false, `tooling 治理组 ${key} 不应在 docs 重复执行`);
    }
  } finally {
    fs.rmSync(fixtureRoot, { recursive: true, force: true });
  }
});

test("tooling 治理 runner 逐文件记录测试引擎实际观测身份", () => {
  const fixtureRoot = fs.mkdtempSync(path.join(os.tmpdir(), "nxp-runner-tooling-plan-"));
  const manifestPath = path.join(fixtureRoot, "tooling-manifest.json");
  const reportRoot = path.join(fixtureRoot, "reports");
  const sourcePlan = createExecutionPlan([], {
    all: true,
    base: "test-base",
    head: "test-head",
  });
  const architectureGroup = sourcePlan.selectedGroups.find(group => group.groupId === "governance:architecture-boundaries");
  assert.ok(architectureGroup);
  const plan = {
    ...sourcePlan,
    all: false,
    selectedGroups: [{ ...architectureGroup, physicalJobId: "docs-i18n", mode: "ci" }],
  };
  const environment = {
    ...process.env,
    NEXUS_CI_MANIFEST: manifestPath,
    NEXUS_CI_REPORT_DIR: reportRoot,
    NEXUS_CI_JOB_ID: "docs-i18n",
    NEXUS_CI_DOMAIN: "docs",
    NEXUS_CI_MODE: "ci",
    NEXUS_CI_HEAD_SHA: "test-head",
    NEXUS_CI_REPOSITORY_SHA: "test-head",
    NEXUS_CI_PLAN_DIGEST: plan.planDigest,
    NEXUS_CI_EXECUTION_PLAN: JSON.stringify(plan),
    NEXUS_CI_COUNTERPART_REPOSITORY: plan.counterpartRepository,
    NEXUS_CI_COUNTERPART_SHA: plan.counterpartSha,
    NEXUS_CI_SOURCE_DIGEST: plan.buildInputs.sourceDigest,
    NEXUS_CI_BUILD_INPUTS_DIGEST: plan.buildInputs.buildInputsDigest,
  };
  delete environment.NODE_TEST_CONTEXT;

  try {
    const result = spawnSync(process.execPath, [path.join(root, "tests", "run.mjs"), "tooling"], {
      cwd: root,
      encoding: "utf8",
      env: environment,
      windowsHide: true,
    });
    assert.equal(result.status, 0, `${result.stdout}\n${result.stderr}`);
    const manifest = JSON.parse(fs.readFileSync(manifestPath, "utf8"));
    const group = manifest.groups.find(item => item.groupId === "governance:architecture-boundaries");
    assert.ok(group);
    assert.deepEqual(group.invokedFiles, group.expectedFiles);
    assert.deepEqual(group.observedFiles, group.expectedFiles);
  } finally {
    fs.rmSync(fixtureRoot, { recursive: true, force: true });
  }
});

test("插件契约 runner 合并命令调用与引擎报告的实际文件身份", () => {
  const fixtureRoot = fs.mkdtempSync(path.join(os.tmpdir(), "nxp-runner-plugin-plan-"));
  const manifestPath = path.join(fixtureRoot, "plugin-manifest.json");
  const reportRoot = path.join(fixtureRoot, "reports");
  const sourcePlan = createExecutionPlan([], {
    all: true,
    base: "test-base",
    head: "test-head",
  });
  const pluginGroup = sourcePlan.selectedGroups.find(group => group.groupId === "domain:plugin");
  assert.ok(pluginGroup);
  const plan = {
    ...sourcePlan,
    selectedGroups: [{ ...pluginGroup, physicalJobId: "plugin-contract", mode: "ci" }],
  };
  const environment = {
    ...process.env,
    NEXUS_CI_MANIFEST: manifestPath,
    NEXUS_CI_REPORT_DIR: reportRoot,
    NEXUS_CI_JOB_ID: "plugin-contract",
    NEXUS_CI_DOMAIN: "plugin",
    NEXUS_CI_MODE: "ci",
    NEXUS_CI_HEAD_SHA: "test-head",
    NEXUS_CI_REPOSITORY_SHA: "test-head",
    NEXUS_CI_PLAN_DIGEST: plan.planDigest,
    NEXUS_CI_EXECUTION_PLAN: JSON.stringify(plan),
    NEXUS_CI_COUNTERPART_REPOSITORY: plan.counterpartRepository,
    NEXUS_CI_COUNTERPART_SHA: plan.counterpartSha,
    NEXUS_CI_SOURCE_DIGEST: plan.buildInputs.sourceDigest,
    NEXUS_CI_BUILD_INPUTS_DIGEST: plan.buildInputs.buildInputsDigest,
    NEXUS_OFFICIAL_PLUGINS_ROOT: defaultOfficialPluginsRoot,
  };
  delete environment.NODE_TEST_CONTEXT;

  try {
    const result = spawnSync(process.execPath, [path.join(root, "tests", "run.mjs"), "contract"], {
      cwd: root,
      encoding: "utf8",
      env: environment,
      windowsHide: true,
    });
    assert.equal(result.status, 0, `${result.stdout}\n${result.stderr}`);
    const manifest = JSON.parse(fs.readFileSync(manifestPath, "utf8"));
    const group = manifest.groups.find(item => item.groupId === "domain:plugin");
    assert.ok(group);
    assert.deepEqual(group.invokedFiles, group.expectedFiles);
    assert.deepEqual(group.observedFiles, group.expectedFiles);
    assert.equal(manifest.artifactManifest.present, false);
  } finally {
    fs.rmSync(fixtureRoot, { recursive: true, force: true });
  }
});

test("无 CI 计划的 docs 入口仍执行完整文档套件", () => {
  const environment = { ...process.env };
  delete environment.NEXUS_CI_MANIFEST;
  delete environment.NEXUS_CI_REPORT_DIR;
  delete environment.NEXUS_CI_EXECUTION_PLAN;
  delete environment.NODE_TEST_CONTEXT;
  const result = spawnSync(process.execPath, [path.join(root, "tests", "run.mjs"), "docs"], {
    cwd: root,
    encoding: "utf8",
    env: environment,
    windowsHide: true,
  });
  assert.equal(result.status, 0, `${result.stdout}\n${result.stderr}`);
  assert.match(`${result.stdout}\n${result.stderr}`, /passed=31 failed=0 skipped=0/u);
});
