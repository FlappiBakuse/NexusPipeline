import crypto from "node:crypto";
import fs from "node:fs";
import path from "node:path";
import { spawnSync } from "node:child_process";
import { fileURLToPath } from "node:url";

export const CI_FINGERPRINT_SCHEMA_VERSION = 1;

const projectRoot = path.resolve(path.dirname(fileURLToPath(import.meta.url)), "..");
const excludedDirectories = new Set(["bin", "obj", "node_modules", "dist"]);

function collectFiles(directory, files = []) {
  if (!fs.existsSync(directory)) return files;
  for (const entry of fs.readdirSync(directory, { withFileTypes: true })) {
    if (excludedDirectories.has(entry.name)) continue;
    const absolute = path.join(directory, entry.name);
    if (entry.isDirectory()) collectFiles(absolute, files);
    else if (entry.isFile()) files.push(absolute);
  }
  return files;
}

function relativePath(root, file) {
  return path.relative(root, file).replaceAll("\\", "/");
}

function readVersion(command, args, root) {
  try {
    const result = spawnSync(command, args, {
      cwd: root,
      encoding: "utf8",
      windowsHide: true,
      timeout: 10_000,
    });
    if (result.status === 0) return String(result.stdout || "").trim().split(/\r?\n/u)[0] || "unknown";
  } catch {
    // 工具链缺失也必须产生可比较的指纹，而不是让 manifest 生成失败。
  }
  return "unavailable";
}

function currentGitSha(root) {
  const configured = process.env.GITHUB_SHA || process.env.NEXUS_CI_HEAD_SHA;
  if (configured) return configured;
  try {
    const result = spawnSync("git", ["rev-parse", "HEAD"], {
      cwd: root,
      encoding: "utf8",
      windowsHide: true,
      timeout: 10_000,
    });
    if (result.status === 0) return String(result.stdout || "").trim() || "unknown";
  } catch {
    // 本地压缩源码或无 git 环境仍可使用文件内容指纹。
  }
  return "unknown";
}

function sourceDigest(root) {
  const roots = ["src", "frontend"].map(relative => path.join(root, relative));
  const files = roots.flatMap(directory => collectFiles(directory));
  for (const relative of [
    "build.cmd",
    "global.json",
    "tools/source-hash.mjs",
    "tools/ci-fingerprint.mjs",
  ]) {
    const file = path.join(root, relative);
    if (fs.existsSync(file) && fs.statSync(file).isFile()) files.push(file);
  }
  const entries = [...new Set(files)]
    .sort((left, right) => Buffer.from(relativePath(root, left), "utf8").compare(Buffer.from(relativePath(root, right), "utf8")))
    .map(file => relativePath(root, file) + "\0" + crypto.createHash("sha256").update(fs.readFileSync(file)).digest("hex"))
    .join("\n");
  return crypto.createHash("sha256").update(entries, "utf8").digest("hex");
}

function buildArguments(mode) {
  const common = [
    "src/NexusPipeline.csproj",
    "-c Release",
    "-r win-x64",
    "--self-contained false",
    "-p:PublishSingleFile=true",
    "-p:DebugType=none",
    "-p:DebugSymbols=false",
  ];
  return mode === "test-host"
    ? [...common, "-p:NexusTestHost=true"]
    : common;
}

export function fingerprintPayload({
  root = projectRoot,
  mode = "production",
  targetFramework = "net8.0-windows",
  runtimeIdentifier = "win-x64",
  applicationManifest = mode === "test-host" ? "src/app.test.manifest" : "src/app.manifest",
  sourceHash = sourceDigest(root),
  gitSha = currentGitSha(root),
  toolchain = null,
} = {}) {
  return {
    schemaVersion: CI_FINGERPRINT_SCHEMA_VERSION,
    gitSha,
    sourceHash,
    targetFramework,
    runtimeIdentifier,
    mode,
    applicationManifest,
    frontendResourceSource: "frontend/src -> frontend/dist -> release/wwwroot",
    buildArguments: buildArguments(mode),
    toolchain: toolchain || {
      node: process.version,
      dotnet: readVersion("dotnet", ["--version"], root),
      npm: readVersion(process.platform === "win32" ? "npm.cmd" : "npm", ["--version"], root),
    },
  };
}

export function createBuildFingerprint(options = {}) {
  return crypto.createHash("sha256")
    .update(JSON.stringify(fingerprintPayload(options)), "utf8")
    .digest("hex");
}

if (process.argv[1] && path.resolve(process.argv[1]) === fileURLToPath(import.meta.url)) {
  const mode = process.argv.includes("--test-host") ? "test-host" : "production";
  process.stdout.write(createBuildFingerprint({ mode }) + "\n");
}
