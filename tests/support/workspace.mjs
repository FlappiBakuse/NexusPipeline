import fs from "node:fs";
import path from "node:path";
import { execFileSync } from "node:child_process";
import { sha256 } from "./report.mjs";

export function stageWorkspace(sourceRoot, artifactRoot, budget, directoryName = "Host", {dotnetRequired=true} = {}) {
  if (!["Host", "Plugins"].includes(directoryName)) throw new Error("Unknown staging repository");
  const started = budget.elapsedMs;
  const git = (...args) => {
    budget.check();
    return execFileSync("git", ["-C", sourceRoot, ...args], {
      encoding: "utf8", windowsHide: true, timeout: Number.isFinite(budget.remainingMs()) ? Math.max(1, Math.floor(budget.remainingMs())) : undefined,
      maxBuffer: 16 * 1024 * 1024,
    });
  };
  const files = [...new Set(git("ls-files", "-z", "--cached", "--others", "--exclude-standard")
    .split("\0").filter(Boolean))].sort();
  const entries = [];
  for (const relative of files) {
    budget.check();
    const file = path.resolve(sourceRoot, relative);
    if (path.relative(sourceRoot, file).startsWith("..") || !fs.existsSync(file)) continue;
    if (fs.lstatSync(file).isSymbolicLink() || !fs.statSync(file).isFile()) throw new Error(`Unsupported source entry: ${relative}`);
    entries.push({ relative, bytes: fs.readFileSync(file) });
  }
  if (!entries.length) throw new Error("Empty source checkout");
  const sourceFingerprint = sha256(entries.map(item => `${item.relative}\0${sha256(item.bytes)}`).join("\n"));
  budget.check();
  const dotnet = dotnetRequired ? execFileSync("dotnet", ["--version"], {
    env: {...process.env, DOTNET_CLI_HOME: process.env.DOTNET_CLI_HOME || path.join(artifactRoot,"cache/dotnet"),
      DOTNET_CLI_TELEMETRY_OPTOUT:"1", DOTNET_CLI_WORKLOAD_UPDATE_NOTIFY_DISABLE:"true", DOTNET_NOLOGO:"1"},
    encoding: "utf8", windowsHide: true, timeout: Number.isFinite(budget.remainingMs()) ? Math.max(1, Math.floor(budget.remainingMs())) : undefined,
  }).trim() : null;
  const fingerprint = sha256(`${sourceFingerprint}\0${process.version}\0${process.platform}\0${process.arch}\0${dotnet}`);
  const destination = path.join(artifactRoot, "cache", fingerprint.slice(0, 16), directoryName);
  const cacheHit = fs.existsSync(destination);
  fs.mkdirSync(destination, { recursive: true });
  for (let current = destination; ; current = path.dirname(current)) {
    if (fs.lstatSync(current).isSymbolicLink()) throw new Error("Linked staging path");
    if (path.dirname(current) === current) break;
  }
  const identityPath = path.join(destination, ".nxp-cache-identity");
  if (!cacheHit) fs.writeFileSync(identityPath, fingerprint, { flag: "wx" });
  if (fs.readFileSync(identityPath, "utf8") !== fingerprint) throw new Error("Cache identity collision");
  const leasePath = path.join(destination, ".nxp-test-lease.json");
  const leaseBytes = JSON.stringify({ pid: process.pid, startedAt: new Date().toISOString(), fingerprint });
  fs.writeFileSync(leasePath, leaseBytes, { flag: "wx" });
  for (const { relative, bytes } of entries) {
    budget.check();
    const target = path.join(destination, relative);
    for (let parent = path.dirname(target); parent !== destination; parent = path.dirname(parent)) {
      if (fs.existsSync(parent) && fs.lstatSync(parent).isSymbolicLink()) throw new Error("Linked staged directory");
    }
    fs.mkdirSync(path.dirname(target), { recursive: true });
    if (fs.existsSync(target) && fs.lstatSync(target).isSymbolicLink()) throw new Error("Linked staged file");
    if (!fs.existsSync(target) || !fs.readFileSync(target).equals(bytes)) fs.writeFileSync(target, bytes);
  }
  const dirty = Boolean(git("status", "--porcelain").trim());
  return { directory: destination, fingerprint, sourceFingerprint, cacheHit, preparationMs: budget.elapsedMs - started,
    toolchain: { node: process.version, dotnet },
    release() {
      if (fs.readFileSync(leasePath, "utf8") !== leaseBytes) throw new Error("Staging ownership changed");
      fs.unlinkSync(leasePath);
    }, source: {
    commitSha: git("rev-parse", "HEAD").trim(), treeSha: git("rev-parse", "HEAD^{tree}").trim(),
    workingTreeDirty: dirty, dirtyFingerprint: dirty ? sourceFingerprint : null,
  } };
}
