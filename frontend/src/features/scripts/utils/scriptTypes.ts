export interface Script {
  id: string;
  name: string;
  pluginType?: string;
  pluginInputs?: Record<string, unknown>;
  rootPath?: string;
  mainExe?: string;
  args?: string;
  configPath?: string;
  logPath?: string;
  launchGame?: boolean;
  gameMode?: string;
  gameExe?: string;
  gameArgs?: string;
  gameWaitSeconds?: number;
  forceCloseGame?: boolean;
  maxAttempts?: number;
  logStallTimeoutMinutes?: number;
  totalTimeoutMinutes?: number;
  successKeywords?: string;
  failureKeywords?: string;
  judgeScriptEnabled?: boolean;
  judgeScriptLanguage?: string;
  judgeScript?: string;
  autoUpdateConfig?: boolean;
}

export interface ScriptPlugin {
  name?: string;
  displayName?: string;
  kind?: string;
  configuredEnabled?: boolean;
  runtimeEnabled?: boolean;
  state?: string;
  supportsEmulator?: boolean;
  selfManagedPcLaunch?: boolean;
}

export interface ScriptDraft extends Script {
  id: string;
  name: string;
  rootPath: string;
  mainExe: string;
  configPath: string;
  logPath: string;
  gameExe: string;
  args: string;
  gameArgs: string;
  gameMode: string;
  gameWaitSeconds: number;
  maxAttempts: number;
  logStallTimeoutMinutes: number;
  totalTimeoutMinutes: number;
  successKeywords: string;
  failureKeywords: string;
  judgeScript: string;
  judgeScriptLanguage: string;
  pluginInputs: Record<string, unknown>;
  launchGame: boolean;
  forceCloseGame: boolean;
  judgeScriptEnabled: boolean;
  autoUpdateConfig: boolean;
}

export const emptyScriptDraft: ScriptDraft = {
  id: "",
  name: "",
  pluginType: "",
  pluginInputs: {},
  rootPath: "",
  mainExe: "",
  args: "",
  configPath: "",
  logPath: "",
  launchGame: false,
  gameMode: "pc",
  gameExe: "",
  gameArgs: "",
  gameWaitSeconds: 30,
  forceCloseGame: false,
  maxAttempts: 3,
  logStallTimeoutMinutes: 5,
  totalTimeoutMinutes: 120,
  successKeywords: "",
  failureKeywords: "",
  judgeScriptEnabled: false,
  judgeScriptLanguage: "javascript",
  judgeScript: "",
  autoUpdateConfig: true,
};

export function stripQuotes(value: string) {
  const trimmed = String(value || "").trim();
  return trimmed.length >= 2 &&
    ((trimmed.startsWith('"') && trimmed.endsWith('"')) ||
      (trimmed.startsWith("'") && trimmed.endsWith("'")))
    ? trimmed.slice(1, -1).trim()
    : trimmed;
}

export function scriptDraftFrom(script: Script | null, plugin = ""): ScriptDraft {
  const value: Partial<Script> = script || {};
  return {
    ...emptyScriptDraft,
    id: value.id || "",
    pluginType: value.pluginType || plugin,
    name: value.name || "",
    pluginInputs: value.pluginInputs && typeof value.pluginInputs === "object" ? { ...value.pluginInputs } : {},
    rootPath: value.rootPath || "",
    mainExe: value.mainExe || "",
    args: value.args || "",
    configPath: value.configPath || "",
    logPath: value.logPath || "",
    launchGame: value.launchGame === true,
    gameMode: value.gameMode === "emulator" ? "emulator" : "pc",
    gameExe: value.gameExe || "",
    gameArgs: value.gameArgs || "",
    gameWaitSeconds: value.gameWaitSeconds ?? 30,
    forceCloseGame: value.forceCloseGame ?? Boolean(value.pluginType),
    maxAttempts: value.maxAttempts ?? 3,
    logStallTimeoutMinutes: value.logStallTimeoutMinutes ?? 5,
    totalTimeoutMinutes: value.totalTimeoutMinutes ?? 120,
    successKeywords: value.successKeywords || "",
    failureKeywords: value.failureKeywords || "",
    judgeScriptEnabled: value.judgeScriptEnabled === true,
    judgeScriptLanguage: value.judgeScriptLanguage || "javascript",
    judgeScript: value.judgeScript || "",
    autoUpdateConfig: value.autoUpdateConfig !== false,
  };
}

export function scriptPayload(draft: ScriptDraft) {
  return {
    id: draft.id,
    pluginType: draft.pluginType || "",
    name: draft.name.trim(),
    rootPath: stripQuotes(draft.rootPath),
    pluginInputs: draft.pluginType ? { ...draft.pluginInputs } : {},
    mainExe: draft.mainExe ? stripQuotes(draft.mainExe) : "",
    args: draft.args.trim(),
    configPath: draft.configPath ? stripQuotes(draft.configPath) : "",
    logPath: draft.logPath ? stripQuotes(draft.logPath) : "",
    launchGame: draft.launchGame,
    gameMode: draft.gameMode,
    gameExe: stripQuotes(draft.gameExe),
    gameArgs: draft.gameArgs.trim(),
    gameWaitSeconds: Number(draft.gameWaitSeconds) || 0,
    forceCloseGame: draft.forceCloseGame,
    maxAttempts: Number(draft.maxAttempts) || 3,
    logStallTimeoutMinutes: Number(draft.logStallTimeoutMinutes) || 5,
    totalTimeoutMinutes: Number(draft.totalTimeoutMinutes) || 120,
    successKeywords: draft.successKeywords,
    failureKeywords: draft.failureKeywords,
    judgeScriptEnabled: draft.judgeScriptEnabled,
    judgeScriptLanguage: draft.judgeScriptLanguage,
    judgeScript: draft.judgeScript,
    autoUpdateConfig: draft.autoUpdateConfig,
  };
}

/** 保存前校验：返回错误提示的 i18n key，无错误时返回空字符串。 */
export function validateScriptDraft(draft: ScriptDraft): { key: string; args?: Record<string, unknown> } | null {
  const required = [draft.name, draft.rootPath, draft.gameExe, draft.maxAttempts, draft.logStallTimeoutMinutes, draft.totalTimeoutMinutes];
  if (!draft.pluginType) required.push(draft.mainExe, draft.configPath, draft.logPath);
  if (required.some((value) => !String(value || "").trim())) {
    return { key: "scripts.validation.required_fields" };
  }
  if (new TextEncoder().encode(draft.name.trim()).length > 64) {
    return { key: "scripts.validation.name_length", args: { bytes: 64 } };
  }
  if (draft.judgeScriptEnabled && !draft.judgeScript.trim()) {
    return { key: "scripts.editor.judge_code_help" };
  }
  return null;
}
