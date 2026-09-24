import { spawnSync } from "node:child_process";
import fs from "node:fs";
import path from "node:path";
import { fileURLToPath } from "node:url";
import { FRONTEND_TEST_GROUPS, HOST_TEST_AREAS } from "../tests/registry.mjs";
import { globToRegExp } from "./path-glob.mjs";

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

function fullFastPlan() {
  return {
    docs_only: false,
    full: true,
    unit_groups: HOST_TEST_AREAS.map(area => area.key),
    frontend_groups: FRONTEND_TEST_GROUPS.map(group => group.key),
    contracts: true,
    docs: true,
    tooling: true,
    syntax: true,
    architecture: true,
    needs_plugins: true,
  };
}

function matchingGroups(file, groups) {
  return groups.filter(group => group.paths.some(pattern => globToRegExp(pattern).test(file)))
    .map(group => group.key);
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
  if (!Array.isArray(changedFiles) || changedFiles.length === 0) return fullFastPlan();
  plan.docs_only = changedFiles.every(isDocumentationPath);
  const unit = new Set();
  const frontend = new Set();
  for (const value of changedFiles) {
    const file = value.replaceAll("\\", "/").replace(/^\.\//u, "");
    if (isDocumentationPath(file) || file === "AGENTS.md") { plan.docs = true; continue; }
    if (file.startsWith("frontend/")) {
      const owners = matchingGroups(file, FRONTEND_TEST_GROUPS);
      (owners.length ? owners : FRONTEND_TEST_GROUPS.map(group => group.key)).forEach(key => frontend.add(key));
      if (file.startsWith("frontend/src/plugin-bridge/")) {
        unit.add("plugins");
        plan.contracts = true;
        plan.architecture = true;
      }
      continue;
    }
    const owners = matchingGroups(file, HOST_TEST_AREAS);
    if (owners.length) {
      owners.forEach(key => unit.add(key));
      if (file.startsWith("src/Modules/Plugins/") || file.startsWith("src/NexusPipeline.Plugin.Abstractions/")) plan.contracts = true;
      if (file.startsWith("src/NexusPipeline.Plugin.Abstractions/")) frontend.add("bridge");
      if (file.startsWith("src/")) plan.architecture = true;
      continue;
    }
    return fullFastPlan();
  }
  plan.unit_groups = [...unit].sort();
  plan.frontend_groups = [...frontend].sort();
  plan.needs_plugins = plan.contracts;
  if (![plan.docs, plan.contracts, plan.architecture, plan.tooling, plan.syntax,
    plan.unit_groups.length > 0, plan.frontend_groups.length > 0].some(Boolean)) return fullFastPlan();
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
