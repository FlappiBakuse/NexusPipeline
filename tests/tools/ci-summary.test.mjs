import test from "node:test";
import assert from "node:assert/strict";
import { createExecutionPlan } from "../../tools/ci-changes.mjs";
import { evaluateRequiredSummary, executionPlanDigest, validateExecutionPlan } from "../../tools/ci-summary.mjs";

function planFor(files = ["docs/DESIGN.md"]) {
  return createExecutionPlan(files, { base: "base", head: "head" });
}

test("全量计划由单个完整回归结果满足", () => {
  const plan = createExecutionPlan([], { base: "base", head: "head", all: true });
  const result = evaluateRequiredSummary(validInput({ plan, selector: selectorFor(plan), jobs: {
    "full-regression": manifestFor({ planDigest: plan.planDigest }),
  } }));
  assert.equal(result.ok, true, result.issues.join("\n"));
  assert.deepEqual(result.selectedJobs, ["full-regression"]);
});

function selectorFor(plan, overrides = {}) {
  return {
    result: "success",
    planValid: true,
    headSha: "head",
    planDigest: plan.planDigest,
    ...overrides,
  };
}

function manifestFor(overrides = {}) {
  return {
    result: "success",
    headSha: "head",
    repositorySha: "host",
    planDigest: "",
    buildFingerprint: "fingerprint",
    manifestPresent: true,
    testCount: 3,
    passed: 3,
    failed: 0,
    skipped: 0,
    exitCode: 0,
    ...overrides,
  };
}

function validInput(overrides = {}) {
  const plan = planFor();
  return {
    plan,
    selector: selectorFor(plan),
    jobs: {
      "docs-i18n": manifestFor({ planDigest: plan.planDigest }),
    },
    expectedHeadSha: "head",
    expectedRepositorySha: "host",
    ...overrides,
  };
}

test("required-summary 接受同一计划、成功 job 和非零通过计数", () => {
  const result = evaluateRequiredSummary(validInput());
  assert.equal(result.ok, true, result.issues.join("\n"));
  assert.deepEqual(result.selectedJobs, ["docs-i18n"]);
});

test("selected job skipped、failure、cancelled 或缺失均失败", () => {
  for (const result of ["skipped", "failure", "cancelled"]) {
    const summary = evaluateRequiredSummary(validInput({
      jobs: { "docs-i18n": manifestFor({ result, planDigest: planFor().planDigest }) },
    }));
    assert.equal(summary.ok, false, result);
    assert.match(summary.issues.join("\n"), /selected job 结果/u);
  }
  const missing = evaluateRequiredSummary(validInput({ jobs: {} }));
  assert.equal(missing.ok, false);
  assert.deepEqual(missing.missingJobs, ["docs-i18n"]);
});

test("未选 job skipped 允许，额外执行成功会记录为重复执行", () => {
  const plan = planFor();
  const result = evaluateRequiredSummary({
    plan,
    selector: selectorFor(plan),
    jobs: {
      "docs-i18n": manifestFor({ planDigest: plan.planDigest }),
      "frontend-unit": { result: "skipped" },
      "host-core": { result: "success" },
    },
    expectedHeadSha: "head",
    expectedRepositorySha: "host",
  });
  assert.equal(result.ok, true, result.issues.join("\n"));
  assert.deepEqual(result.duplicateExecutions, ["host-core"]);
});

test("零测试、计数不守恒、缺 manifest、SHA 或构建指纹均失败", () => {
  const cases = [
    { testCount: 0 },
    { testCount: 3, passed: 2, skipped: 0 },
    { manifestPresent: false },
    { headSha: "other" },
    { buildFingerprint: "" },
  ];
  for (const overrides of cases) {
    const plan = planFor();
    const result = evaluateRequiredSummary({
      plan,
      selector: selectorFor(plan),
      jobs: { "docs-i18n": manifestFor({ ...overrides, planDigest: plan.planDigest }) },
      expectedHeadSha: "head",
      expectedRepositorySha: "host",
    });
    assert.equal(result.ok, false, JSON.stringify(overrides));
  }
});

test("计划 digest、selector digest 和仓库身份必须一致", () => {
  const plan = planFor();
  assert.equal(executionPlanDigest(plan), plan.planDigest);
  assert.equal(validateExecutionPlan(plan, { expectedHeadSha: "head" }).ok, true);
  const wrongSelector = evaluateRequiredSummary(validInput({ selector: selectorFor(plan, { planDigest: "wrong" }) }));
  assert.equal(wrongSelector.ok, false);
  const wrongPlan = evaluateRequiredSummary(validInput({ expectedHeadSha: "other" }));
  assert.equal(wrongPlan.ok, false);
});

test("计划结构缺少域或包含未知域时拒绝", () => {
  const plan = planFor();
  delete plan.domains.docs;
  plan.domains.extra = { affected: false, files: [], reasons: [], jobs: [] };
  const result = evaluateRequiredSummary({ plan, jobs: {} });
  assert.equal(result.ok, false);
  assert.match(result.issues.join("\n"), /缺少域|未知域/u);
});

test("汇总拒绝缺少选择器、伪造作业映射和额外失败", () => {
  assert.equal(evaluateRequiredSummary(validInput({ selector: null })).ok, false);
  const input = validInput();
  input.plan.domains.docs.jobs = ["unregistered-job"];
  input.plan.planDigest = executionPlanDigest(input.plan);
  assert.equal(evaluateRequiredSummary(input).ok, false);
  const extra = validInput();
  extra.jobs["host-core"] = { result: "failure" };
  assert.equal(evaluateRequiredSummary(extra).ok, false);
  const missingExit = validInput();
  missingExit.jobs["docs-i18n"].exitCode = null;
  assert.equal(evaluateRequiredSummary(missingExit).ok, false);
});
