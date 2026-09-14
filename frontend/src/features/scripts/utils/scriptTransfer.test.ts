import { describe, expect, it } from "vitest";
import {
  buildScriptExport,
  deriveImportedPath,
  makeUniqueImportedName,
  MAX_SCRIPT_IMPORT_BYTES,
  parseScriptImport,
  relativizeScriptPath,
  SCRIPT_EXPORT_KIND,
  truncateUtf8,
} from "./scriptTransfer";
import { emptyScriptDraft } from "./scriptTypes";

function validFile() {
  return buildScriptExport({
    ...emptyScriptDraft,
    name: "通用脚本",
    rootPath: "C:\\Tools",
    mainExe: "C:\\Tools\\bin\\tool.exe",
    configPath: "C:/Tools/config",
    logPath: "D:/Logs/tool.log",
    gameExe: "C:\\Games\\game.exe",
    judgeScriptEnabled: true,
    judgeScript: "return { status: 'success' };",
  }, new Date("2026-09-14T12:00:00.000Z"));
}

describe("script transfer path descriptors", () => {
  it("handles case, separator differences, and the root boundary", () => {
    expect(relativizeScriptPath("c:/TOOLS/bin/tool.exe", "C:\\Tools")).toEqual({ kind: "relative", value: "bin/tool.exe" });
    expect(relativizeScriptPath("C:\\Tools", "C:/tools/")).toEqual({ kind: "relative", value: "." });
    expect(relativizeScriptPath("C:\\Tools2\\a.exe", "C:\\Tools")).toEqual({ kind: "absolute", value: "C:\\Tools2\\a.exe" });
    expect(relativizeScriptPath("D:\\Tools\\a.exe", "C:\\Tools")).toEqual({ kind: "absolute", value: "D:\\Tools\\a.exe" });
  });

  it("keeps UNC roots distinct and derives imported paths", () => {
    const descriptor = relativizeScriptPath("\\\\Server\\Share\\bin\\tool.exe", "//server/share");
    expect(descriptor).toEqual({ kind: "relative", value: "bin/tool.exe" });
    expect(relativizeScriptPath("\\\\Server\\Share2\\bin\\tool.exe", "\\\\Server\\Share").kind).toBe("absolute");
    expect(deriveImportedPath({ kind: "relative", value: "config/settings.json" }, "D:/Tools")).toBe("D:\\Tools\\config\\settings.json");
    expect(deriveImportedPath({ kind: "relative", value: "." }, "D:\\Tools\\")).toBe("D:\\Tools\\");
  });
});

describe("script transfer schema", () => {
  it("round-trips the supported fields and reports absolute paths", () => {
    const input = validFile();
    const result = parseScriptImport(JSON.stringify({ ...input, extra: "ignored" }));
    expect(result.ok).toBe(true);
    if (!result.ok) return;
    expect(result.value.file.script).toEqual(input.script);
    expect(result.value.pendingRelativePaths).toEqual({ mainExe: "bin/tool.exe", configPath: "config" });
    expect(result.value.warnings).toEqual([{ field: "logPath", value: "D:/Logs/tool.log" }]);
    expect((result.value.file as unknown as Record<string, unknown>).extra).toBeUndefined();
  });

  it("rejects invalid JSON, kind, version, types, and unsafe relative paths", () => {
    expect(parseScriptImport("{")).toMatchObject({ ok: false, code: "invalid_json" });
    expect(parseScriptImport(JSON.stringify({ ...validFile(), kind: "other" }))).toMatchObject({ ok: false, code: "wrong_kind" });
    expect(parseScriptImport(JSON.stringify({ ...validFile(), schemaVersion: 2 }))).toMatchObject({ ok: false, code: "unsupported_version" });

    const badType = validFile();
    (badType.script as unknown as Record<string, unknown>).maxAttempts = Number.NaN;
    expect(parseScriptImport(JSON.stringify(badType))).toMatchObject({ ok: false, code: "invalid_field" });

    const unsafe = validFile();
    unsafe.paths.mainExe = { kind: "relative", value: "../escape.exe" };
    expect(parseScriptImport(JSON.stringify(unsafe))).toMatchObject({ ok: false, code: "invalid_path" });
    unsafe.paths.mainExe = { kind: "relative", value: "C:\\Tools\\tool.exe" };
    expect(parseScriptImport(JSON.stringify(unsafe))).toMatchObject({ ok: false, code: "invalid_path" });
  });

  it("rejects oversized input before parsing", () => {
    const oversized = "x".repeat(MAX_SCRIPT_IMPORT_BYTES + 1);
    expect(parseScriptImport(oversized)).toMatchObject({ ok: false, code: "file_too_large" });
  });
});

describe("script transfer names and export projection", () => {
  it("avoids case-insensitive name collisions and truncates at UTF-8 boundaries", () => {
    expect(makeUniqueImportedName("Daily", ["daily", "Daily-2"])).toBe("Daily-3");
    const longName = "脚本".repeat(40);
    const unique = makeUniqueImportedName(longName, []);
    expect(new TextEncoder().encode(unique).byteLength).toBeLessThanOrEqual(64);
    expect([...unique].join("")).toBe(unique);
    expect(truncateUtf8("😀😀😀😀😀", 9)).toBe("😀😀");
  });

  it("does not export machine, plugin, or instance identity fields", () => {
    const file = validFile();
    expect(file).toEqual(expect.objectContaining({ kind: SCRIPT_EXPORT_KIND, schemaVersion: 1 }));
    expect(file.script).not.toHaveProperty("rootPath");
    expect(file.script).not.toHaveProperty("gameExe");
    expect(file).not.toHaveProperty("id");
    expect(file).not.toHaveProperty("pluginType");
    expect(file).not.toHaveProperty("pluginInputs");
    expect(file.paths.mainExe).toEqual({ kind: "relative", value: "bin/tool.exe" });
    expect(file.paths.configPath).toEqual({ kind: "relative", value: "config" });
  });
});
