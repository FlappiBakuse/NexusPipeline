import { spawnSync } from "node:child_process";
import fs from "node:fs";
import path from "node:path";
import { fileURLToPath } from "node:url";

const projectRoot = path.resolve(path.dirname(fileURLToPath(import.meta.url)), "..");
const KEYS = ["core", "frontend_contract", "runtime", "update"];

function parseNameStatusZ(output) {
  const tokens = String(output ?? "").split("\0");
  const files = [];
  for (let index = 0; index < tokens.length;) {
    const status = tokens[index++];
    if (!status) continue;
    if (status.startsWith("R") || status.startsWith("C")) {
      const oldPath = tokens[index++];
      const newPath = tokens[index++];
      if (oldPath) files.push(oldPath);
      if (newPath) files.push(newPath);
    } else {
      const file = tokens[index++];
      if (file) files.push(file);
    }
  }
  return files.map(value => value.replaceAll("\\", "/")).filter(Boolean);
}

function all(value = true) {
  return Object.fromEntries(KEYS.map(key => [key, value]));
}

function isDocumentationPath(file) {
  return file === "README.md" || file === "CHANGELOG.md" || file.startsWith("docs/");
}

function classifyFast(changedFiles) {
  const plan = {
    docs_only: false,
    full: false,
    unit_groups: [],
    frontend_groups: [],
    contracts: false,
    docs: false,
    tooling: false,
    syntax: false,
    architecture: false,
    needs_plugins: false,
  };
  if (!Array.isArray(changedFiles) || changedFiles.length === 0) return { ...plan, full: true };
  plan.docs_only = changedFiles.every(isDocumentationPath);
  const unit = new Set();
  const frontend = new Set();
  const allUnit = ["core", "persistence", "config", "execution", "scheduling", "judgement", "plugins", "update", "control", "observability"];
  const allFrontend = ["ui", "bridge", "platform", "features"];
  for (const value of changedFiles) {
    const file = value.replaceAll("\\", "/").replace(/^\.\//u, "");
    if (isDocumentationPath(file) || file === "AGENTS.md") { plan.docs = true; continue; }
    if (file.startsWith("frontend/src/ui/")) { frontend.add("ui"); continue; }
    if (file.startsWith("frontend/src/plugin-bridge/") || file.startsWith("src/NexusPipeline.Plugin.Abstractions/")) {
      frontend.add("bridge"); unit.add("plugins"); plan.contracts = true; plan.needs_plugins = true; plan.architecture = true; continue;
    }
    if (file.startsWith("frontend/src/platform/") || file.startsWith("frontend/src/stores/") || file === "frontend/src/router.ts") { frontend.add("platform"); continue; }
    if (file.startsWith("frontend/src/features/") || file.startsWith("frontend/src/app/")) { frontend.add("features"); continue; }
    if (file.startsWith("frontend/")) { allFrontend.forEach(item => frontend.add(item)); continue; }
    if (file.startsWith("src/Modules/Configuration/") || file.startsWith("src/Modules/Settings/") || file.startsWith("src/Modules/Users/") || file.startsWith("src/Modules/Scripts/") || file.startsWith("src/Modules/Queues/")) unit.add("config");
    else if (file.startsWith("src/Modules/Execution/Judgement/") || file.startsWith("src/Modules/Execution/Monitoring/")) unit.add("judgement");
    else if (file.startsWith("src/Modules/Execution/")) unit.add("execution");
    else if (file.startsWith("src/Modules/Scheduling/")) unit.add("scheduling");
    else if (file.startsWith("src/Modules/Plugins/")) { unit.add("plugins"); plan.contracts = true; plan.needs_plugins = true; }
    else if (file.startsWith("src/Modules/Updates/") || file === "update-policy.json") unit.add("update");
    else if (file.startsWith("src/ControlPlane/")) unit.add("control");
    else if (file.startsWith("src/Modules/Diagnostics/") || file.startsWith("src/Modules/History/") || file.startsWith("src/Modules/Notifications/")) unit.add("observability");
    else if (file.includes("/Persistence/") || file.startsWith("src/Modules/Configuration/Snapshots/")) unit.add("persistence");
    else if (file.startsWith("src/Host/") || file.startsWith("src/Shared/") || file.startsWith("src/Platform/")) unit.add("core");
    else if (file.startsWith("src/")) allUnit.forEach(item => unit.add(item));
    else if (file.startsWith("tools/") || file.startsWith("tests/") || file.startsWith(".github/")
      || ["build.cmd", "global.json", "package.json", "package-lock.json", "Directory.Build.props"].includes(file)
      || file.endsWith(".csproj") || file.endsWith(".sln")) plan.full = true;
    else plan.full = true;
    if (file.startsWith("src/")) plan.architecture = true;
  }
  plan.unit_groups = [...unit].sort();
  plan.frontend_groups = [...frontend].sort();
  if (plan.full) {
    plan.unit_groups = allUnit;
    plan.frontend_groups = allFrontend;
    Object.assign(plan, { contracts: true, docs: true, tooling: true, syntax: true, architecture: true, needs_plugins: true });
  }
  if (![plan.docs, plan.full, plan.contracts, plan.architecture, plan.tooling, plan.syntax,
    plan.unit_groups.length > 0, plan.frontend_groups.length > 0].some(Boolean)) plan.full = true;
  return plan;
}

function markForPath(file) {
  const normalized = file.replaceAll("\\", "/").replace(/^\.\//u, "");
  if (!normalized || normalized.startsWith(".github/")
    || normalized === "build.cmd"
    || normalized === "global.json"
    || normalized === "package.json"
    || normalized === "package-lock.json"
    || normalized === "tools/ci-scope.mjs"
    || normalized === "tools/path-glob.mjs"
    || normalized === "tests/run.mjs"
    || normalized.endsWith(".csproj")
    || normalized.endsWith(".sln")) {
    return all();
  }
  if (normalized === "README.md" || normalized === "AGENTS.md"
    || normalized.startsWith("docs/") || normalized.startsWith(".codex/")) {
    return { core: true, frontend_contract: false, runtime: false, update: false };
  }
  if (normalized.startsWith("frontend/")
    || normalized.startsWith("src/Modules/Plugins/")
    || normalized.startsWith("src/NexusPipeline.Plugin.Abstractions/")
    || normalized.includes("manifest")) {
    return { core: true, frontend_contract: true, runtime: true, update: false };
  }
  if (normalized.includes("Update")
    || normalized.includes("update")
    || normalized === "update-policy.json"
    || normalized.startsWith("tests/system/update")) {
    return { core: true, frontend_contract: false, runtime: true, update: true };
  }
  if (normalized.startsWith("src/") || normalized.startsWith("tests/")) {
    return { core: true, frontend_contract: false, runtime: true, update: false };
  }
  if (normalized.startsWith("tools/") || normalized.startsWith("scripts/")) {
    return all();
  }
  return all();
}

function merge(left, right) {
  for (const key of KEYS) left[key] ||= right[key];
  return left;
}

function collectChangedFiles(base, head) {
  const result = spawnSync("git", ["diff", "--name-status", "--find-renames", "-z", base, head], {
    cwd: projectRoot,
    encoding: "buffer",
    maxBuffer: 32 * 1024 * 1024,
  });
  if (result.error || result.status !== 0) {
    throw new Error(result.error?.message || result.stderr?.toString("utf8") || `git diff 退出码 ${result.status}`);
  }
  return parseNameStatusZ(result.stdout.toString("utf8"));
}

function parseArguments(argv) {
  const options = { base: "", head: "", output: "", mode: "legacy" };
  for (let index = 0; index < argv.length; index++) {
    const arg = argv[index];
    if (arg === "--base") options.base = argv[++index] || "";
    else if (arg === "--head") options.head = argv[++index] || "";
    else if (arg === "--github-output") options.output = argv[++index] || "";
    else if (arg === "--mode") options.mode = argv[++index] || "";
    else throw new Error(`未知参数：${arg}`);
  }
  if (!["legacy", "fast"].includes(options.mode)) throw new Error(`未知 scope mode：${options.mode}`);
  return options;
}

function main(argv) {
  const options = parseArguments(argv);
  const base = options.base || process.env.NEXUS_CI_BASE_SHA || process.env.GITHUB_BASE_SHA || "";
  const head = options.head || process.env.NEXUS_CI_HEAD_SHA || process.env.GITHUB_SHA || "";
  let result = all(false);
  let changedFiles;
  if (!base || !head) {
    changedFiles = null;
  } else {
    try {
      changedFiles = collectChangedFiles(base, head);
    } catch (error) {
      console.error(`[ci-scope] ${error.message}`);
      changedFiles = null;
    }
  }
  if (changedFiles === null) {
    result = all();
  } else {
    for (const file of changedFiles) merge(result, markForPath(file));
  }
  const fast = classifyFast(changedFiles);
  const lines = options.mode === "fast"
    ? [
      `docs_only=${fast.docs_only ? "true" : "false"}`,
      `needs_plugins=${fast.needs_plugins ? "true" : "false"}`,
      `needs_dotnet=${fast.full || fast.architecture || fast.unit_groups.length > 0 ? "true" : "false"}`,
      `needs_python=${fast.full || fast.tooling ? "true" : "false"}`,
      `plan_json=${JSON.stringify(fast)}`,
    ]
    : KEYS.map(key => `${key}=${result[key] ? "true" : "false"}`);
  process.stdout.write(`${lines.join("\n")}\n`);
  if (options.output) fs.appendFileSync(options.output, `${lines.join("\n")}\n`, "utf8");
  return 0;
}

if (process.argv[1] && path.resolve(process.argv[1]) === fileURLToPath(import.meta.url)) {
  try {
    process.exitCode = main(process.argv.slice(2));
  } catch (error) {
    console.error(`[ci-scope] ${error.message}`);
    process.stdout.write(`${KEYS.map(key => `${key}=true`).join("\n")}\n`);
    process.exitCode = 1;
  }
}

export { classifyFast, markForPath, parseNameStatusZ };
