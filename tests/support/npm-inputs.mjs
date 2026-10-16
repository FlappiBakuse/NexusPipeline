import fs from "node:fs";
import path from "node:path";
import { createHash } from "node:crypto";
import { execFileSync } from "node:child_process";

let npmVersion;

export function npmIdentity(directory, args = []) {
  npmVersion ??= process.platform === "win32"
    ? execFileSync(process.env.ComSpec || "cmd.exe", ["/d", "/s", "/c", "npm --version"], { encoding: "utf8", windowsHide: true }).trim()
    : execFileSync("npm", ["--version"], { encoding: "utf8" }).trim();
  const hash = createHash("sha256");
  hash.update(JSON.stringify([process.version, npmVersion, process.platform, process.arch, args,
    Object.entries(process.env).filter(([key]) => key.toLowerCase().startsWith("npm_config_") && key.toLowerCase() !== "npm_config_cache").sort()]));
  for (const name of ["package.json", "package-lock.json", ".npmrc"]) {
    const file = path.join(directory, name);
    hash.update(name); hash.update(fs.existsSync(file) ? fs.readFileSync(file) : "absent");
  }
  return hash.digest("hex");
}

export async function ensureNpmInputs(directory, run, args = []) {
  const identity = npmIdentity(directory, args);
  const stamp = path.join(directory, "node_modules", ".nxp-install.json");
  const lock = path.join(directory, "node_modules", ".package-lock.json");
  if (fs.existsSync(stamp) && fs.existsSync(lock)) {
    const previous = JSON.parse(fs.readFileSync(stamp, "utf8"));
    const installed = createHash("sha256").update(fs.readFileSync(lock)).digest("hex");
    if (previous.identity === identity && previous.installed === installed) {
      console.error(`[dependencies] reuse ${directory}`); return 0;
    }
  }
  const code = await run(process.platform === "win32" ? "npm.cmd" : "npm", ["ci", ...args, "--no-audit", "--no-fund"], { cwd: directory });
  if (code) return code;
  if (!fs.existsSync(lock)) throw new Error("npm ci did not produce its installed lock");
  fs.writeFileSync(stamp, JSON.stringify({ identity, installed: createHash("sha256").update(fs.readFileSync(lock)).digest("hex") }));
  return 0;
}
