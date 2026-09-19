import test from "node:test";
import assert from "node:assert/strict";
import { createExecutionPlan } from "../../tools/ci-changes.mjs";
import { artifactSetDigest } from "../../tools/artifact-manifest.mjs";
import {
  evaluateRequiredSummary,
  executionPlanDigest,
  validateExecutionPlan,
} from "../../tools/ci-summary.mjs";

const BASE = "base";
const HEAD = "head";
const REPOSITORY = "host";

function planFor(files = ["docs/DESIGN.md"], options = {}) {
  return createExecutionPlan(files, { base: BASE, head: HEAD, ...options });
}

function selectorFor(plan, overrides = {}) {
  return {
    result: "success",
    planValid: true,
    headSha: HEAD,
    planDigest: plan.planDigest,
    ...overrides,
  };
}

function selectedFor(plan, jobId) {
  return plan.selectedGroups.filter(group => group.physicalJobId === jobId);
}

function requiredChecks(groups) {
  return [...new Set(groups.flatMap(group => group.requiredChecks || []))];
}

function artifactManifestFor(mode, present) {
  const files = present
    ? [{ relativePath: "nexus-pipeline.exe", byteLength: 1, sha256: "a".repeat(64) }]
    : [];
  return {
    schemaVersion: 1,
    present,
    kind: present ? "runtime" : "none",
    mode,
    producer: "test",
    files,
    artifactSetDigest: present ? artifactSetDigest(files) : null,
  };
}

function manifestFor(plan, jobId, overrides = {}) {
  const groups = selectedFor(plan, jobId);
  assert.ok(groups.length > 0, `计划没有绑定 ${jobId}`);
  const mode = groups[0].mode;
  const integrityLevel = groups.some(group => group.requiredIntegrity === "high-or-system") ? "high" : "none";
  const buildFingerprint = "f".repeat(64);
  const binaryRequired = groups.some(group => group.mode === "admin" || ["host", "frontend"].includes(group.kind));
  const artifactManifest = artifactManifestFor(mode, binaryRequired);
  const records = groups.map(group => ({
    groupId: group.groupId,
    physicalJobId: jobId,
    mode: group.mode,
    integrityLevel,
    testSelectionIdentity: group.testSelectionIdentity,
    plannedTests: group.expectedTests,
    expectedFiles: group.expectedFiles,
    selectedTests: group.expectedFiles,
    invokedFiles: group.expectedFiles,
    observedFiles: group.expectedFiles,
    selection: {
      plannedTests: group.expectedTests,
      actualTests: group.expectedFiles,
      expectedFiles: group.expectedFiles,
      invokedFiles: group.expectedFiles,
      observedFiles: group.expectedFiles,
    },
    observedCases: [
      ...group.exclusions.map(exclusion => ({ id: exclusion.testId, status: "required-in-other-mode" })),
      { id: `${group.groupId}:case`, title: "synthetic passing case", status: "passed" },
    ],
    result: "success",
    testCount: 1,
    passed: 1,
    failed: 0,
    skipped: 0,
    nativeTotal: 1,
    exitCode: 0,
    observedMode: group.mode,
    timeScale: "1",
    exclusions: group.exclusions,
  }));
  return {
    schemaVersion: 2,
    jobId,
    domainId: jobId === "full-regression"
      ? "full-regression"
      : jobId === "host-core"
        ? "host"
        : jobId === "frontend-unit"
          ? "frontend"
          : jobId === "docs-i18n"
            ? "docs"
            : jobId === "plugin-contract"
              ? "plugin"
              : jobId === "ui-smoke"
                ? "ui"
                : jobId === "system-runtime-mcp" ? "system_runtime"
                  : jobId === "system-execution" ? "system_execution"
                    : jobId === "system-emulator" ? "system_emulator"
                      : jobId === "system-update" ? "system_update" : "",
    result: "success",
    headSha: HEAD,
    repositorySha: REPOSITORY,
    planDigest: plan.planDigest,
    buildFingerprint,
    sourceDigest: plan.buildInputs.sourceDigest,
    buildInputsDigest: plan.buildInputs.buildInputsDigest,
    counterpartRepository: plan.counterpartRepository,
    counterpartSha: plan.counterpartSha,
    artifactDigest: artifactManifest.present ? artifactManifest.artifactSetDigest : null,
    artifactManifest,
    artifactManifests: artifactManifest.present ? [artifactManifest] : [],
    mode,
    integrityLevel,
    checks: requiredChecks(groups).map(checkId => ({ checkId, status: "success" })),
    testCount: records.length,
    passed: records.length,
    failed: 0,
    skipped: 0,
    exitCode: 0,
    manifestPresent: true,
    groups: records,
    ...overrides,
  };
}

function inputFor(plan = planFor(), overrides = {}) {
  return {
    plan,
    selector: selectorFor(plan),
    jobs: {
      ...Object.fromEntries([...new Set(plan.selectedGroups.map(group => group.physicalJobId))]
        .map(jobId => [jobId, manifestFor(plan, jobId)])),
    },
    expectedHeadSha: HEAD,
    expectedRepositorySha: REPOSITORY,
    ...overrides,
  };
}

test("有效 docs-only 计划按逻辑组通过，未选 Job 的 skip 可记录", () => {
  const plan = planFor();
  const result = evaluateRequiredSummary(inputFor(plan, {
    jobs: {
      "docs-i18n": manifestFor(plan, "docs-i18n"),
      "frontend-unit": { result: "skipped" },
      "host-core": { result: "skipped" },
    },
  }));
  assert.equal(result.ok, true, result.issues.join("\n"));
  assert.deepEqual(result.selectedJobs, ["docs-i18n"]);
  assert.ok(result.unselectedJobs.includes("frontend-unit"));
  assert.ok(result.unselectedJobs.includes("host-core"));
  assert.equal(result.unselectedJobResults.find(item => item.jobId === "frontend-unit")?.result, "skipped");
  assert.deepEqual(result.missingGroups, []);
});

test("有效全量计划由 full-regression 覆盖全部逻辑组", () => {
  const plan = planFor([], { all: true });
  const result = evaluateRequiredSummary(inputFor(plan, {
    jobs: { "full-regression": manifestFor(plan, "full-regression") },
  }));
  assert.equal(result.ok, true, result.issues.join("\n"));
  assert.deepEqual(result.selectedJobs, ["full-regression"]);
  assert.equal(plan.selectedGroups.length, 30);
});

test("C01：缺少计划中的逻辑组不能由同一 Job 的其他组抵消", () => {
  const plan = planFor(["frontend/src/App.vue"]);
  const manifest = manifestFor(plan, "frontend-unit");
  manifest.groups = manifest.groups.filter(group => group.groupId !== "frontend:bridge");
  const result = evaluateRequiredSummary(inputFor(plan, { jobs: { "frontend-unit": manifest } }));
  assert.equal(result.ok, false);
  assert.ok(result.missingGroups.includes("frontend:bridge"));
});

test("C02：单个逻辑组为零执行时汇总失败", () => {
  const plan = planFor(["frontend/src/App.vue"]);
  const manifest = manifestFor(plan, "frontend-unit");
  const group = manifest.groups.find(item => item.groupId === "frontend:features");
  Object.assign(group, { testCount: 0, passed: 0, nativeTotal: 0 });
  const result = evaluateRequiredSummary(inputFor(plan, { jobs: { "frontend-unit": manifest } }));
  assert.equal(result.ok, false);
  assert.match(result.issues.join("\n"), /frontend:features.*testCount/u);
});

test("C03：管理员计划拒绝 Test Host 结果替代", () => {
  const plan = planFor(["frontend/src/features/history/HistoryView.vue"]);
  const manifest = manifestFor(plan, "ui-smoke", { mode: "test-host" });
  const result = evaluateRequiredSummary(inputFor(plan, { jobs: { "ui-smoke": manifest } }));
  assert.equal(result.ok, false);
  assert.match(result.issues.join("\n"), /ui-smoke.*mode/u);
});

test("C04：管理员计划要求 High/System Integrity", () => {
  const plan = planFor(["frontend/src/features/history/HistoryView.vue"]);
  const manifest = manifestFor(plan, "ui-smoke", { integrityLevel: "none" });
  const result = evaluateRequiredSummary(inputFor(plan, { jobs: { "ui-smoke": manifest } }));
  assert.equal(result.ok, false);
  assert.match(result.issues.join("\n"), /Integrity|完整性/u);
});

test("C05：错误的计划 digest 或逻辑组身份不能通过", () => {
  const plan = planFor();
  const wrongSelector = evaluateRequiredSummary(inputFor(plan, {
    selector: selectorFor(plan, { planDigest: "0".repeat(64) }),
  }));
  assert.equal(wrongSelector.ok, false);

  const wrongGroup = manifestFor(plan, "docs-i18n");
  wrongGroup.groups[0].testSelectionIdentity = "0".repeat(64);
  const wrongResult = evaluateRequiredSummary(inputFor(plan, { jobs: { "docs-i18n": wrongGroup } }));
  assert.equal(wrongResult.ok, false);
  assert.match(wrongResult.issues.join("\n"), /testSelectionIdentity/u);
});

test("结果必须携带当前格式的 Job 身份、摘要和结构化实际选择", () => {
  const plan = planFor();
  const missingJobIdentity = manifestFor(plan, "docs-i18n");
  delete missingJobIdentity.groups[0].physicalJobId;
  assert.equal(evaluateRequiredSummary(inputFor(plan, { jobs: { "docs-i18n": missingJobIdentity } })).ok, false);

  const arbitraryFingerprint = manifestFor(plan, "docs-i18n", { buildFingerprint: "fingerprint" });
  const fingerprintResult = evaluateRequiredSummary(inputFor(plan, { jobs: { "docs-i18n": arbitraryFingerprint } }));
  assert.equal(fingerprintResult.ok, false);
  assert.match(fingerprintResult.issues.join("\n"), /buildFingerprint/u);

  const wrongSelection = manifestFor(plan, "docs-i18n");
  wrongSelection.groups[0].selectedTests = ["tests/tools/not-planned.mjs"];
  wrongSelection.groups[0].invokedFiles = wrongSelection.groups[0].selectedTests;
  wrongSelection.groups[0].selection.invokedFiles = wrongSelection.groups[0].selectedTests;
  wrongSelection.groups[0].selection.actualTests = wrongSelection.groups[0].selectedTests;
  const selectionResult = evaluateRequiredSummary(inputFor(plan, { jobs: { "docs-i18n": wrongSelection } }));
  assert.equal(selectionResult.ok, false);
  assert.match(selectionResult.issues.join("\n"), /实际调用文件|实际测试选择/u);

  const missingStructuredSelection = manifestFor(plan, "docs-i18n");
  delete missingStructuredSelection.groups[0].selection;
  assert.equal(evaluateRequiredSummary(inputFor(plan, { jobs: { "docs-i18n": missingStructuredSelection } })).ok, false);
});

test("C06：源代码、构建输入和产物身份必须与计划一致", () => {
  const plan = planFor();
  for (const field of ["sourceDigest", "buildInputsDigest", "artifactDigest"]) {
    const manifest = manifestFor(plan, "docs-i18n", { [field]: "0".repeat(64) });
    const result = evaluateRequiredSummary(inputFor(plan, { jobs: { "docs-i18n": manifest } }));
    assert.equal(result.ok, false, field);
  }
});

test("C07：双仓库候选身份必须与锁定计划一致", () => {
  const plan = planFor();
  const manifest = manifestFor(plan, "docs-i18n", { counterpartSha: "1".repeat(40) });
  const result = evaluateRequiredSummary(inputFor(plan, { jobs: { "docs-i18n": manifest } }));
  assert.equal(result.ok, false);
  assert.match(result.issues.join("\n"), /counterpartSha/u);
});

test("C08：未声明的 skip 与未计划排除均失败", () => {
  const plan = planFor(["frontend/src/App.vue"]);
  const manifest = manifestFor(plan, "frontend-unit");
  const group = manifest.groups.find(item => item.groupId === "frontend:ui");
  Object.assign(group, { skipped: 1, passed: 0, exclusions: [] });
  const result = evaluateRequiredSummary(inputFor(plan, { jobs: { "frontend-unit": manifest } }));
  assert.equal(result.ok, false);
  assert.match(result.issues.join("\n"), /skip|排除/u);

  const updatePlan = planFor(["src/Modules/Updates/UpdateService.cs"]);
  const updateManifest = manifestFor(updatePlan, "system-update");
  const updateGroup = updateManifest.groups.find(item => item.groupId === "system:update");
  updateGroup.exclusions = [...updateGroup.exclusions, "unplanned:test"];
  const unexpected = evaluateRequiredSummary(inputFor(updatePlan, {
    jobs: {
      "host-core": manifestFor(updatePlan, "host-core"),
      "system-update": updateManifest,
    },
  }));
  assert.equal(unexpected.ok, false);
});

test("C09：计划声明的 System Update 模式排除可被逐组核对", () => {
  const plan = planFor(["src/Modules/Updates/UpdateService.cs"]);
  const result = evaluateRequiredSummary(inputFor(plan));
  assert.equal(result.ok, true, result.issues.join("\n"));
  assert.ok(result.excludedGroups.includes("system:update"));
});

test("F2-01：计划的两个 System Update 文件漏跑一个时失败并列出缺失", () => {
  const plan = planFor(["src/Modules/Updates/UpdateService.cs"]);
  const input = inputFor(plan);
  const manifest = input.jobs["system-update"];
  const group = manifest.groups.find(item => item.groupId === "system:update");
  assert.equal(group.expectedFiles.length, 2);
  const oneFile = [group.expectedFiles[0]];
  group.selectedTests = oneFile;
  group.invokedFiles = oneFile;
  group.observedFiles = oneFile;
  group.selection.actualTests = oneFile;
  group.selection.invokedFiles = oneFile;
  group.selection.observedFiles = oneFile;
  const result = evaluateRequiredSummary(input);
  assert.equal(result.ok, false);
  assert.match(result.issues.join("\n"), /实际观测文件缺失/u);
});

test("F2-01：一个许可排除不能覆盖七个实际 skipped case", () => {
  const plan = planFor(["src/Modules/Updates/UpdateService.cs"]);
  const input = inputFor(plan);
  const group = input.jobs["system-update"].groups.find(item => item.groupId === "system:update");
  Object.assign(group, {
    testCount: 8,
    passed: 1,
    skipped: 7,
    nativeTotal: 8,
    observedCases: [
      { id: "update:swap-ready", status: "required-in-other-mode" },
      ...Array.from({ length: 6 }, (_, index) => ({ id: `unexpected:skip-${index + 1}`, status: "skipped-by-engine" })),
    ],
  });
  const result = evaluateRequiredSummary(input);
  assert.equal(result.ok, false);
  assert.match(result.issues.join("\n"), /未计划 skip/u);
});

test("F2-01：skip 数量相同但身份错误时失败", () => {
  const plan = planFor(["src/Modules/Updates/UpdateService.cs"]);
  const input = inputFor(plan);
  const group = input.jobs["system-update"].groups.find(item => item.groupId === "system:update");
  Object.assign(group, {
    testCount: 2,
    passed: 1,
    skipped: 1,
    nativeTotal: 2,
    observedCases: [{ id: "other:case", status: "skipped-by-engine" }],
  });
  const result = evaluateRequiredSummary(input);
  assert.equal(result.ok, false);
  assert.match(result.issues.join("\n"), /计划排除未在实际结果中闭合|未计划 skip/u);
});

test("C10：缺少物理 Job、重复 Job 和额外运行均有明确分类", () => {
  const plan = planFor();
  const missing = evaluateRequiredSummary(inputFor(plan, { jobs: {} }));
  assert.equal(missing.ok, false);
  assert.deepEqual(missing.missingJobs, ["docs-i18n"]);

  const extra = evaluateRequiredSummary(inputFor(plan, {
    jobs: [
      { jobId: "docs-i18n", ...manifestFor(plan, "docs-i18n") },
      { jobId: "docs-i18n", ...manifestFor(plan, "docs-i18n") },
      { jobId: "host-core", result: "success" },
    ],
  }));
  assert.equal(extra.ok, false);
  assert.ok(extra.duplicateExecutions.includes("host-core"));
});

test("C11：规范化计数与原生 total 不一致时失败", () => {
  const plan = planFor();
  const manifest = manifestFor(plan, "docs-i18n");
  manifest.groups[0].nativeTotal = 2;
  const result = evaluateRequiredSummary(inputFor(plan, { jobs: { "docs-i18n": manifest } }));
  assert.equal(result.ok, false);
  assert.match(result.issues.join("\n"), /native total/u);
});

test("C12：docs-only 只选择 docs 物理 Job，选择器和结果可完整对账", () => {
  const plan = planFor(["docs/reference/ui/README.md"]);
  const result = evaluateRequiredSummary(inputFor(plan));
  assert.equal(result.ok, true, result.issues.join("\n"));
  assert.deepEqual(result.selectedJobs, ["docs-i18n"]);
  assert.ok(result.unselectedJobs.includes("frontend-unit"));
  assert.ok(result.unselectedJobs.includes("host-core"));
});

test("C13：full-regression 缺一个核心逻辑组时失败", () => {
  const plan = planFor([], { all: true });
  const manifest = manifestFor(plan, "full-regression");
  manifest.groups = manifest.groups.filter(group => group.groupId !== "host:core");
  const result = evaluateRequiredSummary(inputFor(plan, { jobs: { "full-regression": manifest } }));
  assert.equal(result.ok, false);
  assert.ok(result.missingGroups.includes("host:core"));
});

test("C14：构建 check 失败时测试全绿也不能通过", () => {
  const plan = planFor(["frontend/src/App.vue"]);
  const manifest = manifestFor(plan, "frontend-unit");
  manifest.checks = manifest.checks.map(check => check.checkId === "build" ? { ...check, status: "failure" } : check);
  const result = evaluateRequiredSummary(inputFor(plan, { jobs: { "frontend-unit": manifest } }));
  assert.equal(result.ok, false);
  assert.match(result.issues.join("\n"), /build/u);
});

test("Plugin Contract Job 显式承接未选 Gate 的宿主插件与公共 UI 契约", () => {
  const plan = planFor(["docs/PLUGIN_API.md"]);
  assert.deepEqual(
    plan.selectedGroups.filter(group => group.physicalJobId === "plugin-contract").map(group => group.groupId),
    ["host:plugins", "frontend:ui", "frontend:bridge", "domain:plugin"],
  );
  const result = evaluateRequiredSummary(inputFor(plan));
  assert.equal(result.ok, true, result.issues.join("\n"));
  assert.deepEqual(result.selectedJobs, ["docs-i18n", "plugin-contract"]);
  assert.deepEqual(result.missingGroups, []);
});

test("Plugin Contract Job 在宿主与前端 Gate 均选中时复用两者的 build/tests", () => {
  const plan = planFor(["plugins.lock.json"]);
  assert.equal(plan.all, false);
  assert.deepEqual(
    plan.selectedGroups.filter(group => group.physicalJobId === "plugin-contract").map(group => group.groupId),
    ["domain:plugin"],
  );
  const input = inputFor(plan);
  input.jobs["plugin-contract"].checks = input.jobs["plugin-contract"].checks.filter(check => check.checkId === "contract");
  const result = evaluateRequiredSummary(input);
  assert.equal(result.ok, true, result.issues.join("\n"));
});

test("计划摘要和结构校验拒绝未知域与篡改 digest", () => {
  const plan = planFor();
  assert.equal(executionPlanDigest(plan), plan.planDigest);
  assert.equal(validateExecutionPlan(plan, { expectedHeadSha: HEAD }).ok, true);

  const missingDomain = structuredClone(plan);
  delete missingDomain.domains.docs;
  assert.equal(validateExecutionPlan(missingDomain).ok, false);

  const unknownDomain = structuredClone(plan);
  unknownDomain.domains.extra = { affected: false, files: [], reasons: [], jobs: [] };
  assert.equal(validateExecutionPlan(unknownDomain).ok, false);

  const tampered = structuredClone(plan);
  tampered.counterpartSha = "1".repeat(40);
  assert.equal(validateExecutionPlan(tampered).ok, false);

  const tamperedGroups = planFor(["src/Modules/Updates/UpdateService.cs"]);
  tamperedGroups.testGroups.host = ["update"];
  tamperedGroups.planDigest = executionPlanDigest(tamperedGroups);
  assert.equal(validateExecutionPlan(tamperedGroups).ok, false);

  const tamperedExpectedTests = structuredClone(plan);
  tamperedExpectedTests.selectedGroups[0].expectedTests = ["tests/tools/not-planned.mjs"];
  tamperedExpectedTests.planDigest = executionPlanDigest(tamperedExpectedTests);
  assert.equal(validateExecutionPlan(tamperedExpectedTests).ok, false);
});
