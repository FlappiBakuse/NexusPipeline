import { createHash } from "node:crypto";
import { execFileSync } from "node:child_process";
import fs from "node:fs";
import path from "node:path";
import { fileURLToPath } from "node:url";
import { globToRegExp } from "../tools/path-glob.mjs";
import { parseTapResults, parseTrxResults, parseVitestResults, parsePlaywrightResults } from "../tools/test-results.mjs";
import { getIntegrityLevel } from "./support/windows-process.mjs";
import { getProcessRunnerState, resetProcessRunnerState, runProcess as runOwnedProcess } from "./support/process-runner.mjs";
import { findAvailablePort } from "./support/test-runtime.mjs";
import { gateSequence, runtimePolicy } from "./support/runtime-policy.mjs";
import { FRONTEND_TEST_GROUPS, GOVERNANCE_DOMAINS, HOST_TEST_AREAS, SYSTEM_TEST_GROUPS, systemRuntimeName, validateRegistry } from "./registry.mjs";

validateRegistry();

const projectRoot = path.resolve(path.dirname(fileURLToPath(import.meta.url)), "..");
const frontendDir = path.join(projectRoot, "frontend");
const e2eDir = path.join(projectRoot, "tests", "e2e");
const systemDir = path.join(projectRoot, "tests", "system");
const toolsDir = path.join(projectRoot, "tools");
const testsDir = path.join(projectRoot, "tests");
const nodeCommand = process.execPath;
const pythonCommand = process.platform === "win32" ? "python" : "python3";
const playwrightCli = path.join(e2eDir, "node_modules", "playwright", "cli.js");
const npmCommand = process.platform === "win32" ? "npm.cmd" : "npm";
const runId = process.env.NEXUS_TEST_RUN_ID?.trim()
  || `run-${Date.now()}-${process.pid}-${Math.random().toString(36).slice(2, 8)}`;
const runRoot = path.join(projectRoot, "tests", ".artifacts", "runs", runId);
const testHostDir = path.join(runRoot, "test-host");
const emulatorFixturePluginDir = path.join(runRoot, "emulator-fixture");
const reportRoot = path.join(runRoot, "reports");
const frontendBuildStamp = path.join(projectRoot, ".generated", "frontend-build.hash");
const RELEASE_GATE_GROUPS = gateSequence("all");
const RELEASE_GROUPS = [...RELEASE_GATE_GROUPS, "all"];
const MODE_SUITES = new Set(["default", "ui", "system", "all"]);
let reportSequence = 0;

const buildPromises = new Map();
const preparedNpmWorkspaces = new Map();
const preparedGateDependencies = new Set();
let officialPluginsRoot = null;

function normalizePath(file) {
  return path.relative(projectRoot, file).replaceAll("\\", "/");
}

function candidateSha() {
  const supplied = process.env.GITHUB_SHA?.trim();
  if (/^[0-9a-f]{40}$/iu.test(supplied || "")) return supplied.toLowerCase();
  try {
    const resolved = execFileSync("git", ["rev-parse", "HEAD"], {
      cwd: projectRoot,
      encoding: "utf8",
      stdio: ["ignore", "pipe", "ignore"],
    }).trim();
    if (/^[0-9a-f]{40}$/iu.test(resolved)) return resolved.toLowerCase();
  } catch {
    // The architecture gate below still reports the command failure; metadata
    // must never silently claim an unbound candidate.
  }
  throw new Error("无法解析本次架构候选的 Git SHA");
}

function sha256File(file) {
  return createHash("sha256").update(fs.readFileSync(file)).digest("hex");
}

function recursiveFiles(directory) {
  if (!fs.existsSync(directory)) return [];
  const files = [];
  for (const entry of fs.readdirSync(directory, { withFileTypes: true })) {
    if (["node_modules", "dist", "bin", "obj", ".git"].includes(entry.name) || entry.name.startsWith(".nxp")) continue;
    const fullPath = path.join(directory, entry.name);
    if (entry.isDirectory()) files.push(...recursiveFiles(fullPath));
    else files.push(fullPath);
  }
  return files;
}

function matchingFiles(patterns, root = projectRoot) {
  return recursiveFiles(root)
    .filter(file => patterns.some(pattern => globToRegExp(pattern).test(normalizePath(file))))
    .sort();
}

function frontendTestFiles(groups = []) {
  const patterns = groups.length === 0
    ? ["frontend/src/**/*.test.ts"]
    : FRONTEND_TEST_GROUPS.filter(group => groups.includes(group.key)).flatMap(group => group.testPaths);
  return matchingFiles(patterns);
}

function unitTestFiles(groups = []) {
  const unitDir = path.join(testsDir, "NexusPipeline.Tests");
  const candidates = recursiveFiles(unitDir).filter(file => file.endsWith("Tests.cs")).sort();
  if (groups.length === 0) return candidates;
  const patterns = HOST_TEST_AREAS.filter(area => groups.includes(area.key))
    .flatMap(area => area.testPaths)
    .map(pattern => globToRegExp(`tests/NexusPipeline.Tests/${pattern}`));
  return candidates.filter(file => patterns.some(pattern => pattern.test(normalizePath(file))));
}

function syntaxFiles() {
  return [...matchingFiles(["tests/**/*.mjs"]), ...matchingFiles(["tools/**/*.mjs"])].sort();
}

function systemSuites(groups = []) {
  const selected = groups.length ? new Set(groups) : null;
  const suites = [];
  for (const group of SYSTEM_TEST_GROUPS) {
    if (selected && !selected.has(group.key)) continue;
    for (let index = 0; index < group.suitePaths.length; index++) {
      const file = path.join(projectRoot, group.suitePaths[index]);
      if (!fs.existsSync(file)) throw new Error(`System suite 不存在：${group.suitePaths[index]}`);
      suites.push({ group: group.key, runtimeName: group.runtimeNames[index], file });
    }
  }
  return suites;
}

function runProcess(command, args, options = {}) {
  return runOwnedProcess(command, args, { defaultCwd: projectRoot, ...options });
}

async function ensureNpmWorkspace(directory) {
  const workspace = path.resolve(directory);
  const manifest = path.join(workspace, "package.json");
  const lockfile = path.join(workspace, "package-lock.json");
  if (!fs.existsSync(manifest) || !fs.existsSync(lockfile)) {
    console.error(`[依赖] 缺少 package.json 或 package-lock.json：${workspace}`);
    return 1;
  }
  const fingerprint = createHash("sha256")
    .update(`npm-ci-v1\0${process.platform}\0${process.arch}\0${process.version}\0`)
    .update(fs.readFileSync(manifest))
    .update("\0")
    .update(fs.readFileSync(lockfile))
    .digest("hex");
  const modules = path.join(workspace, "node_modules");
  const stamp = path.join(modules, ".nxp-install.hash");
  if (fs.existsSync(modules) && fs.lstatSync(modules).isSymbolicLink()) {
    console.error(`[依赖] node_modules 不得是符号链接：${modules}`);
    return 1;
  }
  const ready = fs.existsSync(path.join(modules, ".package-lock.json"))
    && fs.existsSync(stamp)
    && fs.readFileSync(stamp, "utf8").trim() === fingerprint;
  if (preparedNpmWorkspaces.get(workspace) === fingerprint && ready) return 0;
  if (ready) {
    console.error(`[依赖] 复用已安装工作区：${workspace}`);
    preparedNpmWorkspaces.set(workspace, fingerprint);
    return 0;
  }
  const code = await runProcess(npmCommand, ["ci", "--no-audit", "--no-fund"], {
    cwd: workspace,
    timeoutMs: 15 * 60 * 1000,
  });
  if (code !== 0) {
    console.error(`[依赖] npm ci 失败：${workspace}`);
    return code;
  }
  if (!fs.existsSync(path.join(modules, ".package-lock.json"))) {
    console.error(`[依赖] npm ci 未生成完整安装记录：${workspace}`);
    return 1;
  }
  const temporaryStamp = `${stamp}.${process.pid}.tmp`;
  fs.writeFileSync(temporaryStamp, `${fingerprint}\n`, "utf8");
  fs.renameSync(temporaryStamp, stamp);
  preparedNpmWorkspaces.set(workspace, fingerprint);
  return 0;
}

function resolveOfficialPluginsRoot() {
  const configured = process.env.NEXUS_OFFICIAL_PLUGINS_ROOT?.trim();
  if (!configured) throw new Error("frontend-contract 必须显式设置 NEXUS_OFFICIAL_PLUGINS_ROOT，禁止猜测官方 Plugins 根目录。");
  const candidate = path.resolve(configured);
  if (!fs.existsSync(path.join(candidate, "package-lock.json"))) {
    throw new Error(`官方 Plugins 根目录缺少 package-lock.json：${candidate}`);
  }
  if (!fs.existsSync(path.join(candidate, "tools", "Test-FrontendPlugins.mjs"))) {
    throw new Error(`官方 Plugins 根目录缺少 tools/Test-FrontendPlugins.mjs：${candidate}`);
  }
  return candidate;
}

async function prepareGateDependencies(group) {
  if (preparedGateDependencies.has(group)) return 0;
  const workspaces = [frontendDir, toolsDir];
  if (group === "frontend-contract") {
    officialPluginsRoot = officialPluginsRoot || resolveOfficialPluginsRoot();
    workspaces.push(officialPluginsRoot);
  }
  if (group === "ui-runtime") workspaces.push(e2eDir);
  if (group === "execution-emulator" || group === "update-acceptance") {
    workspaces.splice(0, workspaces.length, frontendDir);
  }
  for (const workspace of [...new Set(workspaces)]) {
    const code = await ensureNpmWorkspace(workspace);
    if (code !== 0) return code;
  }
  if (group === "ui-runtime") {
    const code = await runProcess(nodeCommand, [playwrightCli, "install", "chromium"], {
      cwd: e2eDir,
      timeoutMs: 15 * 60 * 1000,
    });
    if (code !== 0) return code;
  }
  preparedGateDependencies.add(group);
  return 0;
}

async function runReported(command, args, options = {}, format = "tap", context = {}) {
  reportSequence += 1;
  const reportDir = path.join(reportRoot, `${String(reportSequence).padStart(3, "0")}-${format}`);
  fs.mkdirSync(reportDir, { recursive: true });
  const reportFile = path.join(reportDir, format === "trx" ? "results.trx" : "results.json");
  const env = { ...(options.env || process.env) };
  const reportedArgs = [...args];
  let output = "";
  if (format === "tap") reportedArgs.splice(1, 0, "--test-reporter=tap");
  if (format === "trx") reportedArgs.push("--logger", "trx;LogFileName=results.trx", "--results-directory", reportDir);
  if (format === "vitest") reportedArgs.push("--reporter=default", "--reporter=json", `--outputFile=${reportFile}`);
  if (format === "playwright") {
    reportedArgs.push("--reporter=line,json");
    env.PLAYWRIGHT_JSON_OUTPUT_NAME = reportFile;
  }
  const code = await runProcess(command, reportedArgs, {
    ...options,
    env,
    onOutput: chunk => { output = (output + chunk).slice(-524288); },
  });
  try {
    const parseOptions = { expectedFiles: context.expectedFiles || [], invokedFiles: context.invokedFiles || [] };
    const result = format === "tap"
      ? parseTapResults(output, parseOptions)
      : format === "trx"
        ? parseTrxResults(fs.readFileSync(reportFile, "utf8"), parseOptions)
        : format === "vitest"
          ? parseVitestResults(JSON.parse(fs.readFileSync(reportFile, "utf8")), parseOptions)
          : parsePlaywrightResults(JSON.parse(fs.readFileSync(reportFile, "utf8")), parseOptions);
    fs.writeFileSync(path.join(reportDir, "stdout.log"), output, "utf8");
    console.error(`[测试结果] ${format}: passed=${result.passed} failed=${result.failed} skipped=${result.skipped}`);
    if (code !== 0 || result.testCount <= 0 || result.failed !== 0 || result.skipped !== 0) return code || 1;
    return 0;
  } catch (error) {
    fs.writeFileSync(path.join(reportDir, "stdout.log"), output, "utf8");
    console.error(`[测试结果] ${format} 报告无效：${error.message}`);
    return code || 1;
  }
}

async function runUnit(groups = []) {
  const selectedFiles = unitTestFiles(groups);
  if (groups.length > 0 && selectedFiles.length === 0) {
    console.error(`[Unit] 选中的 Area 没有可运行测试：${groups.join(", ")}`);
    return 1;
  }
  const args = ["test", "tests\\NexusPipeline.Tests\\NexusPipeline.Tests.csproj", "--nologo", "-m:1", "-p:NexusTestHost=true", "-p:DebugType=none"];
  if (groups.length > 0) {
    const names = [...new Set(selectedFiles.map(file => path.basename(file, ".cs")))];
    args.push("--filter", names.map(name => `FullyQualifiedName~${name}`).join("|"));
  }
  return runReported("dotnet", args, { timeoutMs: 10 * 60 * 1000 }, "trx", {
    expectedFiles: selectedFiles.map(normalizePath),
    invokedFiles: selectedFiles.map(normalizePath),
  });
}

async function runFrontend(groups = []) {
  let code = await ensureNpmWorkspace(frontendDir);
  if (code !== 0) return code;
  code = await runProcess(npmCommand, ["run", "typecheck"], { cwd: frontendDir });
  if (code !== 0) return code;
  const selectedFiles = frontendTestFiles(groups);
  if (groups.length > 0 && selectedFiles.length === 0) {
    console.error(`[Frontend] 选中的分组没有可运行测试：${groups.join(", ")}`);
    return 1;
  }
  const testArgs = ["run", "test", "--", "--run"];
  if (groups.length > 0) testArgs.push(...selectedFiles.map(file => path.relative(frontendDir, file)));
  code = await runReported(npmCommand, testArgs, { cwd: frontendDir }, "vitest", {
    expectedFiles: selectedFiles.map(normalizePath),
    invokedFiles: selectedFiles.map(normalizePath),
  });
  return code;
}

async function runFrontendBuild() {
  const key = `frontend:${runId}`;
  if (!buildPromises.has(key)) {
    buildPromises.set(key, (async () => {
      const sourceHash = () => execFileSync(nodeCommand, [path.join(toolsDir, "source-hash.mjs"), "--frontend"], {
        cwd: projectRoot,
        encoding: "utf8",
      }).trim();
      const before = sourceHash();
      if (fs.existsSync(frontendBuildStamp)
        && fs.readFileSync(frontendBuildStamp, "utf8").trim() === before
        && fs.existsSync(path.join(frontendDir, "dist", "index.html"))
        && fs.existsSync(path.join(frontendDir, "dist", ".vite", "manifest.json"))) {
        console.error(`[Frontend] 复用已验证构建：${before}`);
        return 0;
      }
      fs.rmSync(frontendBuildStamp, { force: true });
      const code = await runProcess(npmCommand, ["run", "build"], { cwd: frontendDir });
      if (code !== 0) return code;
      const after = sourceHash();
      if (before !== after) {
        console.error("[Frontend] 构建期间源码变化，拒绝复用该产物");
        return 1;
      }
      fs.mkdirSync(path.dirname(frontendBuildStamp), { recursive: true });
      fs.writeFileSync(frontendBuildStamp, `${after}\n`, "utf8");
      return 0;
    })());
  }
  return buildPromises.get(key);
}

async function runContracts() {
  let code = await ensureNpmWorkspace(frontendDir);
  if (code !== 0) return code;
  officialPluginsRoot = officialPluginsRoot || resolveOfficialPluginsRoot();
  code = await ensureNpmWorkspace(officialPluginsRoot);
  if (code !== 0) return code;
  code = await runProcess(npmCommand, ["run", "build:frontend"], {
    cwd: officialPluginsRoot,
    timeoutMs: 15 * 60 * 1000,
  });
  if (code !== 0) return code;
  code = await runReported(npmCommand, ["run", "test", "--", "--config", "vitest.contract.config.ts"], { cwd: frontendDir }, "vitest", {
    expectedFiles: ["frontend/contracts/official-plugins.test.ts"],
    invokedFiles: ["frontend/contracts/official-plugins.test.ts"],
  });
  if (code !== 0) return code;
  code = await runProcess(nodeCommand, [path.join(officialPluginsRoot, "tools", "Test-FrontendPlugins.mjs"), "--host-root", projectRoot]);
  if (code !== 0) return code;
  return 0;
}

async function runDocs() {
  const dependencyCode = await ensureNpmWorkspace(toolsDir);
  if (dependencyCode !== 0) return dependencyCode;
  const files = [...matchingFiles(["tests/documentation/**/*.mjs"]), ...matchingFiles(["tests/tools/docs-index.test.mjs"])]
    .filter((file, index, all) => all.indexOf(file) === index);
  if (files.length === 0) return 1;
  return runReported(nodeCommand, ["--test", ...files], {}, "tap", {
    expectedFiles: files.map(normalizePath),
    invokedFiles: files.map(normalizePath),
  });
}

async function runTooling() {
  let code = await ensureNpmWorkspace(toolsDir);
  if (code !== 0) return code;
  // docs-index belongs to runDocs; running it here would repeat the same assertions.
  const nodeTests = matchingFiles(["tests/tools/*.test.mjs"])
    .filter(file => path.basename(file) !== "docs-index.test.mjs");
  if (nodeTests.length === 0) return 1;
  code = await runReported(nodeCommand, ["--test", ...nodeTests], {}, "tap", {
    expectedFiles: nodeTests.map(normalizePath),
    invokedFiles: nodeTests.map(normalizePath),
  });
  if (code !== 0) return code;
  if (!fs.existsSync(path.join(toolsDir, "tests"))) return 1;
  return runProcess(pythonCommand, ["-m", "unittest", "discover", "-s", "tools/tests", "-p", "test_*.py", "-v"]);
}

async function runSyntax() {
  const files = syntaxFiles();
  if (files.length === 0) return 1;
  for (const file of files) {
    const code = await runProcess(nodeCommand, ["--check", file]);
    if (code !== 0) return code;
  }
  return 0;
}

async function runBuild() {
  const dependencyCode = await prepareGateDependencies("core");
  if (dependencyCode !== 0) return dependencyCode;
  const key = `production:${runId}`;
  if (!buildPromises.has(key)) {
    buildPromises.set(key, buildProductionCore());
  }
  return buildPromises.get(key);
}

async function buildProductionCore() {
  let code = await runProcess(npmCommand, ["run", "typecheck"], { cwd: frontendDir });
  if (code !== 0) return code;
  code = await runFrontendBuild();
  if (code !== 0) return code;
  code = await runProcess(path.join(projectRoot, "build.cmd"), ["--frontend-ready"], { timeoutMs: 15 * 60 * 1000 });
  if (code !== 0) return code;
  return verifyEmbeddedManifest(path.join(projectRoot, "release", "nexus-pipeline.exe"), "requireAdministrator");
}

async function verifyEmbeddedManifest(executable, expectedLevel) {
  return runProcess(pythonCommand, [
    "tools\\pe_manifest.py",
    "--exe", executable,
    "--expected-level", expectedLevel,
  ], { cwd: projectRoot });
}

async function runArchitectureCheck() {
  const project = "tools\\NexusPipeline.Architecture\\NexusPipeline.Architecture.csproj";
  const architectureTests = "tools\\NexusPipeline.Architecture.Tests\\NexusPipeline.Architecture.Tests.csproj";
  const restoreCommon = ["--nologo", "-m:1", "-nr:false", "-p:NuGetAudit=false", "-p:RestoreIgnoreFailedSources=true"];
  let code = await runProcess("dotnet", [
    "restore", "src\\NexusPipeline.csproj", ...restoreCommon,
    "-p:NexusTestHost=false", "-p:NexusArchitectureMode=production",
  ], { cwd: projectRoot });
  if (code !== 0) return code;
  code = await runProcess("dotnet", [
    "restore", "tests\\NexusPipeline.Tests\\NexusPipeline.Tests.csproj", ...restoreCommon,
    "-p:NexusTestHost=true",
  ], { cwd: projectRoot });
  if (code !== 0) return code;
  code = await runProcess("dotnet", ["restore", project, ...restoreCommon], { cwd: projectRoot });
  if (code !== 0) return code;
  code = await runProcess("dotnet", ["build", project, "--nologo", "--no-restore", "-m:1"], { cwd: projectRoot });
  if (code !== 0) return code;
  code = await runProcess("dotnet", ["restore", architectureTests, ...restoreCommon], { cwd: projectRoot });
  if (code !== 0) return code;
  code = await runReported("dotnet", ["test", architectureTests, "--nologo", "--no-restore", "-m:1"], {}, "trx", {
    expectedFiles: ["tools/NexusPipeline.Architecture.Tests/ArchitectureRulesTests.cs"],
    invokedFiles: ["tools/NexusPipeline.Architecture.Tests/ArchitectureRulesTests.cs"],
  });
  if (code !== 0) return code;
  code = await runProcess("dotnet", ["run", "--project", project, "--no-build", "--", "check", "--root", projectRoot, "--mode", "both"], { cwd: projectRoot });
  if (code !== 0) return code;

  const generatedMap = path.join(projectRoot, ".generated", "architecture", "backend-map.json");
  const repeatedMap = `${generatedMap}.repeat`;
  fs.rmSync(repeatedMap, { force: true });
  try {
    code = await runProcess("dotnet", ["run", "--project", project, "--no-build", "--", "map", "--root", projectRoot, "--mode", "both", "--out", generatedMap], { cwd: projectRoot });
    if (code !== 0) return code;
    code = await runProcess("dotnet", ["run", "--project", project, "--no-build", "--", "map", "--root", projectRoot, "--mode", "both", "--out", repeatedMap], { cwd: projectRoot });
    if (code !== 0) return code;
    if (!fs.readFileSync(generatedMap).equals(fs.readFileSync(repeatedMap))) {
      console.error("[architecture] map generation is not deterministic");
      return 1;
    }
    code = await runProcess("dotnet", ["run", "--project", project, "--no-build", "--", "map", "--root", projectRoot, "--mode", "both", "--check", "--out", generatedMap], { cwd: projectRoot });
    if (code !== 0) return code;

    const artifactDirectory = path.join(reportRoot, "architecture");
    fs.mkdirSync(artifactDirectory, { recursive: true });
    const artifactMap = path.join(artifactDirectory, "backend-map.json");
    fs.copyFileSync(generatedMap, artifactMap);
    fs.writeFileSync(path.join(artifactDirectory, "backend-map.metadata.json"), `${JSON.stringify({
      schemaVersion: 1,
      candidateSha: candidateSha(),
      mapSha256: sha256File(generatedMap),
      map: "architecture/backend-map.json",
    }, null, 2)}\n`, "utf8");
    return 0;
  } finally {
    fs.rmSync(repeatedMap, { force: true });
  }
}

function buildTestHost() {
  const key = `test-host:${runId}`;
  if (!buildPromises.has(key)) buildPromises.set(key, buildTestHostCore());
  return buildPromises.get(key);
}

async function buildTestHostCore() {
  const dependencyCode = await ensureNpmWorkspace(frontendDir);
  if (dependencyCode !== 0) return dependencyCode;
  const code = await runFrontendBuild();
  if (code !== 0) return code;
  fs.rmSync(testHostDir, { recursive: true, force: true, maxRetries: 120, retryDelay: 250 });
  fs.mkdirSync(testHostDir, { recursive: true });
  const publishCode = await runProcess("dotnet", [
    "publish", "src\\NexusPipeline.csproj", "-c", "Release", "-r", "win-x64", "--self-contained", "false",
    "-p:PublishSingleFile=true", "-p:DebugType=none", "-p:DebugSymbols=false", "-p:NexusTestHost=true",
    "-o", testHostDir, "--nologo", "-m:1", "-nr:false",
  ], { timeoutMs: 15 * 60 * 1000 });
  if (publishCode !== 0) return publishCode;
  const manifestCode = await verifyEmbeddedManifest(path.join(testHostDir, "nexus-pipeline.exe"), "asInvoker");
  if (manifestCode !== 0) return manifestCode;
  fs.cpSync(path.join(frontendDir, "dist"), path.join(testHostDir, "wwwroot"), { recursive: true });
  fs.mkdirSync(path.join(testHostDir, "plugins"), { recursive: true });
  console.error(`[Test Host] 构建完成：${path.join(testHostDir, "nexus-pipeline.exe")}`);
  return 0;
}

function cleanTestHost() {
  fs.rmSync(testHostDir, { recursive: true, force: true, maxRetries: 120, retryDelay: 250 });
  buildPromises.delete(`test-host:${runId}`);
}

function cleanEmulatorFixturePlugin() {
  fs.rmSync(emulatorFixturePluginDir, { recursive: true, force: true, maxRetries: 120, retryDelay: 250 });
}

async function buildEmulatorFixturePlugin() {
  cleanEmulatorFixturePlugin();
  fs.mkdirSync(emulatorFixturePluginDir, { recursive: true });
  return runProcess("dotnet", [
    "publish", "tests\\fixtures\\NexusPipeline.TestPlugin\\NexusPipeline.TestPlugin.csproj", "-c", "Release",
    "-o", emulatorFixturePluginDir, "--nologo", "-m:1", "-nr:false",
  ], { timeoutMs: 10 * 60 * 1000 });
}

function testHostEnvironment({ phase = "default", exitFile, system = false, runtimeName = "" } = {}) {
  const policy = runtimePolicy({
    group: phase === "update-realtime" || phase === "execution-realtime" || phase === "accelerated" ? "update-acceptance" : "ui-runtime",
    phase,
    platform: process.platform,
    integrity: getIntegrityLevel(),
    env: process.env,
    testHostDir,
    exitFile: exitFile || path.join(runRoot, "test-host.exit"),
    runId,
  });
  const env = { ...policy.env, NEXUS_TEST_RUN_ID: runId, NEXUS_TEST_HOST_DIR: testHostDir };
  if (system) {
    env.NEXUS_SYSTEM_RUNTIME_NAME = runtimeName;
    env.NEXUS_SYSTEM_WEB_PORT = "";
  }
  return env;
}

function parseNamedGroups(args, keys, label) {
  const selected = [];
  for (let index = 0; index < args.length; index++) {
    const arg = args[index];
    if (arg === "--affected") return { error: "旧的受影响计划参数已退役；请使用显式 --group 选择或完整运行。" };
    if (arg === "--group") {
      const value = args[++index]?.toLowerCase();
      if (!value || value.startsWith("--")) return { error: "--group 缺少分组名" };
      selected.push(value);
      continue;
    }
    if (arg.startsWith("--group=")) {
      const value = arg.slice(8).toLowerCase();
      if (!value) return { error: "--group 缺少分组名" };
      selected.push(value);
      continue;
    }
    return { error: `${label} 不支持参数：${arg}` };
  }
  const unknown = selected.filter(key => !keys.includes(key));
  return unknown.length ? { error: `未知 ${label} 分组：${unknown.join(", ")}\n可用分组：${keys.join(" | ")}` } : { groups: [...new Set(selected)] };
}

function parseSystemArgs(args) {
  const groups = [];
  let realtime = false;
  for (let index = 0; index < args.length; index++) {
    const arg = args[index];
    if (arg === "--realtime") { realtime = true; continue; }
    if (arg === "--dry") return { error: "旧的列表参数已退役；正式入口必须执行真实测试。" };
    if (arg === "--group") {
      const value = args[++index]?.toLowerCase();
      if (!value || value.startsWith("--")) return { error: "--group 缺少分组名" };
      groups.push(value);
      continue;
    }
    if (arg.startsWith("--group=")) { groups.push(arg.slice(8).toLowerCase()); continue; }
    if (arg.startsWith("--")) return { error: `未知参数：${arg}` };
    groups.push(arg.toLowerCase());
  }
  const keys = SYSTEM_TEST_GROUPS.map(group => group.key);
  const unknown = groups.filter(key => !keys.includes(key));
  return unknown.length ? { error: `未知 System Smoke 分组：${unknown.join(", ")}\n可用分组：${keys.join(" | ")}` } : { groups: [...new Set(groups)], realtime };
}

async function runUi() {
  const dependencyCode = await prepareGateDependencies("ui-runtime");
  if (dependencyCode !== 0) return dependencyCode;
  const code = await buildTestHost();
  if (code !== 0) return code;
  const env = testHostEnvironment({ phase: "default", exitFile: path.join(runRoot, "ui", ".nxp", "test-host.exit") });
  const webPort = await findAvailablePort();
  env.NEXUS_E2E_BASE_URL = `http://127.0.0.1:${webPort}/`;
  const files = syntaxFiles().filter(file => file.endsWith(".smoke.spec.mjs"));
  return runReported(nodeCommand, [playwrightCli, "test"], { cwd: e2eDir, env, timeoutMs: 15 * 60 * 1000 }, "playwright", {
    expectedFiles: files.map(normalizePath),
    invokedFiles: files.map(normalizePath),
  });
}

async function runSystem(args = [], { phase = "accelerated" } = {}) {
  const parsed = parseSystemArgs(args);
  if (parsed.error) { console.error(parsed.error); return 2; }
  const suites = systemSuites(parsed.groups);
  if (suites.length === 0) return 1;
  let code = await buildTestHost();
  if (code !== 0) return code;
  if (suites.some(suite => suite.group === "emulator")) {
    code = await buildEmulatorFixturePlugin();
    if (code !== 0) { cleanEmulatorFixturePlugin(); return code; }
  }
  try {
    for (const suite of suites) {
      const port = await findAvailablePort();
      const suitePhase = parsed.realtime
        ? suite.group === "execution" ? "execution-realtime" : suite.group === "update" ? "update-realtime" : phase
        : phase;
      const runtimeName = systemRuntimeName(suite.runtimeName, suitePhase);
      const env = testHostEnvironment({
        phase: suitePhase,
        system: true,
        runtimeName,
        exitFile: path.join(runRoot, runtimeName, ".nxp", "test-host.exit"),
      });
      env.NEXUS_SYSTEM_WEB_PORT = String(port);
      if (suite.group === "emulator") env.NEXUS_SYSTEM_EMULATOR_PLUGIN_DIR = emulatorFixturePluginDir;
      console.error(`[System Smoke] 开始 ${suite.group}/${runtimeName}，port=${port}，timeScale=${env.NEXUS_TIME_SCALE}`);
      code = await runReported(nodeCommand, ["--test", "--test-concurrency=1", suite.file], { env, timeoutMs: 5 * 60 * 1000 }, "tap", {
        expectedFiles: [normalizePath(suite.file)],
        invokedFiles: [normalizePath(suite.file)],
      });
      if (code !== 0) return code;
    }
    return 0;
  } finally {
    if (suites.some(suite => suite.group === "emulator")) cleanEmulatorFixturePlugin();
  }
}

async function runDefault() {
  for (const step of [
    () => runUnit(),
    () => runFrontend(),
    () => runContracts(),
    () => runDocs(),
    () => runTooling(),
    () => runSyntax(),
    () => runArchitectureCheck(),
  ]) {
    const code = await step();
    if (code !== 0) return code;
  }
  return 0;
}

async function prepareFast() {
  for (const workspace of [frontendDir, toolsDir]) {
    const code = await ensureNpmWorkspace(workspace);
    if (code !== 0) return code;
  }
  return 0;
}

async function runIntegration() {
  let code = await runUi();
  if (code !== 0) return code;
  code = await runSystem();
  if (code !== 0) return code;
  code = await runProcess(nodeCommand, ["tools\\validate-update-policy-history.mjs"]);
  if (code !== 0) return code;
  code = await runSystem(["update"], { phase: "update-realtime" });
  if (code !== 0) return code;
  return runSystem(["execution"], { phase: "execution-realtime" });
}

async function runDev(suite, args) {
  if (!MODE_SUITES.has(suite)) return 2;
  if (suite === "default") return runDefault();
  if (suite === "ui") return args.length ? 2 : runUi();
  if (suite === "system") return runSystem(args, { phase: "accelerated" });
  if (suite === "all") {
    let code = await runDefault();
    if (code === 0) code = await runUi();
    if (code === 0) code = await runSystem();
    return code;
  }
  return 2;
}

async function runRelease(group) {
  if (group === "all") {
    for (const gate of RELEASE_GATE_GROUPS) {
      const code = await runRelease(gate);
      if (code !== 0) return code;
    }
    return 0;
  }
  if (!RELEASE_GATE_GROUPS.includes(group)) return 2;
  const dependencyCode = await prepareGateDependencies(group);
  if (dependencyCode !== 0) return dependencyCode;
  const steps = {
    core: [runBuild, runUnit, runDocs, runTooling, runSyntax, runArchitectureCheck],
    "frontend-contract": [runFrontend, runContracts],
    "ui-runtime": [runUi, () => runSystem(["runtime", "control", "config", "plugins"], { phase: "accelerated" })],
    "execution-emulator": [() => runSystem(["execution", "judge", "emulator"], { phase: "accelerated" })],
    "update-acceptance": [
      () => runProcess(nodeCommand, ["tools\\validate-update-policy-history.mjs"]),
      () => runSystem(["update"], { phase: "accelerated" }),
      () => runSystem(["update"], { phase: "update-realtime" }),
      () => runSystem(["execution"], { phase: "execution-realtime" }),
    ],
  }[group];
  for (const step of steps) {
    const code = await step();
    if (code !== 0) return code;
  }
  return 0;
}

function listTestPlan() {
  return {
    schemaVersion: 1,
    commands: {
      prepare: "node tests/run.mjs prepare",
      fast: "node tests/run.mjs fast",
      integration: "node tests/run.mjs integration",
      all: "node tests/run.mjs all",
      dev: "node tests/run.mjs dev <default|ui|system|all>",
      release: `node tests/run.mjs release <${RELEASE_GROUPS.join("|")}>`,
      unit: "node tests/run.mjs unit [--group <area>]",
      frontend: "node tests/run.mjs frontend [--group <group>]",
    },
    hostAreas: HOST_TEST_AREAS,
    frontendGroups: FRONTEND_TEST_GROUPS,
    systemGroups: SYSTEM_TEST_GROUPS,
    governanceDomains: GOVERNANCE_DOMAINS,
  };
}

function printUsage() {
  console.error("用法：node tests\\run.mjs prepare|fast|integration|all");
  console.error("用法：node tests\\run.mjs dev <default|ui|system|all>");
  console.error(`       node tests\\run.mjs release <${RELEASE_GROUPS.join("|")}>`);
  console.error("       node tests\\run.mjs unit|frontend [--group <name>]");
  console.error("       node tests\\run.mjs contract|docs|tooling|syntax|build|list [--json]");
  console.error("旧的权限/影响域/列表入口已退役；所有功能测试统一使用 NexusTestHost。");
}

const [command = "", ...args] = process.argv.slice(2);
let exitCode = 2;
resetProcessRunnerState();
try {
  switch (command.toLowerCase()) {
    case "prepare": exitCode = args.length ? 2 : await prepareFast(); break;
    case "fast": exitCode = args.length ? 2 : await runDefault(); break;
    case "integration": exitCode = args.length ? 2 : await runIntegration(); break;
    case "all": {
      if (args.length) { exitCode = 2; break; }
      exitCode = await runDefault();
      if (exitCode === 0) exitCode = await runIntegration();
      break;
    }
    case "unit": {
      const parsed = parseNamedGroups(args, HOST_TEST_AREAS.map(area => area.key), "Unit");
      exitCode = parsed.error ? (console.error(parsed.error), 2) : await runUnit(parsed.groups);
      break;
    }
    case "frontend": {
      const parsed = parseNamedGroups(args, FRONTEND_TEST_GROUPS.map(group => group.key), "Frontend");
      exitCode = parsed.error ? (console.error(parsed.error), 2) : await runFrontend(parsed.groups);
      break;
    }
    case "contract": exitCode = args.length ? 2 : await runContracts(); break;
    case "docs": exitCode = args.length ? 2 : await runDocs(); break;
    case "tooling": exitCode = args.length ? 2 : await runTooling(); break;
    case "syntax": exitCode = args.length ? 2 : await runSyntax(); break;
    case "build": exitCode = args.length ? 2 : await runBuild(); break;
    case "release": exitCode = args.length === 1 ? await runRelease(args[0].toLowerCase()) : 2; break;
    case "dev": exitCode = await runDev(args[0]?.toLowerCase() || "default", args.slice(1)); break;
    case "list":
      if (args.length > 1 || (args.length === 1 && args[0] !== "--json")) exitCode = 2;
      else { const plan = listTestPlan(); if (args[0] === "--json") console.log(JSON.stringify(plan, null, 2)); else console.log(`Host=${HOST_TEST_AREAS.length} Frontend=${FRONTEND_TEST_GROUPS.length} System=${SYSTEM_TEST_GROUPS.length}`); exitCode = 0; }
      break;
    default: printUsage(); exitCode = 2;
  }
} catch (error) {
  console.error(`[错误] ${error.stack || error.message}`);
  exitCode = 1;
} finally {
  if (["dev", "release", "integration", "all"].includes(command.toLowerCase())) {
    const runnerState = getProcessRunnerState();
    if (runnerState.cleanupComplete) {
      cleanEmulatorFixturePlugin();
      cleanTestHost();
    } else {
      console.error(`[清理] 进程树清理未确认完成，保留 Test Host/fixture 现场：${runnerState.cleanupFailures.join("；")}`);
    }
  }
}
process.exitCode = exitCode;
