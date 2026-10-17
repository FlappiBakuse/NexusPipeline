import fs from "node:fs";
import path from "node:path";
import { createHash } from "node:crypto";

const hash = bytes => createHash("sha256").update(bytes).digest("hex");
const object = value => value !== null && typeof value === "object" && !Array.isArray(value);

export function loadJsonInput(relative, read) {
  const files = [];
  let total = 0;
  const readValue = name => {
    const raw = Buffer.from(read(name));
    total += raw.length;
    if (raw.length > 4 * 1024 * 1024 || total > 8 * 1024 * 1024 || raw.subarray(0, 3).equals(Buffer.from([239, 187, 191])))
      throw new Error(`Invalid JSON input size or BOM: ${name}`);
    const bytes = Buffer.from(new TextDecoder("utf-8", { fatal: true }).decode(raw).replaceAll("\r\n", "\n"));
    files.push({ path: name, sha256: hash(bytes) });
    return { value: JSON.parse(bytes), bytes };
  };
  const input = readValue(relative);
  if (!object(input.value) || !Object.hasOwn(input.value, "partitionVersion"))
    return { ...input, digest: hash(input.bytes), files };
  const manifest = input.value;
  if (manifest.partitionVersion !== 1 || !Array.isArray(manifest.parts) || manifest.parts.length < 1 || manifest.parts.length > 64)
    throw new Error("Unsupported JSON partition manifest");
  const prefix = relative.replace(/\.json$/, "") + "/";
  const sections = new Map(), seen = new Set([relative.toLowerCase()]);
  for (const part of manifest.parts) {
    if (!object(part) || Object.keys(part).sort().join(",") !== "path,section"
        || !["groups", "topics"].includes(part.section) || typeof part.path !== "string"
        || !part.path.startsWith(prefix) || !/^[a-z0-9][a-z0-9.-]*\.json$/.test(part.path.slice(prefix.length))
        || seen.has(part.path.toLowerCase()) || Object.hasOwn(manifest, part.section))
      throw new Error("Invalid, duplicate or escaping JSON part");
    seen.add(part.path.toLowerCase());
    const value = readValue(part.path).value;
    if (part.section === "topics") {
      if (!Array.isArray(value)) throw new Error("Topics part must be an array");
      sections.set(part.section, [...(sections.get(part.section) ?? []), ...value]);
    } else {
      if (!object(value)) throw new Error("Groups part must be an object");
      const merged = sections.get(part.section) ?? Object.create(null);
      for (const [key, entry] of Object.entries(value)) {
        if (Object.hasOwn(merged, key)) throw new Error(`Duplicate JSON group: ${key}`);
        merged[key] = entry;
      }
      sections.set(part.section, merged);
    }
  }
  if (sections.has("topics") && manifest.order === undefined) throw new Error("Missing JSON topic order");
  if (manifest.order !== undefined) {
    if (!object(manifest.order) || Object.keys(manifest.order).join(",") !== "topics" || !sections.has("topics"))
      throw new Error("Invalid JSON topic order");
    const topics = sections.get("topics"), order = manifest.order.topics;
    const indexed = new Map(topics.map(topic => [topic?.id, topic]));
    if (!Array.isArray(order) || indexed.size !== topics.length || indexed.has(undefined)
        || order.length !== topics.length || new Set(order).size !== order.length || order.some(id => !indexed.has(id)))
      throw new Error("Incomplete or duplicate JSON topic identities");
    sections.set("topics", order.map(id => indexed.get(id)));
  }
  const value = Object.create(null);
  for (const [key, entry] of Object.entries(manifest)) {
    if (key === "partitionVersion" || key === "order") continue;
    if (key === "parts") for (const [section, entries] of sections) value[section] = entries;
    else value[key] = entry;
  }
  return { value, bytes: Buffer.from(JSON.stringify(value, null, 2) + "\n"), digest: hash(JSON.stringify(files)), files };
}

export function readJsonInput(root, relative) {
  const directory = path.resolve(root);
  return loadJsonInput(relative, name => {
    const file = path.resolve(directory, name);
    if (!file.startsWith(directory + path.sep)) throw new Error("JSON input escapes repository");
    for (let entry = file; entry !== directory; entry = path.dirname(entry))
      if (fs.lstatSync(entry).isSymbolicLink()) throw new Error("Linked JSON input");
    if (!fs.statSync(file).isFile()) throw new Error("JSON input is not a regular file");
    return fs.readFileSync(file);
  });
}
