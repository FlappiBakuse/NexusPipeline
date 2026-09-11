import { computed, ref } from "vue";
import { buildConfigEditRequest } from "../utils/configEditRequest";
import { findRestorableEditSession } from "../utils/editSession";
import type { EditSessionScriptLike, EditSessionUserLike } from "../utils/editSession";

/** 配置编辑事务状态机（生产实现）。
 *
 *  两类事务严格分开：
 *  - `startSession`：创建新编辑事务，唯一允许 POST `action:"start"` 的入口；
 *  - `restoreExisting`：恢复宿主已存在的编辑事务，只还原前端锁定编辑 UI，
 *    不发起 start、不创建 requester window token、不改 document title、不重新解析候选。
 */

export interface ConfigEditDetails {
  userId: string;
  scriptId: string;
  userName: string;
  scriptName: string;
}

export type ConfigEditItem = ConfigEditDetails & { mode: string };

export interface ConfigEditSelection extends ConfigEditDetails {
  freshAvailable: boolean;
}

export interface ConfigEditCandidate extends ConfigEditDetails {
  mode: string;
  inputName: string;
  candidates: string[];
}

export interface ConfigEditResultToast {
  message?: string;
  kind?: string;
}

export interface ConfigEditFlowAdapters {
  /** 查询绑定是否已有配置快照。 */
  getStatus(userId: string, scriptId: string): Promise<{ hasSnapshot?: boolean } | null>;
  /** 创建编辑事务的唯一出口。 */
  start(userId: string, scriptId: string, request: Record<string, unknown>): Promise<unknown>;
  /** 结束编辑事务（done / cancel）。 */
  finish(
    userId: string,
    scriptId: string,
    action: "done" | "cancel",
  ): Promise<{ validation?: { toasts?: ConfigEditResultToast[] } } | null>;
  /** 读取宿主当前进行中的编辑会话。 */
  listSessions(): Promise<unknown>;
  createRequesterWindowToken(): string;
  waitForRequesterTitlePaint(): Promise<void>;
  getDocumentTitle(): string;
  setDocumentTitle(title: string): void;
  notify(message: string, kind?: "info" | "error"): void;
  translate(key: string, args?: Record<string, unknown>): string;
  onTransactionChanged(userId: string): void;
}

function errorText(reason: unknown) {
  return reason instanceof Error ? reason.message : String(reason);
}

function isAbortError(reason: unknown) {
  const value = reason as { name?: string; code?: number; message?: string } | null;
  if (!value) return false;
  if (value.name === "AbortError" || value.name === "CanceledError" || value.code === 20) return true;
  return /\b(?:abort|aborted|cancell?ed)\b/i.test(String(value.message ?? reason));
}

function configEditErrorData(reason: unknown) {
  const value = reason as { code?: string; data?: { inputName?: string; candidates?: unknown[] } } | null;
  return value?.code === "config_input_mismatch" && Array.isArray(value.data?.candidates)
    ? { inputName: String(value.data?.inputName || ""), candidates: value.data.candidates.map(item => String(item || "")).filter(Boolean) }
    : null;
}

export function useConfigEditFlow(adapters: ConfigEditFlowAdapters) {
  const configEdit = ref<ConfigEditItem | null>(null);
  const configChooser = ref<ConfigEditSelection | null>(null);
  const configCandidates = ref<ConfigEditCandidate | null>(null);
  const isOpen = computed(() => Boolean(configEdit.value || configChooser.value || configCandidates.value));

  /** 创建新编辑事务：唯一发送 `action:"start"` 的路径。 */
  async function startSession(item: ConfigEditItem, inputOverride?: { name: string; value: string }) {
    // 临时标记发起请求的浏览器窗口，宿主据此把该窗口后置。
    const requesterWindowToken = adapters.createRequesterWindowToken();
    const savedTitle = adapters.getDocumentTitle();
    adapters.setDocumentTitle(
      `${adapters.translate("users.nexuspipeline_core")} · ${requesterWindowToken}`,
    );
    try {
      const request = buildConfigEditRequest(item.mode, inputOverride, requesterWindowToken);
      await adapters.waitForRequesterTitlePaint();
      await adapters.start(item.userId, item.scriptId, request);
      configChooser.value = null;
      configCandidates.value = null;
      configEdit.value = item;
    } catch (reason) {
      const candidateData = configEditErrorData(reason);
      if (candidateData) {
        if (!candidateData.inputName) {
          adapters.notify(adapters.translate("users.config.input_missing"), "error");
          return;
        }
        configChooser.value = null;
        configCandidates.value = {
          userId: item.userId,
          scriptId: item.scriptId,
          userName: item.userName,
          scriptName: item.scriptName,
          mode: item.mode,
          inputName: candidateData.inputName,
          candidates: candidateData.candidates,
        };
        return;
      }
      if (!isAbortError(reason)) adapters.notify(errorText(reason), "error");
    } finally {
      adapters.setDocumentTitle(savedTitle);
    }
  }

  /** 从绑定卡片进入配置编辑：已有快照直接创建事务，否则展示首次编辑选择。 */
  async function open(details: ConfigEditDetails, freshAvailable: boolean) {
    try {
      const status = await adapters.getStatus(details.userId, details.scriptId);
      if (status?.hasSnapshot) {
        await startSession({ ...details, mode: "normal" });
        return;
      }
      configChooser.value = { ...details, freshAvailable };
    } catch (reason) {
      if (!isAbortError(reason)) adapters.notify(errorText(reason), "error");
    }
  }

  /**
   * 恢复宿主已存在的编辑事务：只还原锁定编辑 UI。
   * 不 POST start、不创建 token、不改标题、不重新解析候选；失败静默。
   */
  function restore(item: ConfigEditItem) {
    configChooser.value = null;
    configCandidates.value = null;
    configEdit.value = { ...item };
  }

  /** 读取宿主进行中的编辑会话并恢复 UI；失败静默，不阻断页面。
   *  `canRestore` 在取得会话后再次判断页面是否已被其他弹窗占用，避免强行覆盖。 */
  async function restoreExisting(
    users: readonly EditSessionUserLike[] | null | undefined,
    scripts: readonly EditSessionScriptLike[] | null | undefined,
    canRestore?: () => boolean,
  ) {
    try {
      const sessions = await adapters.listSessions();
      if (canRestore && !canRestore()) return;
      const matched = findRestorableEditSession(sessions, users, scripts);
      if (matched) restore(matched);
    } catch {
      // 恢复失败时保留页面，用户可从绑定卡片重新进入配置编辑。
    }
  }

  async function chooseMode(mode: "fresh" | "reuse") {
    const chooser = configChooser.value;
    if (!chooser || (mode === "fresh" && !chooser.freshAvailable)) return;
    await startSession({
      userId: chooser.userId,
      scriptId: chooser.scriptId,
      userName: chooser.userName,
      scriptName: chooser.scriptName,
      mode,
    });
  }

  async function chooseCandidate(candidate: string) {
    const chooser = configCandidates.value;
    if (!chooser) return;
    configCandidates.value = null;
    await startSession(
      { userId: chooser.userId, scriptId: chooser.scriptId, userName: chooser.userName, scriptName: chooser.scriptName, mode: chooser.mode },
      { name: chooser.inputName, value: candidate },
    );
  }

  async function finish(action: "done" | "cancel") {
    const edit = configEdit.value;
    if (!edit) return;
    try {
      const result = await adapters.finish(edit.userId, edit.scriptId, action);
      configEdit.value = null;
      adapters.notify(
        action === "done"
          ? (edit.mode === "fresh"
            ? adapters.translate("users.config.snapshot_saved")
            : adapters.translate("users.user_configuration_saved", { user: edit.userName }))
          : (edit.mode === "reuse"
            ? adapters.translate("common.cancelled")
            : adapters.translate("users.config.cancelled_restored")),
      );
      for (const item of result?.validation?.toasts || []) {
        if (item.message) adapters.notify(item.message, item.kind === "error" ? "error" : "info");
      }
      adapters.onTransactionChanged(edit.userId);
    } catch (reason) {
      if (!isAbortError(reason)) adapters.notify(errorText(reason), "error");
    }
  }

  function close() {
    configEdit.value = null;
    configChooser.value = null;
    configCandidates.value = null;
  }

  return {
    configEdit,
    configChooser,
    configCandidates,
    isOpen,
    open,
    startSession,
    restore,
    restoreExisting,
    chooseMode,
    chooseCandidate,
    finish,
    close,
  };
}
