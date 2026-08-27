import crypto from "node:crypto";
import fs from "node:fs";
import path from "node:path";
import { fileURLToPath } from "node:url";

const projectRoot = path.resolve(path.dirname(fileURLToPath(import.meta.url)), "..");
const inputRoots = ["src"];

function collectFiles(root) {
  const files = [];
  if (!fs.existsSync(root)) return files;

  for (const entry of fs.readdirSync(root, { withFileTypes: true })) {
    if (entry.name === "bin" || entry.name === "obj") continue;
    const absolute = path.join(root, entry.name);
    if (entry.isDirectory()) {
      files.push(...collectFiles(absolute));
    } else if (entry.isFile()) {
      files.push(absolute);
    }
  }
  return files;
}

const files = inputRoots
  .flatMap(relative => collectFiles(path.join(projectRoot, relative)))
  .sort((left, right) => {
    const a = path.relative(projectRoot, left).split(path.sep).join("/");
    const b = path.relative(projectRoot, right).split(path.sep).join("/");
    return Buffer.from(a, "utf8").compare(Buffer.from(b, "utf8"));
  });

const manifest = files.map(file => {
  const relative = path.relative(projectRoot, file).split(path.sep).join("/");
  const digest = crypto.createHash("sha256").update(fs.readFileSync(file)).digest("hex");
  return `${relative}\0${digest}`;
}).join("\n");

const result = crypto.createHash("sha256").update(manifest, "utf8").digest("hex").toUpperCase();
process.stdout.write(`${result}\n`);
