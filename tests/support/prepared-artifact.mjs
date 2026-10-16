import fs from "node:fs";
import path from "node:path";
import { createHash } from "node:crypto";

function inventory(root) {
  if (fs.lstatSync(root).isSymbolicLink()) throw new Error("Linked artifact output");
  const files = [];
  function visit(directory) {
    for (const entry of fs.readdirSync(directory, { withFileTypes: true }).sort((a, b) => a.name.localeCompare(b.name))) {
      const file = path.join(directory, entry.name);
      if (entry.isSymbolicLink()) throw new Error("Linked prepared artifact");
      if (entry.isDirectory()) visit(file);
      else if (entry.isFile()) files.push([path.relative(root, file), fs.statSync(file).size,
        createHash("sha256").update(fs.readFileSync(file)).digest("hex")]);
      else throw new Error("Unsupported prepared artifact entry");
    }
  }
  visit(root); return files;
}

const pending = new Map();
export function preparedArtifact(root, identity, build) {
  const key = createHash("sha256").update(JSON.stringify(identity)).digest("hex");
  const id = path.resolve(root, key);
  if (!pending.has(id)) pending.set(id, prepare(root, identity, key, build).finally(() => pending.delete(id)));
  return pending.get(id);
}

async function prepare(root, identity, key, build) {
  const directory = path.join(root, key.slice(0, 16)), output = path.join(directory, "output"), receipt = path.join(directory, "complete.json");
  for (let current = directory; ; current = path.dirname(current)) {
    if (fs.existsSync(current) && fs.lstatSync(current).isSymbolicLink()) throw new Error("Linked artifact cache path");
    if (path.dirname(current) === current) break;
  }
  if (fs.existsSync(directory)) {
    if (fs.lstatSync(receipt).isSymbolicLink()) throw new Error("Linked artifact receipt");
    const saved = JSON.parse(fs.readFileSync(receipt, "utf8"));
    if (saved.key !== key || JSON.stringify(saved.identity) !== JSON.stringify(identity) || JSON.stringify(saved.files) !== JSON.stringify(inventory(output)))
      throw new Error(`Prepared artifact changed: ${directory}`);
    console.error(`[artifact] reuse ${key}`);
    return { code: 0, output, cacheHit: true, key };
  }
  fs.mkdirSync(directory, { recursive: true });
  const code = await build(output);
  if (code) return { code, output, cacheHit: false, key };
  const files = inventory(output);
  if (!files.length) throw new Error("Empty prepared artifact");
  fs.writeFileSync(receipt, JSON.stringify({ key, identity, files }), { flag: "wx" });
  console.error(`[artifact] built ${key}`);
  return { code: 0, output, cacheHit: false, key };
}
