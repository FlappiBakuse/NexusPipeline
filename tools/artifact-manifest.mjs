import crypto from "node:crypto";
import fs from "node:fs";
import path from "node:path";

export const ARTIFACT_MANIFEST_SCHEMA_VERSION = 1;

function sha256Buffer(value) {
  return crypto.createHash("sha256").update(value).digest("hex");
}

function sha256Json(value) {
  return sha256Buffer(Buffer.from(JSON.stringify(value), "utf8"));
}

function normalizeRelativePath(value) {
  if (typeof value !== "string") return "";
  const normalized = value.trim().replaceAll("\\", "/").replace(/^\.\//u, "");
  if (!normalized || normalized.startsWith("/") || /^[A-Za-z]:\//u.test(normalized)) return "";
  if (normalized.split("/").some(segment => segment === "..")) return "";
  return normalized;
}

function walkFiles(root) {
  if (!root || !fs.existsSync(root)) return [];
  const files = [];
  const visit = directory => {
    for (const entry of fs.readdirSync(directory, { withFileTypes: true })) {
      if (entry.name === ".artifact-manifest.json") continue;
      const fullPath = path.join(directory, entry.name);
      if (entry.isDirectory()) visit(fullPath);
      else if (entry.isFile()) files.push(fullPath);
    }
  };
  visit(root);
  return files;
}

function entryDigest(root, file) {
  const relativePath = normalizeRelativePath(path.relative(root, file));
  if (!relativePath) throw new Error(`产物路径无效：${file}`);
  const bytes = fs.readFileSync(file);
  return {
    relativePath,
    byteLength: bytes.byteLength,
    sha256: sha256Buffer(bytes),
  };
}

export function artifactSetDigest(files) {
  return sha256Json(files.map(file => ({
    relativePath: file.relativePath,
    byteLength: file.byteLength,
    sha256: file.sha256,
  })));
}

export function collectArtifactFiles(root) {
  const resolvedRoot = path.resolve(root);
  if (!fs.existsSync(resolvedRoot)) throw new Error(`产物目录不存在：${resolvedRoot}`);
  const files = walkFiles(resolvedRoot)
    .map(file => entryDigest(resolvedRoot, file))
    .sort((left, right) => left.relativePath.localeCompare(right.relativePath));
  const seen = new Set();
  for (const file of files) {
    const key = file.relativePath.toLowerCase();
    if (seen.has(key)) throw new Error(`产物清单存在大小写冲突路径：${file.relativePath}`);
    seen.add(key);
  }
  return files;
}

export function createArtifactManifest({
  root,
  kind,
  mode,
  producer,
  sourceRepository = "",
  sourceSha = "",
  sourceDigest = "",
  buildInputsDigest = "",
  counterpartRepository = "",
  counterpartSha = "",
  buildFingerprint = "",
  buildInvocation = [],
  toolchain = {},
  observedTimeScale = "",
  metadata = {},
}) {
  const files = collectArtifactFiles(root);
  return {
    schemaVersion: ARTIFACT_MANIFEST_SCHEMA_VERSION,
    present: true,
    kind: String(kind || "runtime"),
    mode: String(mode || ""),
    producer: String(producer || ""),
    sourceRepository: String(sourceRepository || ""),
    sourceSha: String(sourceSha || ""),
    sourceDigest: String(sourceDigest || ""),
    buildInputsDigest: String(buildInputsDigest || ""),
    counterpartRepository: String(counterpartRepository || ""),
    counterpartSha: String(counterpartSha || ""),
    buildFingerprint: String(buildFingerprint || ""),
    buildInvocation: Array.isArray(buildInvocation) ? [...buildInvocation] : [String(buildInvocation || "")],
    toolchain: toolchain && typeof toolchain === "object" ? { ...toolchain } : {},
    observedTimeScale: String(observedTimeScale || ""),
    files,
    artifactSetDigest: artifactSetDigest(files),
    ...(metadata && typeof metadata === "object" ? { metadata: { ...metadata } } : {}),
  };
}

export function createNoArtifactManifest({ mode, producer, reason, metadata = {} } = {}) {
  return {
    schemaVersion: ARTIFACT_MANIFEST_SCHEMA_VERSION,
    present: false,
    kind: "none",
    mode: String(mode || ""),
    producer: String(producer || ""),
    reason: String(reason || "未生成二进制产物"),
    files: [],
    artifactSetDigest: null,
    ...(metadata && typeof metadata === "object" ? { metadata: { ...metadata } } : {}),
  };
}

export function validateArtifactManifestShape(manifest, { requirePresent = false } = {}) {
  const issues = [];
  if (!manifest || typeof manifest !== "object" || Array.isArray(manifest)) return ["artifactManifest 不是对象"];
  if (manifest.schemaVersion !== ARTIFACT_MANIFEST_SCHEMA_VERSION) issues.push("artifactManifest schemaVersion 无效");
  if (typeof manifest.present !== "boolean") issues.push("artifactManifest 缺少 present");
  if (requirePresent && manifest.present !== true) issues.push("artifactManifest 未声明真实产物");
  if (typeof manifest.kind !== "string" || !manifest.kind) issues.push("artifactManifest 缺少 kind");
  if (typeof manifest.mode !== "string" || !manifest.mode) issues.push("artifactManifest 缺少 mode");
  if (!Array.isArray(manifest.files)) issues.push("artifactManifest 缺少 files");
  if (manifest.present === true) {
    if (!/^[0-9a-f]{64}$/u.test(String(manifest.artifactSetDigest || ""))) issues.push("artifactManifest artifactSetDigest 无效");
    if (Array.isArray(manifest.files) && manifest.files.length === 0) issues.push("真实产物 manifest 不得为空");
    const seen = new Set();
    for (const file of Array.isArray(manifest.files) ? manifest.files : []) {
      const relativePath = normalizeRelativePath(file?.relativePath);
      if (!relativePath) issues.push("artifactManifest 包含无效相对路径");
      const key = relativePath.toLowerCase();
      if (seen.has(key)) issues.push(`artifactManifest 包含重复路径：${relativePath}`);
      seen.add(key);
      if (!Number.isSafeInteger(file?.byteLength) || file.byteLength < 0) issues.push(`artifactManifest byteLength 无效：${relativePath}`);
      if (!/^[0-9a-f]{64}$/u.test(String(file?.sha256 || ""))) issues.push(`artifactManifest sha256 无效：${relativePath}`);
    }
    if (Array.isArray(manifest.files) && artifactSetDigest(manifest.files) !== manifest.artifactSetDigest) issues.push("artifactManifest artifactSetDigest 校验失败");
  } else if (manifest.artifactSetDigest !== null) {
    issues.push("无产物 manifest 的 artifactSetDigest 必须为 null");
  }
  return issues;
}

export function verifyArtifactManifest(root, manifest) {
  const issues = validateArtifactManifestShape(manifest, { requirePresent: true });
  if (issues.length) return { ok: false, issues };
  let actual;
  try {
    actual = collectArtifactFiles(root);
  } catch (error) {
    return { ok: false, issues: [error.message] };
  }
  if (JSON.stringify(actual) !== JSON.stringify(manifest.files)) {
    return { ok: false, issues: ["产物文件集合或字节摘要与 manifest 不一致"] };
  }
  return { ok: true, issues: [], artifactSetDigest: manifest.artifactSetDigest };
}

export function writeArtifactManifest(filePath, manifest) {
  const destination = path.resolve(filePath);
  fs.mkdirSync(path.dirname(destination), { recursive: true });
  fs.writeFileSync(destination, `${JSON.stringify(manifest, null, 2)}\n`, "utf8");
  return destination;
}
