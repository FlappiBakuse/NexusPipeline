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
  return { docs_only: Array.isArray(changedFiles) && changedFiles.length > 0 && changedFiles.every(isDocumentationPath) };
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
  const lines = options.mode === "fast"
    ? [`docs_only=${classifyFast(changedFiles).docs_only ? "true" : "false"}`]
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
