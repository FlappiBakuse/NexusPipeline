import crypto from "node:crypto";
import { execFileSync } from "node:child_process";
import fs from "node:fs";
import path from "node:path";
import { fileURLToPath } from "node:url";

const projectRoot = path.resolve(path.dirname(fileURLToPath(import.meta.url)), "..");
const mode = process.argv[2] || "--production";
if (!["--production", "--frontend"].includes(mode) || process.argv.length > 3) {
  throw new Error("用法：node tools/source-hash.mjs [--production|--frontend]");
}
const inputs = mode === "--frontend"
  ? ["frontend", "tools/source-hash.mjs"]
  : ["src", "frontend", "Directory.Build.props", "build.cmd", "tools/source-hash.mjs"];

function collectFiles(root) {
  const files = [];
  if (!fs.existsSync(root)) return files;

  for (const entry of fs.readdirSync(root, { withFileTypes: true })) {
    if (entry.name === "bin" || entry.name === "obj" || entry.name === "node_modules" || entry.name === "dist") continue;
    const absolute = path.join(root, entry.name);
    if (entry.isDirectory()) {
      files.push(...collectFiles(absolute));
    } else if (entry.isFile()) {
      files.push(absolute);
    }
  }
  return files;
}

const files = inputs
  .flatMap(relative => {
    const absolute = path.join(projectRoot, relative);
    return fs.statSync(absolute).isDirectory() ? collectFiles(absolute) : [absolute];
  })
  .sort((left, right) => {
    const a = path.relative(projectRoot, left).split(path.sep).join("/");
    const b = path.relative(projectRoot, right).split(path.sep).join("/");
    return Buffer.from(a, "utf8").compare(Buffer.from(b, "utf8"));
  });

const toolchain = [mode, process.platform, process.arch, process.version];
if (mode === "--production") {
  toolchain.push(execFileSync("dotnet", ["--version"], { cwd: projectRoot, encoding: "utf8" }).trim());
}
const manifest = [toolchain.join("\0"), ...files.map(file => {
  const relative = path.relative(projectRoot, file).split(path.sep).join("/");
  const digest = crypto.createHash("sha256").update(fs.readFileSync(file)).digest("hex");
  return `${relative}\0${digest}`;
})].join("\n");

const result = crypto.createHash("sha256").update(manifest, "utf8").digest("hex").toUpperCase();
process.stdout.write(`${result}\n`);
