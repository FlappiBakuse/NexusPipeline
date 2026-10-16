import fs from "node:fs";
import path from "node:path";
import { execFileSync } from "node:child_process";
import { sha256 } from "./report.mjs";

export function stageWorkspace(sourceRoot, artifactRoot, budget, directoryName = "Host", {dotnetRequired=true, allowIncremental=true} = {}) {
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
    if ([".nxp-cache-identity", ".nxp-test-lease.json", ".nxp-source-inventory.json"].includes(relative)) throw new Error("Reserved staging metadata in source");
    for (let parent = path.dirname(file); parent !== path.resolve(sourceRoot); parent = path.dirname(parent)) {
      if (fs.lstatSync(parent).isSymbolicLink()) throw new Error("Linked source directory");
    }
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
  const incremental = allowIncremental && process.env.NEXUS_TEST_LOW_RESOURCE === "1";
  const graph = entries.filter(item => /(?:^|\/)(?:package(?:-lock)?\.json|\.npmrc|global\.json|[Nn]u[Gg]et\.[Cc]onfig)$|\.(?:csproj|props|targets)$/.test(item.relative));
  const slot = incremental ? sha256(JSON.stringify([path.resolve(sourceRoot), process.version, process.platform,
    process.arch, dotnet, graph.map(item => [item.relative, sha256(item.bytes)])])) : fingerprint;
  const destination = path.join(artifactRoot, "cache", incremental ? "incremental" : "", slot.slice(0, 16), directoryName);
  const cacheHit = fs.existsSync(destination);
  for (let current = destination; ; current = path.dirname(current)) {
    if (fs.existsSync(current) && fs.lstatSync(current).isSymbolicLink()) throw new Error("Linked staging path");
    if (path.dirname(current) === current) break;
  }
  fs.mkdirSync(destination, { recursive: true });
  const identityPath = path.join(destination, ".nxp-cache-identity");
  if (!cacheHit) fs.writeFileSync(identityPath, slot, { flag: "wx" });
  if (fs.readFileSync(identityPath, "utf8") !== slot) throw new Error("Cache identity collision");
  const leasePath = path.join(destination, ".nxp-test-lease.json");
  const leaseBytes = JSON.stringify({ pid: process.pid, startedAt: new Date().toISOString(), fingerprint });
  fs.writeFileSync(leasePath, leaseBytes, { flag: "wx" });
  const inventoryPath = path.join(destination, ".nxp-source-inventory.json");
  const previous = incremental && cacheHit ? JSON.parse(fs.readFileSync(inventoryPath, "utf8")) : [];
  const owned = new Map(previous.map(item => [item.relative, item.sha256]));
  const safeTarget = relative => {
    const target = path.resolve(destination, relative);
    if (!relative || path.isAbsolute(relative) || path.relative(destination, target).startsWith("..") || target === destination)
      throw new Error("Invalid staged source path");
    for (let current = target; current !== destination; current = path.dirname(current)) {
      if (fs.existsSync(current) && fs.lstatSync(current).isSymbolicLink()) throw new Error("Linked staged source");
    }
    return target;
  };
  for (const item of previous) {
    const target = safeTarget(item.relative);
    if (!fs.existsSync(target) || !fs.statSync(target).isFile() || sha256(fs.readFileSync(target)) !== item.sha256)
      throw new Error(`Staged source changed outside the runner: ${item.relative}`);
  }
  const currentFiles = new Set(entries.map(item => item.relative));
  if (incremental && cacheHit) {
    const generated = new Set(["bin", "obj", "node_modules", "dist", "out", ".generated", ".git"]);
    const metadata = new Set([".nxp-cache-identity", ".nxp-test-lease.json", ".nxp-source-inventory.json"]);
    const inspect = directory => {
      for (const entry of fs.readdirSync(directory, { withFileTypes: true })) {
        const file = path.join(directory, entry.name), relative = path.relative(destination, file).replaceAll("\\", "/");
        if (entry.isSymbolicLink()) throw new Error("Linked staged directory");
        if (entry.isDirectory()) { if (!generated.has(entry.name)) inspect(file); }
        else if (!owned.has(relative) && !metadata.has(relative)) throw new Error(`Unowned staged file: ${relative}`);
      }
    };
    inspect(destination);
  }
  let copiedBytes = 0, copiedFiles = 0, removedFiles = 0;
  for (const item of previous) {
    if (!currentFiles.has(item.relative)) { fs.unlinkSync(safeTarget(item.relative)); removedFiles++; }
  }
  for (const { relative, bytes } of entries) {
    budget.check();
    const target = path.join(destination, relative);
    if (incremental && cacheHit && !owned.has(relative) && fs.existsSync(target)) throw new Error(`Unowned staged source: ${relative}`);
    for (let parent = path.dirname(target); parent !== destination; parent = path.dirname(parent)) {
      if (fs.existsSync(parent) && fs.lstatSync(parent).isSymbolicLink()) throw new Error("Linked staged directory");
    }
    fs.mkdirSync(path.dirname(target), { recursive: true });
    if (fs.existsSync(target) && fs.lstatSync(target).isSymbolicLink()) throw new Error("Linked staged file");
    if (!fs.existsSync(target) || !fs.readFileSync(target).equals(bytes)) {
      fs.writeFileSync(target, bytes); copiedFiles++; copiedBytes += bytes.length;
    }
  }
  if (incremental) {
    const inventory = JSON.stringify(entries.map(item => ({ relative: item.relative, sha256: sha256(item.bytes) })));
    if (!fs.existsSync(inventoryPath) || fs.readFileSync(inventoryPath, "utf8") !== inventory) fs.writeFileSync(inventoryPath, inventory);
  }
  const staging = { incremental, copiedBytes, copiedFiles, removedFiles, slot };
  console.error(`[workspace] ${directoryName} ${JSON.stringify(staging)}`);
  const dirty = Boolean(git("status", "--porcelain").trim());
  return { directory: destination, fingerprint, sourceFingerprint, cacheHit, staging, preparationMs: budget.elapsedMs - started,
    toolchain: { node: process.version, dotnet },
    release() {
      if (fs.readFileSync(leasePath, "utf8") !== leaseBytes) throw new Error("Staging ownership changed");
      fs.unlinkSync(leasePath);
    }, source: {
    commitSha: git("rev-parse", "HEAD").trim(), treeSha: git("rev-parse", "HEAD^{tree}").trim(),
    workingTreeDirty: dirty, dirtyFingerprint: dirty ? sourceFingerprint : null,
  } };
}
