import fs from "node:fs";
import { createHash } from "node:crypto";

export const sha256 = bytes => createHash("sha256").update(bytes).digest("hex");

function sameSet(actual, expected, label) {
  if (!Array.isArray(actual) || !Array.isArray(expected)
    || actual.some(value => typeof value !== "string" || !value)
    || new Set(actual).size !== actual.length || new Set(expected).size !== expected.length
    || actual.length !== expected.length
    || [...actual].sort().some((value, index) => value !== [...expected].sort()[index])) {
    throw new Error(`报告集合不一致：${label}`);
  }
}

export function validateResult(report, plan, native) {
  if (report.schemaVersion !== 1 || report.evidenceType !== "actual"
    || report.runId !== plan.runId || report.repository !== plan.repository
    || report.policySha256 !== plan.policySha256) throw new Error("报告身份不一致");
  for (const field of ["commitSha", "treeSha", "workingTreeDirty", "dirtyFingerprint"]) {
    if (report.source?.[field] !== plan.source?.[field]) throw new Error(`源码身份不一致：${field}`);
  }
  if (JSON.stringify(report.partner) !== JSON.stringify(plan.partner)) throw new Error("对端身份不一致");
  if (report.scope?.profile !== plan.profile || report.scope?.plugin !== plan.plugin) throw new Error("选择身份不一致");
  sameSet(report.scope.expectedScenarioIds, plan.scenarioIds, "expected scenarios");
  sameSet(report.scope.expectedCaseIds, plan.caseIds, "expected cases");
  if (report.status !== "PASS" || report.exitCode !== 0 || report.primaryFailure !== null) throw new Error("测试未通过");
  if (report.scope.applicability !== "required" || !plan.scenarioIds.length || !plan.caseIds.length) throw new Error("测试选择为空");
  sameSet(report.scope.completedScenarioIds, plan.scenarioIds, "completed scenarios");
  sameSet(report.scope.completedCaseIds, plan.caseIds, "completed cases");
  sameSet(native.caseIds, plan.caseIds, "native cases");
  if (native.passed !== plan.caseIds.length || native.failed !== 0 || native.skipped !== 0
    || report.counts?.passed !== native.passed || report.counts?.failed !== 0 || report.counts?.skipped !== 0) {
    throw new Error("原生报告计数不一致或存在失败/skip");
  }
  if (report.timing?.budgetMs !== plan.budgetMs || report.timing?.qualificationMs !== plan.qualificationMs
    || !Number.isFinite(report.timing.elapsedMs) || report.timing.elapsedMs < 0
    || report.timing.elapsedMs > plan.qualificationMs) throw new Error("报告预算不合规");
  if (plan.plugin && (!Number.isFinite(report.timing.exclusivePluginMs)
    || report.timing.exclusivePluginMs < 0 || report.timing.exclusivePluginMs > plan.pluginBudgetMs)) {
    throw new Error("插件累计预算不合规");
  }
  if (report.cleanup?.status !== "complete" || report.cleanup.remainingOwnedProcessCount !== 0) throw new Error("清理未完成");
  if (!report.artifacts?.length || report.artifacts.some(file => !fs.statSync(file).isFile())) throw new Error("原始报告缺失");
  return report;
}
