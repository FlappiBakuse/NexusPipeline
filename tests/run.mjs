import fs from "node:fs";
import path from "node:path";
import os from "node:os";
import { execFileSync } from "node:child_process";
import { fileURLToPath } from "node:url";
import { runProcess, getProcessRunnerState, resetProcessRunnerState } from "./support/process-runner.mjs";
import { findAvailablePort, resolveTestRunRoot } from "./support/test-runtime.mjs";
import { Budget } from "./support/budget.mjs";
import { stageWorkspace } from "./support/workspace.mjs";
import { runCore } from "./support/core.mjs";
import { runScopeCommand } from "./scope-cli.mjs";
import { readRegistry } from "./scope-plan.mjs";
import { loadBatch, saveBatch } from "./batch-runner.mjs";

const projectRoot = path.resolve(path.dirname(fileURLToPath(import.meta.url)), "..");
if (process.argv[2] === "plan") {
  try { runScopeCommand(projectRoot, process.argv.slice(3)); process.exit(0); }
  catch (error) { console.error(error.stack || error.message); process.exit(2); }
}
const policyBytes = fs.readFileSync(path.join(projectRoot, "tests", "policy.json"));
const policy = JSON.parse(policyBytes);
if (policy.invocationBudgetMs !== 180_000 || policy.qualificationMs !== 150_000 || policy.cleanupReserveMs < 10_000
  || policy.cleanupReserveMs >= policy.invocationBudgetMs) throw new Error("Invalid Host budget policy");
const batchContext = process.argv[2] === "batch" ? loadBatch(projectRoot,process.argv.slice(3)) : null;
let executionRoot = projectRoot;
let frontendDir = path.join(projectRoot, "frontend");
let e2eDir = path.join(projectRoot, "tests", "e2e");
let runId = process.env.NEXUS_TEST_RUN_ID?.trim() || `run-${Date.now()}-${process.pid}`;
if (["batch", "ci", "smoke", "daily", "integration", "diagnostic", "gate"].includes(process.argv[2]?.toLowerCase())
    && !process.env.NEXUS_TEST_ARTIFACT_ROOT) {
  const base = path.join(process.env.RUNNER_TEMP || os.tmpdir(), "NexusPipeline.Tests");
  if (!fs.existsSync(base)) {
    fs.mkdirSync(base);
    fs.writeFileSync(path.join(base, ".nxp-test-artifact-root.json"), JSON.stringify({
      schemaVersion: 1, owner: "NexusPipeline.Tests", directory: base,
    }), { flag: "wx" });
  }
  process.env.NEXUS_TEST_ARTIFACT_ROOT = base;
}
let runRoot = resolveTestRunRoot(projectRoot, runId);
const jobStartedAt=Number(process.env.NEXUS_TEST_JOB_STARTED_AT_MS||Date.now());
if(!Number.isFinite(jobStartedAt)||jobStartedAt>Date.now()) throw new Error("Invalid controlled job start");
const setupElapsedMs=Date.now()-jobStartedAt;
if(batchContext) batchContext.setupElapsedMs=setupElapsedMs;
const invocationBudget = process.argv[2]?.toLowerCase() === "release" ? null
  : new Budget("Host 测试命令", policy.invocationBudgetMs,
    { reserveMs: batchContext ? 50000 : policy.cleanupReserveMs, qualificationMs: policy.qualificationMs,
      inheritedWorkMs:Math.min(process.env.NEXUS_TEST_PARENT_WORK_DEADLINE_MS ? Number(process.env.NEXUS_TEST_PARENT_WORK_DEADLINE_MS)-Date.now() : Infinity,process.env.NEXUS_TEST_JOB_STARTED_AT_MS ? 130000-(Date.now()-Number(process.env.NEXUS_TEST_JOB_STARTED_AT_MS)) : Infinity),
      inheritedHardMs:Math.min(process.env.NEXUS_TEST_PARENT_HARD_DEADLINE_MS ? Number(process.env.NEXUS_TEST_PARENT_HARD_DEADLINE_MS)-Date.now() : Infinity,
        process.env.NEXUS_TEST_JOB_STARTED_AT_MS?180000-setupElapsedMs:Infinity) });
const cancellation = new AbortController();
const cancel = () => cancellation.abort();
process.once("SIGINT", cancel);
process.once("SIGTERM", cancel);
const testHostDir = path.join(runRoot, "test-host");
const npmCommand = process.platform === "win32" ? "npm.cmd" : "npm";
const pythonCommand = process.platform === "win32" ? "python" : "python3";

function step(label) {
  console.error(`\n== ${label} ==`);
}

async function run(command, args, options = {}) {
  let env = options.env ?? process.env;
  if (invocationBudget) {
    const root = process.env.NEXUS_TEST_ARTIFACT_ROOT;
    if (root) {
      const temporary = path.join(root, "tmp");
      fs.mkdirSync(temporary, { recursive: true });
      env = { ...env, TEMP: temporary, TMP: temporary,
        NUGET_PACKAGES: process.env.NUGET_PACKAGES || path.join(root, "cache", "nuget"),
        npm_config_cache: process.env.npm_config_cache || path.join(root, "cache", "npm"),
        DOTNET_CLI_HOME: process.env.DOTNET_CLI_HOME || path.join(root, "cache", "dotnet"),
        DOTNET_GENERATE_ASPNET_CERTIFICATE: "false", DOTNET_CLI_USE_MSBUILD_SERVER: "0",
        DOTNET_CLI_TELEMETRY_OPTOUT: "1", DOTNET_CLI_WORKLOAD_UPDATE_NOTIFY_DISABLE: "true", DOTNET_NOLOGO: "1",
        PYTHONDONTWRITEBYTECODE: "1", PYTHONUTF8: "1" };
    }
  }
  if(batchContext) {
    const log=path.join(runRoot,"commands.log");fs.mkdirSync(runRoot,{recursive:true});
    fs.appendFileSync(log,JSON.stringify([command,...args])+"\n");
    const onOutput=options.onOutput;
    options={...options,onOutput:text=>{fs.appendFileSync(log,text);onOutput?.(text);}};
  }
  return runProcess(command, args, {
    defaultCwd: executionRoot, ...options, env, budget: invocationBudget, signal: cancellation.signal,
  });
}

async function ensureNpm(directory) {
  if (fs.existsSync(path.join(directory, "node_modules", ".package-lock.json"))) return 0;
  return run(npmCommand, ["ci", "--no-audit", "--no-fund"], { cwd: directory, timeoutMs: 15 * 60 * 1000 });
}

async function runSmoke() {
  let primary = 0;
  await Promise.all(["backend", "frontend"].map(async group => {
    let code;
    try { code = await runSelectedCore(group); }
    catch (error) { console.error(`[Host / ${group}] ${error.message}`); code = error.exitCode ?? 3; }
    if (code !== 0 && primary === 0) {
      primary = code;
      cancellation.abort();
    }
  }));
  return primary;
}

let workspace;
function runSelectedCore(group) {
  const artifactRoot = process.env.NEXUS_TEST_ARTIFACT_ROOT;
  if (!artifactRoot) throw new Error("核心测试需要 NEXUS_TEST_ARTIFACT_ROOT 指向已登记归属的外部测试目录");
  workspace ??= stageWorkspace(projectRoot, artifactRoot, invocationBudget);
  return runCore({ group, policy, policyBytes, workspace, runRoot, runId, budget: invocationBudget, run });
}

async function buildFrontendBundle({typechecked=false}={}) {
  step("前端生产构建");
  let code = await ensureNpm(frontendDir);
  if (code !== 0) return code;
  const bundle = path.join(frontendDir, "dist");
  const stampPath = path.join(executionRoot, ".generated", "frontend-build.hash");
  const sourceHash = () => execFileSync(
    process.execPath,
    [path.join(executionRoot, "tools", "source-hash.mjs"), "--frontend"],
    { cwd: executionRoot, encoding: "utf8", timeout: invocationBudget
      ? Math.max(1, Math.floor(invocationBudget.remainingMs())) : undefined },
  ).trim();
  // 发行候选按 --frontend-ready 读取该指纹；指纹仍匹配时不再改动源码树，
  // 否则候选的干净工作树检查会失败。
  const current = sourceHash();
  if (fs.existsSync(path.join(bundle, "index.html"))
    && fs.existsSync(path.join(bundle, ".vite", "manifest.json"))
    && fs.existsSync(stampPath)
    && fs.readFileSync(stampPath, "utf8").trim() === current) {
    console.error(`[前端] 复用已验证构建：${current}`);
    return 0;
  }
  code = typechecked ? 0 : await run(npmCommand, ["run", "typecheck"], { cwd: frontendDir });
  if (code !== 0) return code;
  code = await run(npmCommand, ["run", "build"], { cwd: frontendDir });
  if (code !== 0) return code;
  fs.mkdirSync(path.dirname(stampPath), { recursive: true });
  fs.writeFileSync(stampPath, `${sourceHash()}\n`, "utf8");
  return 0;
}

/** 发布 asInvoker Test Host：nexus-pipeline.exe 与 wwwroot 放在同一次运行目录内。 */
let publishedTestHost=false, publishedFrontend=false;
async function publishTestHost({ withFrontend = true } = {}) {
  if (publishedTestHost) {
    if(withFrontend&&!publishedFrontend) {
      fs.cpSync(path.join(frontendDir,"dist"),path.join(testHostDir,"wwwroot"),{recursive:true});publishedFrontend=true;
    }
    return 0;
  }
  step("发布 asInvoker Test Host");
  fs.rmSync(testHostDir, { recursive: true, force: true, maxRetries: 120, retryDelay: 250 });
  fs.mkdirSync(testHostDir, { recursive: true });
  const code = await run("dotnet", [
    "publish", "src\\NexusPipeline.csproj", "-c", "Release", "-r", "win-x64", "--self-contained", "false",
    "-p:PublishSingleFile=true", "-p:DebugType=none", "-p:DebugSymbols=false", "-p:NexusTestHost=true",
    "-p:UseSharedCompilation=false", "--disable-build-servers",
    "-o", testHostDir, "--nologo",
  ], { timeoutMs: 20 * 60 * 1000 });
  if (code !== 0) return code;
  const manifestCode = await verifyManifest(path.join(testHostDir, "nexus-pipeline.exe"), "asInvoker");
  if (manifestCode !== 0) return manifestCode;
  if (withFrontend) fs.cpSync(path.join(frontendDir, "dist"), path.join(testHostDir, "wwwroot"), { recursive: true });
  else fs.mkdirSync(path.join(testHostDir, "wwwroot"), { recursive: true });
  fs.mkdirSync(path.join(testHostDir, "plugins"), { recursive: true });
  publishedTestHost=true;publishedFrontend=withFrontend;
  return 0;
}

const batchResults=[];
const batchParentId=runId, batchParentRoot=runRoot;
async function runBatch() {
  if(batchContext.partnerRoot) process.env.NEXUS_PARTNER_ROOT=batchContext.partnerRoot;
  workspace=stageWorkspace(projectRoot,process.env.NEXUS_TEST_ARTIFACT_ROOT,invocationBudget,"Host",{
    dotnetRequired:batchContext.batch.units.some(unit=>unit.kind==="backend"||unit.kind==="partner-jint"||unit.preparations.some(name=>name.includes("test-build"))||unit.id==="host.architecture.backend")});
  executionRoot=workspace.directory;frontendDir=path.join(executionRoot,"frontend");e2eDir=path.join(executionRoot,"tests/e2e");
  let primary=0;
  for(const unit of batchContext.batch.units) {
    const result={id:unit.id,providedObligations:unit.provides,completedCaseIds:[],completedMethodIds:[],completedScenarioIds:[],completedEditorCaseIds:[],
      status:"NOT_RUN",exitCode:4,rawEvidence:[],real:[],substituted:[]};
    batchResults.push(result);
    try {
      invocationBudget.check();
      runId=`${batchParentId}-${unit.id.replaceAll(/[.:]/g,"-")}`;runRoot=resolveTestRunRoot(projectRoot,runId);
      fs.mkdirSync(runRoot,{recursive:true});
      let code=0;
      if(unit.kind==="backend") {
        const selected={caseIds:unit.expectedCaseIds,scenarioIds:unit.expectedScenarioIds,
          filter:[...new Set(unit.expectedCaseIds.map(id=>`FullyQualifiedName=${id.split("(")[0]}`))].join("|"),
          boundaries:{real:["production C# rules and transactions"],substituted:["controlled provider ports"]}};
        code=await runCore({group:"backend.batch",policy:{...policy,groups:{...policy.groups,"backend.batch":selected}},policyBytes,workspace,runRoot,runId,budget:invocationBudget,run});
        const summary=JSON.parse(fs.readFileSync(path.join(runRoot,"backend.batch/summary.json")));
        result.completedCaseIds=summary.scope.completedCaseIds;result.completedScenarioIds=summary.scope.completedScenarioIds;
        result.real=summary.boundaries.real;result.substituted=summary.boundaries.substituted;
      } else if(unit.kind==="frontend") {
        const state=unit.provides.includes("host.frontend.state");
        if(state) {
          code=await runSelectedCore("frontend");
          const summary=JSON.parse(fs.readFileSync(path.join(runRoot,"frontend/summary.json")));
          result.completedCaseIds=summary.scope.completedCaseIds;result.completedScenarioIds=summary.scope.completedScenarioIds;
        } else {code=await ensureNpm(frontendDir);if(!code) code=await run(npmCommand,["run","typecheck"],{cwd:frontendDir});}
        if(!code&&unit.provides.includes("host.frontend.build")) code=await buildFrontendBundle({typechecked:true});
      } else if(unit.kind==="finite") {
        code = await runFinite(unit.groups);
        for (const group of unit.groups) {
          const native = finiteResults.findLast(item => item.group === group);
          if (code || !native || native.exitCode) throw Object.assign(new Error("Missing finite production scenario evidence"), {exitCode:code || 4});
          const value = JSON.parse(fs.readFileSync(native.evidence));
          const scenario = policy.integrationScenarios[`host.integration.${group}`];
          if (value.scenarioId !== scenario || value.status !== "PASS" || value.cleanup !== "complete") throw new Error("Failed finite scenario");
          fs.copyFileSync(native.evidence,path.join(runRoot,`finite-${scenario}.json`));
          result.completedScenarioIds.push(scenario);
        }
      } else if(unit.kind==="partner-jint") {
        const partner=batchContext.partnerRoot;
        const staged=stageWorkspace(partner,process.env.NEXUS_TEST_ARTIFACT_ROOT,invocationBudget,"Plugins");
        batchContext.partnerSource=staged.source;batchContext.partnerFingerprint=staged.sourceFingerprint;
        try {
          code=await run("dotnet",["build","tools/NexusPipeline.TaskProtocolTests","-c","Release","-p:NexusTestHost=true","-p:UseSharedCompilation=false","--disable-build-servers","--nologo"]);
          for(const plugin of unit.partnerPlugins) for(const [index,fixture] of plugin.fixtureIds.entries()) {
            if(code) break;
            invocationBudget.check();
            const report=path.join(runRoot,`${plugin.artifact}-${fixture}.json`);
            const args=[path.join(executionRoot,"bin/test-host/NexusPipeline.TaskProtocolTests/Release/net8.0-windows/NexusPipeline.TaskProtocolTests.dll"),"--plugin-root",staged.directory,
              "--plugin",plugin.artifact,"--scenario",fixture,"--report",report];
            if(index===0) args.push("--observe-count","12");
            code=await run("dotnet",args);
            if(code) break;
            const native=JSON.parse(fs.readFileSync(report));
            if(native.artifact!==plugin.artifact||native.passed!==1||native.failed||native.skipped||JSON.stringify(native.completedCaseIds)!==JSON.stringify([fixture])
              ||index===0&&(native.observeCount!==12||!native.isolatedRunChecked||JSON.stringify(native.editorCaseIds)!==JSON.stringify(plugin.expectedEditorCaseIds))) throw new Error("Invalid production Jint/editor evidence");
            result.completedCaseIds.push(`${plugin.artifact}/${fixture}`);
            if(index===0) result.completedEditorCaseIds.push(...native.editorCaseIds);
          }
          if(!code) result.completedScenarioIds=unit.expectedScenarioIds;
        } finally {if(getProcessRunnerState().cleanupComplete) staged.release();}
      } else {
        code=await runGate(unit.id);
        if(!code&&policy.integrationScenarios?.[unit.id]) {
          const finite=finiteResults.findLast(item=>({execution:"H-E01",config:"H-E02",control:"H-E03",schedule:"H-E04"}[item.group])===policy.integrationScenarios[unit.id]);
          if(!finite||finite.exitCode) throw new Error("Missing finite production scenario evidence");
          const native=JSON.parse(fs.readFileSync(finite.evidence));
          if(native.status!=="PASS"||native.cleanup!=="complete"||native.scenarioId!==policy.integrationScenarios[unit.id]) throw new Error("Failed finite scenario");
          fs.copyFileSync(finite.evidence,path.join(runRoot,"finite-scenario.json"));result.completedScenarioIds=[native.scenarioId];
        }
        if(unit.id==="host.partner-contract") {
          const partner=stageWorkspace(batchContext.partnerRoot,process.env.NEXUS_TEST_ARTIFACT_ROOT,invocationBudget,"Plugins",{dotnetRequired:false});
          batchContext.partnerSource=partner.source;batchContext.partnerFingerprint=partner.sourceFingerprint;partner.release();
        }
      }
      result.exitCode=code;result.status=code?"FAIL":"PASS";

    } catch(error) {result.exitCode=error.exitCode??1;result.status="FAIL";result.failure=error.message;primary ||=result.exitCode;console.error(error.message);}
    finally {
      if(fs.existsSync(path.join(runRoot,"runtime/.nxp/test-run-marker.json"))) {
        const remaining=invocationBudget.remainingMs({cleanup:true});
        const cleanup=remaining>1000?await runProcess(process.execPath,[path.join(projectRoot,"tests/support/cleanup-runtime.mjs"),runRoot,runId],{
          env:process.env,timeoutMs:Math.min(8000,remaining-1000),timeoutCleanupMs:1000}):6;
        if(cleanup) {result.exitCode ||=6;result.status="FAIL";primary ||=6;}
      }
    }
    try {
      const receipt=path.join(runRoot,"unit-receipt.json");
      fs.writeFileSync(receipt,JSON.stringify({unitId:unit.id,provides:unit.provides,exitCode:result.exitCode,status:result.status,source:workspace.source,
        elapsedMs:invocationBudget.elapsedMs,inputPair:batchContext.plan.inputPair??null,type:"typed gate execution receipt"},null,2));
      const destination=path.join(batchParentRoot,"evidence",unit.id.replaceAll(/[.:]/g,"-"));
      const copy=(source,target)=>{
        fs.mkdirSync(target,{recursive:true});
        for(const entry of fs.readdirSync(source,{withFileTypes:true})) {
          const from=path.join(source,entry.name),to=path.join(target,entry.name);
          if(entry.isSymbolicLink()) throw new Error("Linked native output");
          if(entry.isDirectory()&&!["test-host","architecture-bin","architecture-obj","runtime","wwwroot","plugins"].includes(entry.name)) copy(from,to);
          else if(entry.isFile()&&/\.(json|trx|tap|log|txt)$/.test(entry.name)) {
            fs.copyFileSync(from,to);result.rawEvidence.push(path.relative(batchParentRoot,to).replaceAll("\\","/"));
          }
        }
      };copy(runRoot,destination);

    } catch(error) { result.exitCode ||= 4; result.status="FAIL"; result.failure ||= error.message; primary ||= result.exitCode; }
    primary ||= result.exitCode;
  }
  runId=batchParentId;runRoot=batchParentRoot;
  return primary;
}

function verifyManifest(executable, expectedLevel) {
  return run(pythonCommand, ["tools\\pe_manifest.py", "--exe", executable, "--expected-level", expectedLevel]);
}

function testHostEnvironment({ runtimeName, webPort, exitFile }) {
  const env = { ...process.env };
  Object.assign(env, {
    NEXUS_TEST_MODE: "test-host",
    NEXUS_TEST_HOST: "1",
    NEXUS_TEST_HOST_DIR: testHostDir,
    NEXUS_TEST_HOST_EXIT_FILE: exitFile,
    NEXUS_SYSTEM_SMOKE: "1",
    NEXUS_SYSTEM_ACTION_DRYRUN: "1",
    NEXUS_TIME_SCALE: "10",
    NEXUS_TEST_RUN_ID: runId,
    NEXUS_SYSTEM_RUNTIME_NAME: runtimeName,
    NEXUS_SYSTEM_WEB_PORT: String(webPort),
  });
  return env;
}

async function runUiSmoke() {
  step("UI Smoke（Edge，单浏览器会话）");
  const code = await ensureNpm(e2eDir);
  if (code !== 0) return code;
  const webPort = await findAvailablePort();
  const env = testHostEnvironment({
    runtimeName: "ui",
    webPort,
    exitFile: path.join(runRoot, "ui", ".nxp", "test-host.exit"),
  });
  env.NEXUS_E2E_BASE_URL = `http://127.0.0.1:${webPort}/`;
  // Playwright 产物放在本次运行目录内，源码树保持干净。
  env.NEXUS_E2E_OUTPUT_DIR = path.join(runRoot, "ui", "playwright");
  return run(process.execPath, [path.join(e2eDir, "node_modules", "playwright", "cli.js"), "test"], { cwd: e2eDir, env, timeoutMs: 15 * 60 * 1000 });
}

async function runSystemSuite(suite) {
  const webPort = await findAvailablePort();
  const env = testHostEnvironment({
    runtimeName: suite.runtimeName,
    webPort,
    exitFile: path.join(runRoot, suite.runtimeName, ".nxp", "test-host.exit"),
  });
  console.error(`\n-- System Smoke ${suite.file} (port=${webPort}) --`);
  return run(process.execPath, ["--test", "--test-concurrency=1", ...(batchContext?["--test-reporter=tap",`--test-reporter-destination=${path.join(runRoot,"runtime-native.tap")}`]:[]), suite.file], { env, timeoutMs: 15 * 60 * 1000 });
}

async function runSystemSmoke() {
  step("System Smoke");
  for (const suite of [
    { file: "tests/system/runtime-smoke.mjs", runtimeName: "runtime" },
    { file: "tests/system/judge-smoke.mjs", runtimeName: "judge-runtime" },
  ]) {
    const code = await runSystemSuite(suite);
    if (code !== 0) return code;
  }
  return 0;
}

async function runIntegration() {
  workspace ??= stageWorkspace(projectRoot, process.env.NEXUS_TEST_ARTIFACT_ROOT, invocationBudget);
  executionRoot = workspace.directory;
  frontendDir = path.join(executionRoot, "frontend");
  e2eDir = path.join(executionRoot, "tests", "e2e");
  let code = await buildFrontendBundle();
  if (code !== 0) return code;
  code = await publishTestHost();
  if (code !== 0) return code;
  code = await runUiSmoke();
  if (code !== 0) return code;
  return runSystemSmoke();
}

async function prepareTestHostUi() {
  const codes = await Promise.all([buildFrontendBundle(), publishTestHost({ withFrontend: false })]);
  if (codes.some(code => code)) return codes.find(code => code);
  return publishTestHost({ withFrontend: true });
}

const finiteGroups = ["schedule", "execution", "config", "control"];
const finiteResults = [];
async function runFinite(selectedGroup) {
  workspace ??= stageWorkspace(projectRoot, process.env.NEXUS_TEST_ARTIFACT_ROOT, invocationBudget);
  executionRoot = workspace.directory;
  frontendDir = path.join(executionRoot, "frontend");
  e2eDir = path.join(executionRoot, "tests/e2e");
  const selected = selectedGroup ? (Array.isArray(selectedGroup) ? selectedGroup : [selectedGroup]) : [...finiteGroups];
  if (!selected.length || new Set(selected).size !== selected.length || selected.some(group => !finiteGroups.includes(group))) throw new Error("Unknown or duplicate finite group");
  const browserRequired = selected.includes("execution");
  if (browserRequired) {
    const codes = await Promise.all([prepareTestHostUi(), ensureNpm(e2eDir)]);
    if (codes.some(code => code)) return codes.find(code => code);
  } else {
    const code = await publishTestHost({ withFrontend: false }); if (code) return code;
  }
  if (selected.includes("control")) {
    const code = await run("dotnet", ["build", "tests/fixtures/NexusPipeline.TestPlugin/NexusPipeline.TestPlugin.csproj",
      "-c", "Release", "-p:NexusTestHost=true", "-p:UseSharedCompilation=false", "--disable-build-servers", "--nologo"]);
    if (code) return code;
  }
  const queue = [...selected];
  let code = 0;
  await Promise.all(Array.from({ length: Math.min(2, queue.length) }, async () => {
    while (queue.length && !code) {
      const group = queue.shift();
      const file = path.join(executionRoot, `tests/system/finite-${group}.mjs`);
      if (!fs.existsSync(file)) { code = 7; console.error(`Missing finite scenario: ${group}`); break; }
      const childId = `${runId}-${group}`;
      const childRoot = path.join(process.env.NEXUS_TEST_ARTIFACT_ROOT, "runs", childId);
      fs.mkdirSync(childRoot, { recursive: true });
      const output = path.join(childRoot, "evidence.json");
      const env = testHostEnvironment({ runtimeName: "runtime", webPort: await findAvailablePort(),
        exitFile: path.join(childRoot, "runtime/.nxp/test-host.exit") });
      Object.assign(env, { NEXUS_TEST_RUN_ID: childId, NEXUS_FINITE_RESULT: output });
      if (group === "control") {
        const plan = path.join(childRoot, "http-plan.json");
        const prepared = await run(pythonCommand, ["tests/system/store-fixture.py",
          path.join(executionRoot, "bin/test-host/NexusPipeline.TestPlugin/Release/net8.0/NexusPipeline.TestPlugin.dll"), plan, childId, "--upgrade"]);
        if (prepared) { code = prepared; break; }
        env.NEXUS_TEST_HTTP_PLAN = plan;
      }
      let childCode = await run(process.execPath, [file], { env });
      const remaining = invocationBudget.remainingMs({ cleanup: true });
      const cleanupCode = remaining > 1000 ? await runProcess(process.execPath,
        [path.join(projectRoot, "tests/support/cleanup-runtime.mjs"), childRoot, childId],
        { env, timeoutMs: Math.min(5000, remaining - 1000), timeoutCleanupMs: 1000 }) : 6;
      childCode ||= cleanupCode;
      if (!childCode) {
        const evidence = JSON.parse(fs.readFileSync(output));
        const scenario = { execution: "H-E01", config: "H-E02", control: "H-E03", schedule: "H-E04" }[group];
        if (evidence.scenarioId !== scenario || evidence.runId !== childId
            || evidence.status !== "PASS" || evidence.cleanup !== "complete") childCode = 4;
      }
      finiteResults.push({ group, exitCode: childCode, evidence: output });
      code ||= childCode;
    }
  }));
  return code;
}

async function runStoreDiagnostic() {
  workspace ??= stageWorkspace(projectRoot, process.env.NEXUS_TEST_ARTIFACT_ROOT, invocationBudget);
  executionRoot = workspace.directory;
  frontendDir = path.join(executionRoot, "frontend");
  let code = await publishTestHost({ withFrontend: false });
  if (code !== 0) return code;
  code = await run("dotnet", ["build", "tests/fixtures/NexusPipeline.TestPlugin/NexusPipeline.TestPlugin.csproj",
    "-c", "Release", "-p:NexusTestHost=true", "-p:UseSharedCompilation=false", "--disable-build-servers", "--nologo"]);
  if (code !== 0) return code;
  const plan = path.join(runRoot, "http-plan.json");
  code = await run(pythonCommand, ["tests/system/store-fixture.py",
    path.join(executionRoot, "bin/test-host/NexusPipeline.TestPlugin/Release/net8.0/NexusPipeline.TestPlugin.dll"), plan, runId]);
  if (code !== 0) return code;
  const env = testHostEnvironment({ runtimeName: "runtime", webPort: await findAvailablePort(),
    exitFile: path.join(runRoot, "runtime", ".nxp", "test-host.exit") });
  env.NEXUS_TEST_HTTP_PLAN = plan;
  const report = path.join(runRoot, "store-native.tap");
  code = await run(process.execPath, ["--test", "--test-concurrency=1", "--test-reporter=tap",
    `--test-reporter-destination=${report}`, "tests/system/store-core.mjs"], { env });
  if (code === 0) {
    const native = fs.readFileSync(report, "utf8");
    for (const [counter, count] of Object.entries({ tests: 1, pass: 1, fail: 0, cancelled: 0, skipped: 0, todo: 0 })) {
      if (!new RegExp(`^# ${counter} ${count}\\r?$`, "m").test(native)) return 4;
    }
  }
  fs.writeFileSync(path.join(runRoot, "store-evidence.json"), JSON.stringify({
    evidenceType: "actual", scope: "diagnostic store installation lifecycle; partial H-C09",
    runId, source: workspace.source, exitCode: code, elapsedMs: invocationBudget.elapsedMs,
    boundaries: { real: ["Host HTTP API", "catalog and package policy", "SHA256", "ZIP", "installation transaction",
      "managed plugin loading", "restart handoff", "uninstall"], substituted: ["external HTTPS responses", "synthetic fixture plugin"] },
    rawReport: report,
  }, null, 2));
  return code;
}

async function runGate(id) {
  const { registry } = readRegistry(projectRoot);
  const gate = registry.gates.find(item => item.id === id);
  if (!gate || gate.kind === "aggregate" || gate.kind === "scope" || gate.kind === "release")
    throw new Error(`Unknown or non-executable Host gate: ${id}`);
  fs.mkdirSync(runRoot, { recursive: true });
  workspace ??= stageWorkspace(projectRoot, process.env.NEXUS_TEST_ARTIFACT_ROOT, invocationBudget);
  executionRoot = workspace.directory;
  frontendDir = path.join(executionRoot, "frontend");
  e2eDir = path.join(executionRoot, "tests/e2e");
  if (id.startsWith("host.backend.")) return runSelectedCore(id.slice("host.".length));
  if (id === "host.frontend.state") return runSelectedCore("frontend");
  if (id === "host.frontend.typecheck") {
    let code = await ensureNpm(frontendDir);
    return code || run(npmCommand, ["run", "typecheck"], { cwd: frontendDir });
  }
  if (id === "host.frontend.build") return buildFrontendBundle();
  if (id === "host.build.test-host") {
    const withFrontend = !batchContext || batchContext.batch.units.some(unit => unit.id === "host.integration.execution" || unit.kind === "finite" && unit.groups.includes("execution"));
    return withFrontend ? prepareTestHostUi() : publishTestHost({ withFrontend: false });
  }
  if (id === "host.ci-policy") {
    let code = await run(process.execPath, ["--test", "tests/scope-plan.test.mjs", "tests/core-plan.test.mjs", "tests/batch-plan.test.mjs", "tests/support/budget.test.mjs", "tests/support/test-runtime.test.mjs"]);
    if (code) return code;
    return run(pythonCommand, ["-m", "unittest", "discover", "-s", "tests", "-p", "test_*.py", "-v"]);
  }
  if (id === "host.docs") {
    const required = ["README.md", "docs/user/README.md", "docs/user/README.en.md"];
    if (required.some(name => !fs.existsSync(path.join(executionRoot, name)))) return 4;
    const checked=await run(process.execPath,["tools/check-doc-links.mjs"]);
    if(checked) return checked;
    fs.writeFileSync(path.join(runRoot, "docs-manifest.json"), JSON.stringify({ files: required,
      source: workspace.source }, null, 2));
    return 0;
  }
  if (id === "host.release-contract")
    return run(pythonCommand, ["-m", "unittest", "tools.tests.test_host_release", "-v"]);
  if (id === "host.installer-localization")
    return run(pythonCommand, ["-m", "unittest", "tools.tests.test_host_installer", "-v"]);
  if (id === "host.architecture.backend") {
    const project = "tools/NexusPipeline.Architecture/NexusPipeline.Architecture.csproj";
    const output = path.join(runRoot, "architecture-bin") + path.sep;
    const intermediate = path.join(runRoot, "architecture-obj") + path.sep;
    let code = await run("dotnet", ["build", project, "-c", "Release", `-p:BaseOutputPath=${output}`,
      `-p:BaseIntermediateOutputPath=${intermediate}`, "-p:UseSharedCompilation=false", "--disable-build-servers", "--nologo"]);
    if (code) return code;
    // Roslyn compiles both modes; restore prepares their separate reference graphs.
    for (const testHost of [true, false]) {
      code = await run("dotnet", ["restore", "src/NexusPipeline.csproj", "-r", "win-x64",
        `-p:NexusTestHost=${testHost}`, "--nologo"]);
      if (code) return code;
    }
    const checker = path.join(output, "Release", "net8.0", "NexusPipeline.Architecture.dll");
    code = await run("dotnet", [checker, executionRoot, "--report", path.join(runRoot, "architecture-backend.json")]);
    if (code) return code;
    return run(pythonCommand, ["tests/architecture-fixture.py", checker]);
  }
  if (id === "host.architecture.frontend") {
    const code = await ensureNpm(frontendDir);
    if (code) return code;
    const report = path.join(runRoot, "architecture-frontend.json");
    const checked = await run(process.execPath, ["scripts/architecture-check.mjs", frontendDir, "--report", report],
      { cwd: frontendDir });
    return checked || run(process.execPath, ["--test", "scripts/architecture-check.test.mjs"], { cwd: frontendDir });
  }
  if (id === "host.integration.store") return runStoreDiagnostic();
  if (id === "host.integration.restart-update") {
    const code = await publishTestHost({ withFrontend: false });
    return code || runSystemSuite({ file: "tests/system/runtime-smoke.mjs", runtimeName: "runtime" });
  }
  const finite = {
    "host.integration.execution": "execution", "host.integration.config": "config",
    "host.integration.control": "control", "host.integration.schedule": "schedule",
  }[id];
  if (finite) return runFinite(finite);
  if (id === "host.partner-contract") {
    const partner = process.env.NEXUS_PARTNER_ROOT;
    if (!partner || !path.isAbsolute(partner)) throw new Error("Fixed partner checkout is required");
    return run(pythonCommand, ["tests/partner-contract.py", "--plugins-root", partner,
      "--host-root", executionRoot, "--report", path.join(runRoot, "partner-contract.json")]);
  }
  throw new Error(`Gate has no execution contract: ${id}`);
}

/** 生产构建只发布程序自有文件，release/ 下的用户运行数据保持原样。 */
async function runRelease() {
  step("生产构建");
  let code = await buildFrontendBundle();
  if (code !== 0) return code;
  const releaseDir = path.join(projectRoot, "release");
  fs.mkdirSync(releaseDir, { recursive: true });
  fs.rmSync(path.join(releaseDir, "wwwroot"), { recursive: true, force: true, maxRetries: 120, retryDelay: 250 });
  code = await run("dotnet", [
    "publish", "src\\NexusPipeline.csproj", "-c", "Release", "-r", "win-x64", "--self-contained", "false",
    "-p:PublishSingleFile=true", "-p:DebugType=none", "-p:DebugSymbols=false", "-o", releaseDir, "--nologo",
  ], { timeoutMs: 20 * 60 * 1000 });
  if (code !== 0) return code;
  fs.cpSync(path.join(frontendDir, "dist"), path.join(releaseDir, "wwwroot"), { recursive: true });
  fs.mkdirSync(path.join(releaseDir, "plugins"), { recursive: true });
  return verifyManifest(path.join(releaseDir, "nexus-pipeline.exe"), "requireAdministrator");
}

function printUsage() {
  console.error("用法：node tests\\run.mjs ci --group backend|frontend；daily [--group execution|config|control|schedule]；diagnostic --group store；smoke|integration|release；list --json");
  console.error("  smoke       并行核心 xUnit 与前端类型/核心 Vitest，共享 180 秒预算");
  console.error("  integration 发布 asInvoker Test Host，运行 UI Smoke 与 System Smoke");
  console.error("  daily       两个 Host 槽位运行四条有限真实 E2E，共享 180 秒预算");
  console.error("  release     生产 requireAdministrator 构建与内嵌清单校验");
}

const [command = ""] = process.argv.slice(2);
const commands = { smoke: runSmoke, daily: () => runFinite(), integration: runIntegration, release: runRelease };
let exitCode = 2;
let gateId = null;
resetProcessRunnerState();
try {
  const selected = commands[command.toLowerCase()];
  if(command==="batch") {exitCode=await runBatch();}
  else if (command === "list" && process.argv.length === 4 && process.argv[3] === "--json") {
    console.log(JSON.stringify({ budgetMs: policy.invocationBudgetMs, groups: Object.fromEntries(
      Object.entries(policy.groups).map(([name, group]) => [name, {
        scenarioIds: group.scenarioIds, caseCount: group.caseIds.length, boundaries: group.boundaries,
      }])) , daily: { groups: finiteGroups, scenarioIds: ["H-E01", "H-E02", "H-E03", "H-E04"], hostSlots: 2 } }, null, 2));
    exitCode = 0;
  } else if (command === "ci" && process.argv.length === 5 && process.argv[3] === "--group"
      && Object.hasOwn(policy.groups, process.argv[4])) {
    exitCode = await runSelectedCore(process.argv[4]);
  } else if (command === "gate" && process.argv.length === 5 && process.argv[3] === "--id") {
    gateId = process.argv[4];
    exitCode = await runGate(gateId);
  } else if (command === "diagnostic" && process.argv.length === 5 && process.argv[3] === "--group" && process.argv[4] === "store") {
    exitCode = await runStoreDiagnostic();
  } else if (command === "daily" && process.argv.length === 5 && process.argv[3] === "--group" && finiteGroups.includes(process.argv[4])) {
    exitCode = await runFinite(process.argv[4]);
  } else if (!selected) {
    printUsage();
  } else if (process.argv.length !== 3) {
    printUsage();
  } else {
    exitCode = await selected();
    if (invocationBudget?.remainingMs({ cleanup: true }) === 0) exitCode = exitCode || 5;
  }
} catch (error) {
  console.error(`[错误] ${error.stack || error.message}`);
  exitCode = 1;
} finally {
  if (["integration", "diagnostic"].includes(command.toLowerCase())
      || ["host.integration.store", "host.integration.restart-update"].includes(gateId)) {
    if (!fs.existsSync(runRoot)) fs.mkdirSync(runRoot, { recursive: true });
    const cleanupMs = Math.min(8_000, invocationBudget.remainingMs({ cleanup: true }) - 1_000);
    if (cleanupMs <= 0) {
      console.error("[清理] 命令剩余预算不足，保留运行现场");
      exitCode ||= 6;
    } else {
      const cleanupCode = await runProcess(process.execPath, [
        path.join(projectRoot, "tests", "support", "cleanup-runtime.mjs"), runRoot, runId,
      ], { defaultCwd: projectRoot, timeoutMs: cleanupMs, timeoutCleanupMs: 1_000,
        env: { ...process.env, NEXUS_TEST_MODE: "test-host" } });
      if (cleanupCode !== 0) exitCode ||= 6;
    }
  }
  process.removeListener("SIGINT", cancel);
  process.removeListener("SIGTERM", cancel);
  const runnerState = getProcessRunnerState();
  if (!runnerState.cleanupComplete) {
    console.error(`[清理] 进程树清理未确认完成：${runnerState.cleanupFailures.join("；")}`);
    exitCode = exitCode === 0 ? 6 : exitCode;
  } else if (workspace) {
    workspace.release();
  }
  if (invocationBudget && invocationBudget.elapsedMs+setupElapsedMs > policy.qualificationMs) exitCode ||= 5;
  if (invocationBudget?.remainingMs({ cleanup: true }) === 0) exitCode ||= 5;
  if(batchContext) saveBatch(batchContext,batchParentRoot,workspace,batchResults,exitCode,invocationBudget.elapsedMs,getProcessRunnerState());
  const storeEvidence = path.join(runRoot, "store-evidence.json");
  if (command === "diagnostic" && fs.existsSync(storeEvidence)) {
    const evidence = JSON.parse(fs.readFileSync(storeEvidence, "utf8"));
    fs.writeFileSync(storeEvidence, JSON.stringify({ ...evidence, exitCode,
      elapsedMs: invocationBudget.elapsedMs, cleanup: getProcessRunnerState(),
      status: exitCode === 0 ? "PASS" : "FAIL" }, null, 2));
  }
  if (command === "daily") {
    fs.writeFileSync(path.join(runRoot, "daily-evidence.json"), JSON.stringify({ evidenceType: "actual",
      source: workspace?.source, runId, groups: finiteResults, budgetMs: policy.invocationBudgetMs,
      elapsedMs: invocationBudget.elapsedMs, exitCode, status: exitCode ? "FAIL" : "PASS",
      cleanup: getProcessRunnerState() }, null, 2));
  }
  if (gateId) {
    fs.mkdirSync(runRoot, { recursive: true });
    const { registry, digest } = readRegistry(projectRoot);
    const gate = registry.gates.find(item => item.id === gateId);
    const group = gateId.startsWith("host.backend.") ? gateId.slice("host.".length)
      : gateId === "host.frontend.state" ? "frontend" : null;
    const nativePath = group ? path.join(runRoot, group, "summary.json") : null;
    const native = nativePath && fs.existsSync(nativePath) ? JSON.parse(fs.readFileSync(nativePath, "utf8")) : null;
    const partnerRoot = process.env.NEXUS_PARTNER_ROOT;
    const partner = gate?.partnerRequired && partnerRoot && path.isAbsolute(partnerRoot)
      ? { commitSha: execFileSync("git", ["-C", partnerRoot, "rev-parse", "HEAD"], { encoding: "utf8" }).trim(),
        workingTreeDirty: Boolean(execFileSync("git", ["-C", partnerRoot, "status", "--porcelain"],
          { encoding: "utf8" }).trim()) } : null;
    if (invocationBudget.elapsedMs > policy.qualificationMs) exitCode ||= 5;
    const report = {
      schemaVersion: 1, scope: "LOCAL_GATE", gateId, kind: gate?.kind ?? null, runId,
      source: workspace?.source ?? null, partner, policyDigest: digest,
      digestFormat: "utf8-lf-v1", selectedCases: native?.scope.expectedCaseIds ?? null,
      completedCases: native?.scope.completedCaseIds ?? null, counts: native?.counts ?? null,
      status: exitCode === 0 ? "PASS" : "FAIL", exitCode,
      timing: { qualificationMs: policy.qualificationMs, hardTimeoutMs: policy.invocationBudgetMs,
        localElapsedMs: invocationBudget.elapsedMs, actualJobMs: null },
      cleanup: getProcessRunnerState(), nativeReport: nativePath && fs.existsSync(nativePath) ? nativePath : null,
    };
    fs.writeFileSync(path.join(runRoot, "gate-report.json"), `${JSON.stringify(report, null, 2)}\n`);
  }
}
process.exitCode = exitCode;
