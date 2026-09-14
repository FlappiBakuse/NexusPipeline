import type { ScriptDraft } from "./scriptTypes";

export const SCRIPT_EXPORT_KIND = "nexus-pipeline.script-export" as const;
export const SCRIPT_EXPORT_SCHEMA_VERSION = 1 as const;
export const MAX_SCRIPT_IMPORT_BYTES = 1024 * 1024;
export const SCRIPT_NAME_MAX_BYTES = 64;

export type ScriptPathField = "mainExe" | "configPath" | "logPath";
export type ScriptExportPathKind = "relative" | "absolute";

export interface ScriptExportPath {
  kind: ScriptExportPathKind;
  value: string;
}

export interface ScriptExportScript {
  name: string;
  args: string;
  launchGame: boolean;
  gameMode: "pc" | "emulator";
  gameArgs: string;
  gameWaitSeconds: number;
  forceCloseGame: boolean;
  maxAttempts: number;
  logStallTimeoutMinutes: number;
  totalTimeoutMinutes: number;
  successKeywords: string;
  failureKeywords: string;
  judgeScriptEnabled: boolean;
  judgeScriptLanguage: "javascript" | "python";
  judgeScript: string;
  autoUpdateConfig: boolean;
}

export interface ScriptExportFileV1 {
  kind: typeof SCRIPT_EXPORT_KIND;
  schemaVersion: typeof SCRIPT_EXPORT_SCHEMA_VERSION;
  exportedAt: string;
  script: ScriptExportScript;
  paths: Record<ScriptPathField, ScriptExportPath>;
}

export interface ScriptImportWarning {
  field: ScriptPathField;
  value: string;
}

export interface ParsedScriptImport {
  file: ScriptExportFileV1;
  pendingRelativePaths: Partial<Record<ScriptPathField, string>>;
  warnings: ScriptImportWarning[];
}

export type ScriptImportErrorCode =
  | "file_too_large"
  | "invalid_json"
  | "invalid_shape"
  | "wrong_kind"
  | "unsupported_version"
  | "invalid_field"
  | "invalid_mode"
  | "invalid_path";

export type ScriptImportResult =
  | { ok: true; value: ParsedScriptImport }
  | { ok: false; code: ScriptImportErrorCode; field?: string };

const PATH_FIELDS: ScriptPathField[] = ["mainExe", "configPath", "logPath"];
const STRING_FIELDS: Array<keyof ScriptExportScript> = [
  "name",
  "args",
  "gameArgs",
  "successKeywords",
  "failureKeywords",
  "judgeScript",
];
const BOOLEAN_FIELDS: Array<keyof ScriptExportScript> = [
  "launchGame",
  "forceCloseGame",
  "judgeScriptEnabled",
  "autoUpdateConfig",
];
const NUMBER_FIELDS: Array<keyof ScriptExportScript> = [
  "gameWaitSeconds",
  "maxAttempts",
  "logStallTimeoutMinutes",
  "totalTimeoutMinutes",
];

function isRecord(value: unknown): value is Record<string, any> {
  return typeof value === "object" && value !== null && !Array.isArray(value);
}

function hasOwn(value: Record<string, any>, key: string): boolean {
  return Object.prototype.hasOwnProperty.call(value, key);
}

function cleanPath(value: unknown): string {
  const trimmed = String(value ?? "").trim();
  if (trimmed.length >= 2) {
    const first = trimmed[0];
    const last = trimmed[trimmed.length - 1];
    if ((first === '"' && last === '"') || (first === "'" && last === "'")) {
      return trimmed.slice(1, -1).trim();
    }
  }
  return trimmed;
}

interface WindowsPathParts {
  root: string;
  segments: string[];
  absolute: boolean;
}

function normalizeSegments(rawSegments: string[], absolute: boolean): string[] {
  const segments: string[] = [];
  for (const segment of rawSegments) {
    if (!segment || segment === ".") continue;
    if (segment === "..") {
      if (segments.length && segments[segments.length - 1] !== "..") segments.pop();
      else if (!absolute) segments.push(segment);
      continue;
    }
    segments.push(segment);
  }
  return segments;
}

function parseWindowsPath(value: string): WindowsPathParts {
  const normalized = cleanPath(value).replaceAll("/", "\\");
  if (!normalized) return { root: "", segments: [], absolute: false };

  const drive = normalized.match(/^([A-Za-z]):(?:\\|$)/);
  if (drive) {
    return {
      root: `${drive[1].toLowerCase()}:`,
      segments: normalizeSegments(normalized.slice(2).split("\\"), true),
      absolute: true,
    };
  }
  if (/^[A-Za-z]:/.test(normalized)) {
    return { root: normalized.slice(0, 2).toLowerCase(), segments: normalized.slice(2).split("\\"), absolute: false };
  }

  if (normalized.startsWith("\\\\")) {
    const parts = normalized.slice(2).split("\\").filter(Boolean);
    if (parts.length >= 2) {
      return {
        root: `unc:${parts[0].toLowerCase()}:${parts[1].toLowerCase()}`,
        segments: normalizeSegments(parts.slice(2), true),
        absolute: true,
      };
    }
    return { root: "unc:", segments: normalizeSegments(parts, true), absolute: true };
  }
  if (normalized.startsWith("\\")) {
    return { root: "rooted", segments: normalizeSegments(normalized.slice(1).split("\\"), true), absolute: true };
  }
  return { root: "relative", segments: normalizeSegments(normalized.split("\\"), false), absolute: false };
}

function samePrefix(path: WindowsPathParts, root: WindowsPathParts): boolean {
  return path.absolute && root.absolute && path.root === root.root;
}

function isWithinRoot(path: WindowsPathParts, root: WindowsPathParts): boolean {
  if (!samePrefix(path, root) || path.segments.length < root.segments.length) return false;
  return root.segments.every((segment, index) => segment.toLowerCase() === path.segments[index].toLowerCase());
}

function isSafeRelativePath(value: string): boolean {
  const trimmed = value.trim();
  if (!trimmed || trimmed === ".") return trimmed === ".";
  const normalized = trimmed.replaceAll("/", "\\");
  if (normalized.startsWith("\\") || /^[A-Za-z]:/.test(normalized)) return false;
  const segments = normalized.split("\\");
  return segments.every(segment => segment.length > 0 && segment !== "." && segment !== "..");
}

function invalid(code: ScriptImportErrorCode, field?: string): ScriptImportResult {
  return { ok: false, code, field };
}

/** 将当前 Windows 路径转为带根目录边界语义的导出描述符。 */
export function relativizeScriptPath(path: string, root: string): ScriptExportPath {
  const source = cleanPath(path);
  const pathParts = parseWindowsPath(source);
  const rootParts = parseWindowsPath(root);
  if (!source || !isWithinRoot(pathParts, rootParts)) return { kind: "absolute", value: source };
  const relative = pathParts.segments.slice(rootParts.segments.length).join("/");
  return { kind: "relative", value: relative || "." };
}

/** 将安全的相对导入路径派生到用户当前填写的脚本根目录。 */
export function deriveImportedPath(path: ScriptExportPath, root: string): string {
  if (path.kind === "absolute") return path.value;
  const base = cleanPath(root).replaceAll("/", "\\");
  if (!base) return "";
  if (path.value === ".") return base;
  const trimmedBase = /^[A-Za-z]:\\$/.test(base) || base === "\\"
    ? base
    : base.replace(/[\\]+$/, "");
  const separator = trimmedBase.endsWith("\\") ? "" : "\\";
  return `${trimmedBase}${separator}${path.value.replaceAll("/", "\\")}`;
}

function validateScriptObject(script: Record<string, any>): ScriptImportResult | null {
  for (const field of STRING_FIELDS) {
    if (!hasOwn(script, field) || typeof script[field] !== "string") return invalid("invalid_field", `script.${String(field)}`);
  }
  if (!hasOwn(script, "gameMode") || (script.gameMode !== "pc" && script.gameMode !== "emulator")) {
    return invalid("invalid_mode", "script.gameMode");
  }
  if (!hasOwn(script, "judgeScriptLanguage") || (script.judgeScriptLanguage !== "javascript" && script.judgeScriptLanguage !== "python")) {
    return invalid("invalid_mode", "script.judgeScriptLanguage");
  }
  for (const field of BOOLEAN_FIELDS) {
    if (!hasOwn(script, field) || typeof script[field] !== "boolean") return invalid("invalid_field", `script.${String(field)}`);
  }
  for (const field of NUMBER_FIELDS) {
    if (!hasOwn(script, field) || typeof script[field] !== "number" || !Number.isFinite(script[field])) {
      return invalid("invalid_field", `script.${String(field)}`);
    }
  }
  return null;
}

/** 完整验证 v1 文件，成功后才返回可应用到草稿的独立对象。 */
export function parseScriptImport(input: string): ScriptImportResult {
  if (new TextEncoder().encode(input).byteLength > MAX_SCRIPT_IMPORT_BYTES) return invalid("file_too_large");
  let parsed: unknown;
  try {
    parsed = JSON.parse(input);
  } catch {
    return invalid("invalid_json");
  }
  if (!isRecord(parsed)) return invalid("invalid_shape");
  if (parsed.kind !== SCRIPT_EXPORT_KIND) return invalid("wrong_kind", "kind");
  if (parsed.schemaVersion !== SCRIPT_EXPORT_SCHEMA_VERSION) return invalid("unsupported_version", "schemaVersion");
  if (typeof parsed.exportedAt !== "string" || !isRecord(parsed.script) || !isRecord(parsed.paths)) return invalid("invalid_shape");

  const scriptError = validateScriptObject(parsed.script);
  if (scriptError) return scriptError;

  const paths = {} as Record<ScriptPathField, ScriptExportPath>;
  const pendingRelativePaths: Partial<Record<ScriptPathField, string>> = {};
  const warnings: ScriptImportWarning[] = [];
  for (const field of PATH_FIELDS) {
    const value = parsed.paths[field];
    if (!isRecord(value) || (value.kind !== "relative" && value.kind !== "absolute") || typeof value.value !== "string") {
      return invalid("invalid_path", `paths.${field}`);
    }
    if (value.kind === "relative" && !isSafeRelativePath(value.value)) return invalid("invalid_path", `paths.${field}`);
    const descriptor: ScriptExportPath = { kind: value.kind, value: value.value };
    paths[field] = descriptor;
    if (descriptor.kind === "relative") pendingRelativePaths[field] = descriptor.value;
    else warnings.push({ field, value: descriptor.value });
  }

  const file: ScriptExportFileV1 = {
    kind: SCRIPT_EXPORT_KIND,
    schemaVersion: SCRIPT_EXPORT_SCHEMA_VERSION,
    exportedAt: parsed.exportedAt,
    script: {
      name: parsed.script.name,
      args: parsed.script.args,
      launchGame: parsed.script.launchGame,
      gameMode: parsed.script.gameMode,
      gameArgs: parsed.script.gameArgs,
      gameWaitSeconds: parsed.script.gameWaitSeconds,
      forceCloseGame: parsed.script.forceCloseGame,
      maxAttempts: parsed.script.maxAttempts,
      logStallTimeoutMinutes: parsed.script.logStallTimeoutMinutes,
      totalTimeoutMinutes: parsed.script.totalTimeoutMinutes,
      successKeywords: parsed.script.successKeywords,
      failureKeywords: parsed.script.failureKeywords,
      judgeScriptEnabled: parsed.script.judgeScriptEnabled,
      judgeScriptLanguage: parsed.script.judgeScriptLanguage,
      judgeScript: parsed.script.judgeScript,
      autoUpdateConfig: parsed.script.autoUpdateConfig,
    },
    paths,
  };
  return { ok: true, value: { file, pendingRelativePaths, warnings } };
}

function utf8Bytes(value: string): number {
  return new TextEncoder().encode(value).byteLength;
}

export function truncateUtf8(value: string, maxBytes = SCRIPT_NAME_MAX_BYTES): string {
  let result = "";
  for (const character of value) {
    if (utf8Bytes(result + character) > maxBytes) break;
    result += character;
  }
  return result;
}

/** 生成后端可接受的导入名称，比较与后端一致地忽略大小写。 */
export function makeUniqueImportedName(name: string, existingNames: string[] = []): string {
  const original = String(name ?? "").trim() || "Imported script";
  const normalizedExisting = new Set(existingNames.map(value => String(value ?? "").trim().toLowerCase()).filter(Boolean));
  const fits = (value: string) => !normalizedExisting.has(value.toLowerCase());
  const base = truncateUtf8(original);
  if (fits(base)) return base;
  for (let suffixIndex = 2; ; suffixIndex += 1) {
    const suffix = `-${suffixIndex}`;
    const candidate = `${truncateUtf8(original, SCRIPT_NAME_MAX_BYTES - utf8Bytes(suffix))}${suffix}`;
    if (fits(candidate)) return candidate;
  }
}

/** 构建不含机器位置、插件字段和实例标识的通用脚本导出文件。 */
export function buildScriptExport(draft: ScriptDraft, now = new Date()): ScriptExportFileV1 {
  return {
    kind: SCRIPT_EXPORT_KIND,
    schemaVersion: SCRIPT_EXPORT_SCHEMA_VERSION,
    exportedAt: now.toISOString(),
    script: {
      name: draft.name.trim(),
      args: draft.args,
      launchGame: draft.launchGame,
      gameMode: draft.gameMode === "emulator" ? "emulator" : "pc",
      gameArgs: draft.gameArgs,
      gameWaitSeconds: Number(draft.gameWaitSeconds),
      forceCloseGame: draft.forceCloseGame,
      maxAttempts: Number(draft.maxAttempts),
      logStallTimeoutMinutes: Number(draft.logStallTimeoutMinutes),
      totalTimeoutMinutes: Number(draft.totalTimeoutMinutes),
      successKeywords: draft.successKeywords,
      failureKeywords: draft.failureKeywords,
      judgeScriptEnabled: draft.judgeScriptEnabled,
      judgeScriptLanguage: draft.judgeScriptLanguage === "python" ? "python" : "javascript",
      judgeScript: draft.judgeScript,
      autoUpdateConfig: draft.autoUpdateConfig,
    },
    paths: {
      mainExe: relativizeScriptPath(draft.mainExe, draft.rootPath),
      configPath: relativizeScriptPath(draft.configPath, draft.rootPath),
      logPath: relativizeScriptPath(draft.logPath, draft.rootPath),
    },
  };
}
