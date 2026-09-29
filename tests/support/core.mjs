import fs from "node:fs";
import path from "node:path";
import os from "node:os";
import { getProcessRunnerState } from "./process-runner.mjs";
import { sha256, validateResult } from "./report.mjs";

export async function runCore({ group, policy, policyBytes, workspace, runRoot, runId, budget, run }) {
  const selected = policy.groups[group];
  const backend = group === "backend" || group.startsWith("backend.");
  const gateId = group.startsWith("backend.") ? `host.${group}` : group === "frontend" ? "host.frontend.state" : null;
  const directory = path.join(runRoot, group);
  fs.mkdirSync(directory, { recursive: true });
  const rawReport = path.join(directory, backend ? "native.trx" : "native.json");
  const normalized = path.join(directory, "native-counts.json");
  const logPath = path.join(directory, "commands.log");
  const log = fs.openSync(logPath, "wx");
  const phases = [{ name: "校验输入与隔离副本", owner: "common", elapsedMs: workspace.preparationMs }];
  const startedAt = new Date().toISOString();
  let native = { caseIds: [], passed: 0, failed: 0, skipped: 0 };
  let code = 0;
  let failure = null;
  const plan = {
    runId, repository: "FlappiBakuse/NexusPipeline", source: workspace.source,
    partner: null, policySha256: sha256(policyBytes), profile: group, plugin: null,
    scenarioIds: selected.scenarioIds, caseIds: selected.caseIds, budgetMs: policy.invocationBudgetMs,
    qualificationMs: policy.qualificationMs,
  };
  async function step(label, command, args, cwd = workspace.directory, failureCode = null) {
    const started = budget.elapsedMs;
    console.error(`[Host / ${group}] ${label}`);
    fs.writeSync(log, `${label}\n${JSON.stringify([command, ...args])}\n`);
    const result = await run(command, args, { cwd, onOutput: text => fs.writeSync(log, text) });
    phases.push({ name: label, owner: "suite", elapsedMs: budget.elapsedMs - started });
    if (result !== 0) {
      const error = new Error(`${label}: exit ${result}`);
      error.exitCode = result === 5 || result === 130 ? result : failureCode ?? result;
      throw error;
    }
  }
  try {
    if (backend) {
      await step("构建并验证执行与配置规则", "dotnet", [
        "test", "tests/NexusPipeline.Tests/NexusPipeline.Tests.csproj", "-p:NexusTestHost=true",
        "-p:UseSharedCompilation=false", "--disable-build-servers", "--nologo",
        "--filter", selected.filter, "--logger", "trx;LogFileName=native.trx", "--results-directory", directory,
      ]);
    } else {
      const frontend = path.join(workspace.directory, "frontend");
      const npm = process.platform === "win32" ? "npm.cmd" : "npm";
      if (!fs.existsSync(path.join(frontend, "node_modules", ".package-lock.json"))) {
        await step("准备锁定前端依赖", npm, ["ci", "--no-audit", "--no-fund"], frontend);
      }
      await step("检查前端类型", npm, ["run", "typecheck"], frontend);
      await step("验证核心状态与桥接", npm, ["run", "test", "--", ...selected.files,
        "--reporter=json", "--outputFile", rawReport], frontend);
    }
    await step("读取原生报告", process.platform === "win32" ? "python" : "python3", [
      path.join(workspace.directory, "tests", "support", "native-report.py"),
      backend ? "trx" : "vitest", rawReport, normalized,
    ], workspace.directory, 4);
    native = JSON.parse(fs.readFileSync(normalized, "utf8"));
  } catch (error) {
    code = error.exitCode ?? 3;
    failure = { category: code === 5 ? "budget" : code === 130 ? "cancelled" : code === 4 ? "report" : "unclassified", message: error.message };
  } finally {
    fs.closeSync(log);
  }
  const cleanup = getProcessRunnerState();
  const report = {
    schemaVersion: 1, evidenceType: "actual", gateId, runId, repository: plan.repository, source: plan.source,
    partner: null, policySha256: plan.policySha256,
    scope: { profile: group, plugin: null, applicability: "required", selectionReason: "explicit core group",
      expectedScenarioIds: selected.scenarioIds, completedScenarioIds: code === 0 ? selected.scenarioIds : [],
      expectedCaseIds: selected.caseIds, completedCaseIds: native.caseIds },
    environment: { label: `${os.platform()} ${os.release()}`, toolchain: workspace.toolchain,
      cold: !workspace.cacheHit, coldReason: workspace.cacheHit ? null : "source/toolchain cache miss; normal budget applies",
      environmentFingerprint: sha256(`${os.platform()} ${os.release()} ${JSON.stringify(workspace.toolchain)}`), ci: null },
    status: code === 0 ? "PASS" : code === 5 ? "TIMEOUT" : code === 130 ? "CANCELLED" : "FAIL",
    exitCode: code,
    timing: { budgetMs: policy.invocationBudgetMs, qualificationMs: policy.qualificationMs,
      elapsedMs: budget.elapsedMs, exclusivePluginMs: null,
      ciJobElapsedMs: null, queueMs: null, startedAt, completedAt: new Date().toISOString(), phases },
    counts: { passed: native.passed, failed: native.failed, skipped: native.skipped },
    boundaries: selected.boundaries,
    cleanup: { status: cleanup.cleanupComplete ? "complete" : "failed",
      remainingOwnedProcessCount: cleanup.cleanupComplete ? 0 : null, notes: cleanup.cleanupFailures },
    artifacts: [logPath, rawReport, normalized].filter(file => fs.existsSync(file)), primaryFailure: failure,
  };
  if (code === 0) {
    try { validateResult(report, plan, native); }
    catch (error) {
      code = !cleanup.cleanupComplete ? 6 : budget.remainingMs({ cleanup: true }) === 0 ? 5 : 4;
      report.status = code === 5 ? "TIMEOUT" : "FAIL";
      report.exitCode = code;
      report.primaryFailure = { category: code === 6 ? "cleanup" : code === 5 ? "budget" : "report", message: error.message };
    }
  }
  fs.writeFileSync(path.join(directory, "summary.json"), `${JSON.stringify(report, null, 2)}\n`);
  console.error(`[Host / ${group}] ${report.status}; ${Math.round(report.timing.elapsedMs)}ms; ${directory}`);
  return code;
}
