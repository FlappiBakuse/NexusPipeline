<script setup lang="ts">
import { computed, nextTick, reactive, ref, watch } from "vue";
import { isAbortError } from "../../platform/api";
import { renderPluginSlot } from "@bridge/index";
import { disposePluginSlot } from "@bridge/index";
import { t } from "../../platform/i18n";
import { clearFieldError, setRequiredFieldError, toast } from "../../platform/toast";
import NxpModal from "../../ui/primitives/NxpModal.vue";
import NxpSwitchGrid from "../../ui/composites/NxpSwitchGrid.vue";
import NxpSwitchSetting from "../../ui/composites/NxpSwitchSetting.vue";
import NxpButton from "../../ui/primitives/NxpButton.vue";
import NxpNumberInput from "../../ui/primitives/NxpNumberInput.vue";
import NxpPathPicker from "../../ui/primitives/NxpPathPicker.vue";
import NxpSelect, { type NxpOption } from "../../ui/primitives/NxpSelect.vue";
import NxpTextArea from "../../ui/primitives/NxpTextArea.vue";
import NxpTextInput from "../../ui/primitives/NxpTextInput.vue";
import { browseNativeDialog, createScript, probeScriptRoot, updateScript } from "./services/scriptsApi";
import { createRootProbe } from "./utils/scriptProbe";
import { emptyScriptDraft, scriptDraftFrom, scriptPayload, validateScriptDraft } from "./utils/scriptTypes";
import {
  buildScriptExport,
  deriveImportedPath,
  makeUniqueImportedName,
  MAX_SCRIPT_IMPORT_BYTES,
  parseScriptImport,
  type ScriptImportErrorCode,
  type ScriptPathField,
} from "./utils/scriptTransfer";
import type { Script, ScriptDraft, ScriptPlugin } from "./utils/scriptTypes";

/** 脚本实例编辑弹窗：承担草稿、字段联动、专项 root 探测、判定脚本上传与保存事务。

 *  新增/编辑共用同一表单；专项脚本隐藏通用路径字段并锁定配置自动同步。 */

const props = defineProps<{
  script: Script | null;
  plugin: string;
  plugins: ScriptPlugin[];
  existingNames?: string[];
}>();
const emit = defineEmits<{ close: []; saved: [] }>();

const draft = reactive<ScriptDraft>({ ...emptyScriptDraft });
const editorSlotRoot = ref<HTMLElement | null>(null);
const importFileInput = ref<HTMLInputElement | null>(null);
const pendingRelativePaths = reactive<Partial<Record<ScriptPathField, string>>>({});
const lastDerivedPaths = reactive<Partial<Record<ScriptPathField, string>>>({});
const manualPathOverrides = reactive<Partial<Record<ScriptPathField, boolean>>>({});
const scriptLabel = computed(() => props.script || null);
const modalTitle = computed(() =>
  scriptLabel.value
    ? t("scripts.edit_script_instance")
    : draft.pluginType
      ? t("scripts.action.new_specialized", { plugin: pluginName(draft) })
      : t("scripts.new_general_script_instance"),
);
const canExport = computed(() => Boolean(props.script && draft.id && !draft.pluginType));
const canImport = computed(() => !props.script && !draft.id && !draft.pluginType);

const gameModeOptions = computed<NxpOption[]>(() => [
  { value: "pc", label: t("scripts.pc_client") },
  { value: "emulator", label: t("scripts.android_emulator") },
]);
const judgeLanguageOptions = computed<NxpOption[]>(() => [
  { value: "javascript", label: t("scripts.javascript_built_in_engine") },
  { value: "python", label: t("scripts.python_system_interpreter") },
]);
const currentPlugin = computed(() =>
  props.plugins.find(
    (plugin) =>
      String(plugin.name || "").toLowerCase() ===
      String(draft.pluginType || "").toLowerCase(),
  ),
);
const emulatorAllowed = computed(() => !draft.pluginType || currentPlugin.value?.supportsEmulator === true);
const selfManagedPc = computed(
  () => draft.gameMode !== "emulator" && currentPlugin.value?.selfManagedPcLaunch === true,
);
const showSelfManagedPcHint = computed(
  () => selfManagedPc.value && String(currentPlugin.value?.name || "").trim().toLowerCase() !== "baah",
);

// 生产探测器：手工输入与原生目录选择统一走这一实例（含签名去重与过期响应抑制）。
const rootProbe = createRootProbe({
  request: (input) => probeScriptRoot(input),
  onError: (reason) => {
    toast(t("scripts.plugin.config_derive_failed", { reason: reason instanceof Error ? reason.message : String(reason) }), "error");
  },
});
function pluginName(script: { pluginType?: string }) {
  const plugin = props.plugins.find(
    (item) =>
      String(item.name || "").toLowerCase() ===
      String(script.pluginType || "").toLowerCase(),
  );
  return plugin?.displayName || plugin?.name || script.pluginType || t("scripts.general_script");
}

const pathFields: ScriptPathField[] = ["mainExe", "configPath", "logPath"];

function clearTransferState() {
  for (const field of pathFields) {
    delete pendingRelativePaths[field];
    delete lastDerivedPaths[field];
    delete manualPathOverrides[field];
  }
}

function applyPendingRelativePaths() {
  for (const field of pathFields) {
    const relative = pendingRelativePaths[field];
    if (relative === undefined || manualPathOverrides[field]) continue;
    const previous = lastDerivedPaths[field];
    const current = draft[field];
    const canReplace = !current || current === previous;
    if (!canReplace) {
      manualPathOverrides[field] = true;
      continue;
    }
    const derived = deriveImportedPath({ kind: "relative", value: relative }, draft.rootPath);
    draft[field] = derived;
    if (derived) lastDerivedPaths[field] = derived;
    else delete lastDerivedPaths[field];
  }
}

function setPathValue(field: ScriptPathField, value: unknown) {
  const next = String(value ?? "");
  draft[field] = next;
  const previous = lastDerivedPaths[field];
  if (pendingRelativePaths[field] !== undefined && next !== previous) manualPathOverrides[field] = true;
  clearFieldError(scriptFieldId(field));
}

function setRootPathValue(value: unknown) {
  draft.rootPath = String(value ?? "");
  applyPendingRelativePaths();
  clearFieldError("sm-root");
}

function transferErrorMessage(code: ScriptImportErrorCode) {
  const fallback: Record<ScriptImportErrorCode, string> = {
    file_too_large: "导入文件过大",
    invalid_json: "导入文件不是有效 JSON",
    invalid_shape: "导入文件结构无效",
    wrong_kind: "导入文件类型不受支持",
    unsupported_version: "导入文件版本不受支持",
    invalid_field: "导入文件包含无效字段",
    invalid_mode: "导入文件包含无效模式",
    invalid_path: "导入文件包含无效路径",
  };
  switch (code) {
    case "file_too_large": return t("scripts.transfer.error.file_too_large", { bytes: Math.floor(MAX_SCRIPT_IMPORT_BYTES / 1024) }, fallback[code]);
    case "invalid_json": return t("scripts.transfer.error.invalid_json", {}, fallback[code]);
    case "invalid_shape": return t("scripts.transfer.error.invalid_shape", {}, fallback[code]);
    case "wrong_kind": return t("scripts.transfer.error.wrong_kind", {}, fallback[code]);
    case "unsupported_version": return t("scripts.transfer.error.version", {}, fallback[code]);
    case "invalid_field": return t("scripts.transfer.error.invalid_field", {}, fallback[code]);
    case "invalid_mode": return t("scripts.transfer.error.invalid_mode", {}, fallback[code]);
    case "invalid_path": return t("scripts.transfer.error.invalid_path", {}, fallback[code]);
  }
}

function sanitizeExportName(name: string) {
  const sanitized = String(name || "script")
    .trim()
    .replace(/[<>:"/\\|?*]/g, "_")
    .replace(/[. ]+$/g, "_");
  return `${sanitized || "script"}.nxpscript.json`;
}

function exportScript() {
  if (!canExport.value) return;
  const file = buildScriptExport(draft);
  const blob = new Blob([JSON.stringify(file, null, 2)], { type: "application/json" });
  const url = URL.createObjectURL(blob);
  try {
    const anchor = document.createElement("a");
    anchor.href = url;
    anchor.download = sanitizeExportName(draft.name);
    anchor.click();
    toast(t("scripts.transfer.exported", {}, "脚本配置已导出"));
  } finally {
    URL.revokeObjectURL(url);
  }
}

function applyImportedFile(result: Extract<ReturnType<typeof parseScriptImport>, { ok: true }>["value"]) {
  const importedName = makeUniqueImportedName(result.file.script.name, props.existingNames || []);
  clearTransferState();
  Object.assign(draft, result.file.script);
  draft.id = "";
  draft.pluginType = "";
  draft.pluginInputs = {};
  draft.rootPath = "";
  draft.gameExe = "";
  for (const field of pathFields) {
    const descriptor = result.file.paths[field];
    if (descriptor.kind === "relative") {
      pendingRelativePaths[field] = descriptor.value;
      draft[field] = "";
    } else {
      draft[field] = descriptor.value;
    }
  }
  draft.name = importedName;
  applyPendingRelativePaths();
  result.warnings.forEach(warning => {
    toast(t("scripts.transfer.warning.absolute_path", { path: warning.value }, `路径“${warning.value}”需要检查。`), "info");
  });
  if (importedName !== result.file.script.name.trim()) {
    toast(t("scripts.transfer.renamed", { name: importedName }, `导入脚本名称已调整为“${importedName}”`), "info");
  }
  toast(t("scripts.transfer.imported", {}, "脚本配置已导入"));
  void nextTick(() => document.getElementById("sm-root")?.focus());
}

async function importScriptFile(event: Event) {
  const input = event.target as HTMLInputElement;
  const file = input.files?.[0];
  input.value = "";
  if (!file || !canImport.value) return;
  if (file.size > MAX_SCRIPT_IMPORT_BYTES) {
    toast(transferErrorMessage("file_too_large"), "error");
    return;
  }
  try {
    const result = parseScriptImport(await file.text());
    if (!result.ok) {
      toast(transferErrorMessage(result.code), "error");
      return;
    }
    applyImportedFile(result.value);
  } catch (reason) {
    toast(reason instanceof Error ? reason.message : String(reason), "error");
  }
}

function openImportPicker() {
  if (canImport.value) importFileInput.value?.click();
}

function toggleJudge() {
  draft.judgeScriptEnabled = !draft.judgeScriptEnabled;
}
function toggleField(field: "launchGame" | "forceCloseGame" | "autoUpdateConfig") {
  draft[field] = !draft[field];
}
async function browseDraftPath(
  field: "rootPath" | "mainExe" | "configPath" | "logPath" | "gameExe",
  kind: "file" | "folder",
) {
  try {
    const result = (await browseNativeDialog({
      kind,
      title: t("common.select_path"),
      initialPath: draft.rootPath || undefined,
      filter: "",
    })) as { path?: string } | null;
    if (result?.path) {
      if (field === "rootPath") setRootPathValue(result.path);
      else if (field === "mainExe" || field === "configPath" || field === "logPath") setPathValue(field, result.path);
      else draft[field] = result.path;
      clearFieldError(scriptFieldId(field));
      if (field === "rootPath") await rootProbe.probe(draft.pluginType, result.path);
    }
  } catch (reason) {
    if (!isAbortError(reason)) toast(reason instanceof Error ? reason.message : String(reason), "error");
  }
}
function uploadJudgeScript() {
  const input = document.createElement("input");
  input.type = "file";
  input.accept = ".js,.py";
  input.addEventListener("change", () => {
    const file = input.files?.[0];
    if (!file) return;
    if (file.size > 256 * 1024) {
      toast(t("scripts.validation.file_size"), "error");
      return;
    }
    const reader = new FileReader();
    reader.onload = () => {
      draft.judgeScript = String(reader.result || "");
      draft.judgeScriptLanguage = file.name.toLowerCase().endsWith(".py") ? "python" : "javascript";
      toast(t("scripts.status.loaded", { language: draft.judgeScriptLanguage === "python" ? "Python" : "JavaScript" }));
    };
    reader.onerror = () => toast(t("scripts.file.read_failed"), "error");
    reader.readAsText(file, "utf-8");
  }, { once: true });
  input.click();
}
async function probeRootPath(value: string) {
  setRootPathValue(value);
  await rootProbe.probe(draft.pluginType, value);
}

function scriptFieldId(field: string) {
  return ({
    name: "sm-name",
    rootPath: "sm-root",
    mainExe: "sm-exe",
    configPath: "sm-config",
    logPath: "sm-log",
    gameExe: "sm-game-exe",
    maxAttempts: "sm-attempts",
    logStallTimeoutMinutes: "sm-stall",
    totalTimeoutMinutes: "sm-total",
    judgeScript: "sm-judge-code",
  } as Record<string, string>)[field] || field;
}

function hasRequiredValue(value: unknown) {
  if (typeof value === "number") return Number.isFinite(value) && value !== 0;
  return String(value ?? "").trim().length > 0;
}

function syncScriptFieldErrors(invalid: { key: string } | null) {
  const requiredFields = [
    { id: "sm-name", value: draft.name },
    { id: "sm-root", value: draft.rootPath },
    { id: "sm-game-exe", value: draft.gameExe },
    { id: "sm-attempts", value: draft.maxAttempts },
    { id: "sm-stall", value: draft.logStallTimeoutMinutes },
    { id: "sm-total", value: draft.totalTimeoutMinutes },
    ...(!draft.pluginType
      ? [
          { id: "sm-exe", value: draft.mainExe },
          { id: "sm-config", value: draft.configPath },
          { id: "sm-log", value: draft.logPath },
        ]
      : []),
  ];
  const allFieldIds = [
    "sm-name", "sm-root", "sm-exe", "sm-config", "sm-log", "sm-game-exe",
    "sm-attempts", "sm-stall", "sm-total", "sm-judge-code",
  ];
  allFieldIds.forEach(clearFieldError);
  let firstInvalidId: string | null = null;
  for (const field of requiredFields) {
    if (hasRequiredValue(field.value)) continue;
    setRequiredFieldError(field.id, false);
    if (!firstInvalidId) firstInvalidId = field.id;
  }
  if (invalid?.key === "scripts.validation.name_length") firstInvalidId = "sm-name";
  if (invalid?.key === "scripts.editor.judge_code_help") firstInvalidId = "sm-judge-code";
  if (firstInvalidId) setRequiredFieldError(firstInvalidId);
  return firstInvalidId;
}

async function save() {
  const invalid = validateScriptDraft(draft);
  if (invalid) {
    syncScriptFieldErrors(invalid);
    toast(invalid.args ? t(invalid.key, invalid.args) : t(invalid.key), "error");
    return;
  }
  syncScriptFieldErrors(null);
  try {
    const payload = scriptPayload(draft);
    if (draft.id) await updateScript(draft.id, payload);
    else await createScript(payload);
    toast(t("scripts.script_instance_saved"));
    emit("saved");
    close();
  } catch (reason) {
    if (!isAbortError(reason)) toast(reason instanceof Error ? reason.message : String(reason), "error");
  }
}
async function paintEditorSlot() {
  await nextTick();
  if (editorSlotRoot.value) {
    await renderPluginSlot(editorSlotRoot.value, "scripts.editor.sections", {
      mode: draft.id ? "edit" : "create",
      primaryId: draft.id,
    });
  }
}
function close() {
  rootProbe.invalidate();
  if (editorSlotRoot.value) void disposePluginSlot(editorSlotRoot.value);
  emit("close");
}

watch(
  () => props.script,
  () => {
    rootProbe.invalidate();
    clearTransferState();
    Object.assign(draft, scriptDraftFrom(props.script, props.plugin));
    void paintEditorSlot();
  },
  { immediate: true },
);
</script>

<template>
    <NxpModal
    :locked="true"
    :open="true"
    :title="modalTitle"
    :aria-label="scriptLabel ? t('scripts.edit_script_instance') : t('scripts.new_script_instance')"
    panel-class="secondary-surface"
    size="wide"
    @close="close"
    >
        <template #header>
          <div class="script-editor-header">
            <h2 class="modal-title">{{ modalTitle }}</h2>
            <div class="script-transfer-actions">
              <input
                ref="importFileInput"
                class="script-transfer-input"
                type="file"
                accept=".nxpscript.json,application/json"
                aria-hidden="true"
                tabindex="-1"
                @change="importScriptFile"
              />
              <NxpButton
                v-if="canImport"
                class="ghost"
                size="sm"
                type="button"
                @click.stop="openImportPicker"
              >{{ t("scripts.transfer.import", {}, "导入") }}</NxpButton>
              <NxpButton
                v-if="canExport"
                class="ghost"
                size="sm"
                type="button"
                @click.stop="exportScript"
              >{{ t("scripts.transfer.export", {}, "导出") }}</NxpButton>
            </div>
          </div>
        </template>
        <div class="form-grid">
          <div class="field">
            <label class="field-label" for="sm-name"
              >{{ t("scripts.script_name") }}
              <span class="req">*</span></label
            ><NxpTextInput id="sm-name" v-model="draft.name" :aria-label="t('scripts.script_name')" @update:model-value="clearFieldError('sm-name')" />
          </div>
          <div class="field">
            <label class="field-label" for="sm-root"
              >{{ t("scripts.script_root_directory") }}
              <span class="req">*</span></label
            ><NxpPathPicker
              id="sm-root"
              :model-value="draft.rootPath"
              kind="folder"
              :placeholder="t('scripts.script_root_directory')"
              :aria-label="t('scripts.script_root_directory')"
              @update:model-value="setRootPathValue"
              @change="probeRootPath"
              @browse="browseDraftPath('rootPath', $event)"
            />
          </div>
        </div>
        <template v-if="!draft.pluginType">
          <div class="form-grid">
            <div class="field">
              <label class="field-label" for="sm-exe"
                >{{ t("scripts.main_program_path") }}
                <span class="req">*</span></label
              ><NxpPathPicker
                id="sm-exe"
                :model-value="draft.mainExe"
                kind="file"
                :disabled="!draft.rootPath"
                :placeholder="t('scripts.main_program_file')"
                :aria-label="t('scripts.main_program_path')"
                @update:model-value="setPathValue('mainExe', $event)"
                @browse="browseDraftPath('mainExe', $event)"
              />
            </div>
            <div class="field">
              <label class="field-label" for="sm-args">{{
                t("scripts.script_startup_arguments")
              }}</label
              ><NxpTextInput
                id="sm-args"
                v-model="draft.args"
                type="text"
                :aria-label="t('scripts.script_startup_arguments')"
                :disabled="!draft.rootPath"
                :placeholder="t('scripts.optional_startup_arguments')"
              />
            </div>
          </div>
          <div class="form-grid">
            <div class="field">
              <label class="field-label" for="sm-config"
                >{{ t("scripts.configuration_file_folder") }}
                <span class="req">*</span></label
              ><NxpPathPicker
                id="sm-config"
                :model-value="draft.configPath"
                kind="file-or-folder"
                :disabled="!draft.rootPath"
                :placeholder="t('scripts.editor.root_required')"
                :aria-label="t('scripts.configuration_file_folder')"
                @update:model-value="setPathValue('configPath', $event)"
                @browse="browseDraftPath('configPath', $event)"
              />
            </div>
            <div class="field">
              <label class="field-label" for="sm-log"
                >{{ t("scripts.editor.log_path.help") }} <span class="req">*</span></label
              ><NxpPathPicker
                id="sm-log"
                :model-value="draft.logPath"
                kind="file-or-folder"
                :disabled="!draft.rootPath"
                :placeholder="t('scripts.log_file_path')"
                :aria-label="t('scripts.log_path')"
                @update:model-value="setPathValue('logPath', $event)"
                @browse="browseDraftPath('logPath', $event)"
              />
            </div>
          </div>
        </template>
        <div class="subsection">
          <div class="section-heading">
            <h3>{{ t("scripts.game_integration") }}</h3>
            <span v-if="draft.pluginType" class="muted">{{
              t("scripts.editor.path_adb_cleanup_help")
            }}</span>
          </div>
          <NxpSwitchGrid>
            <NxpSwitchSetting
              id="sm-launch"
              :model-value="selfManagedPc ? false : draft.launchGame"
              :label="t('scripts.launch_game')"
              :description="t('scripts.editor.game_launch.help', {}, 'Launch the game before running the script')"
              :help="selfManagedPc ? t('scripts.editor.pc_client_disabled') : undefined"
              :disabled="selfManagedPc"
              :aria-label="t('scripts.launch_game')"
              @update:model-value="draft.launchGame = $event"
            />
            <NxpSwitchSetting
              id="sm-force"
              v-model="draft.forceCloseGame"
              :label="t('scripts.force_close')"
              :description="t('scripts.game.cleanup_help', {}, 'Close the game after the run')"
              :aria-label="t('scripts.force_close')"
            />
            <NxpSwitchSetting
              id="sm-autoupdate"
              v-model="draft.autoUpdateConfig"
              :label="t('scripts.editor.config_auto_update')"
              :description="t('scripts.editor.config_sync_help', {}, 'Keep configuration synchronized automatically')"
              :disabled="Boolean(draft.pluginType)"
              :aria-label="t('scripts.editor.config_auto_update')"
            />
          </NxpSwitchGrid>
          <div class="nested-panel">
            <p v-if="showSelfManagedPcHint" id="sm-self-managed-hint" class="muted">{{ t("scripts.editor.launch.controlled_help") }}</p>
            <div class="form-grid">
              <div
                class="field"
                :data-help="draft.gameMode === 'emulator' ? t('scripts.editor.adb_cleanup_help') : t('scripts.editor.game_path.cleanup_help')"
              >
                <label class="field-label" for="sm-game-exe">{{
                  draft.gameMode === "emulator"
                    ? t("scripts.emulator_adb_address")
                    : t("scripts.game_path")
                }} <span class="req">*</span></label
                ><NxpPathPicker
                  v-if="draft.gameMode !== 'emulator'"
                  id="sm-game-exe"
                  v-model="draft.gameExe"
                  kind="file"
                  :placeholder="t('scripts.editor.game_path.placeholder')"
                  :aria-label="t('scripts.game_path')"
                  @update:model-value="clearFieldError('sm-game-exe')"
                  @browse="browseDraftPath('gameExe', $event)"
                /><NxpTextInput
                  v-else
                  id="sm-game-exe"
                  v-model="draft.gameExe"
                  type="text"
                  :aria-label="t('scripts.emulator_adb_address')"
                  :placeholder="t('scripts.emulator_adb_address')"
                  @update:model-value="clearFieldError('sm-game-exe')"
                />
              </div>
              <div
                class="field"
                :data-help="selfManagedPc ? t('scripts.editor.pc_client_disabled') : draft.gameMode === 'emulator' ? t('scripts.android.arguments_mode_help') : undefined"
              >
                <label class="field-label" for="sm-game-args">{{
                  t("scripts.script_startup_arguments")
                }}</label
                ><NxpTextInput
                  id="sm-game-args"
                  v-model="draft.gameArgs"
                  type="text"
                  :aria-label="t('scripts.script_startup_arguments')"
                  :disabled="selfManagedPc"
                />
              </div>
            </div>
            <div class="form-grid">
              <div class="field" :data-help="t('scripts.select_game_start_mode')">
                <label class="field-label" for="sm-mode-trigger">{{
                  t("scripts.startup_mode")
                }}</label
                ><NxpSelect
                  id="sm-mode"
                  v-model="draft.gameMode"
                  :options="gameModeOptions"
                  :disabled="!emulatorAllowed"
                  :aria-label="t('scripts.startup_mode')"
                />
              </div>
              <div
                class="field"
                :data-help="selfManagedPc ? t('scripts.editor.pc_client_disabled') : t('scripts.editor.game_launch.wait_help')"
              >
                <label class="field-label" for="sm-game-wait">{{
                  t("scripts.wait_after_game_start")
                }}</label
                ><NxpNumberInput
                  id="sm-game-wait"
                  v-model.number="draft.gameWaitSeconds"
                  :min="0"
                  :disabled="selfManagedPc"
                  :aria-label="t('scripts.wait_after_game_start')"
                />
              </div>
            </div>
          </div>
        </div>
        <div class="subsection">
          <div class="section-heading">
            <h3>{{ t("scripts.run_settings") }}</h3>
          </div>
          <div class="form-grid three">
            <div class="field" :data-help="t('scripts.editor.retry.attempts_help')">
              <label class="field-label" for="sm-attempts"
                >{{ t("scripts.editor.retry.attempts_label") }}
                <span class="req">*</span></label
              ><NxpNumberInput
                id="sm-attempts"
                v-model.number="draft.maxAttempts"
                :min="1"
                :max="10"
                :aria-label="t('scripts.editor.retry.attempts_label')"
                @update:model-value="clearFieldError('sm-attempts')"
              />
            </div>
            <div class="field" :data-help="t('scripts.editor.retry.stall_timeout_help')">
              <label class="field-label" for="sm-stall"
                >{{ t("scripts.log_stall_timeout_minutes") }}
                <span class="req">*</span></label
              ><NxpNumberInput
                id="sm-stall"
                v-model.number="draft.logStallTimeoutMinutes"
                :min="-1"
                :max="60"
                :aria-label="t('scripts.log_stall_timeout_minutes')"
                @update:model-value="clearFieldError('sm-stall')"
              />
            </div>
            <div class="field" :data-help="t('scripts.validation.total_timeout_help')">
              <label class="field-label" for="sm-total"
                >{{ t("scripts.total_timeout_minutes") }}
                <span class="req">*</span></label
              ><NxpNumberInput
                id="sm-total"
                v-model.number="draft.totalTimeoutMinutes"
                :min="-1"
                :max="720"
                :aria-label="t('scripts.total_timeout_minutes')"
                @update:model-value="clearFieldError('sm-total')"
              />
            </div>
          </div>
        </div>
        <div v-if="!draft.pluginType" class="subsection judge-box">
          <div class="section-heading">
            <h3>{{ t("scripts.custom_completion_markers") }}</h3>
          </div>
          <div v-show="!draft.judgeScriptEnabled" id="sm-kw-box">
            <div
              class="field"
              :data-help="t('scripts.success_keyword_help', {}, '')"
            >
              <label class="field-label" for="sm-succ-kw">{{
                t("scripts.success_keywords")
              }}</label
              ><NxpTextArea
                id="sm-succ-kw"
                v-model="draft.successKeywords"
                :placeholder="t('scripts.judge.keyword_syntax')"
              />
            </div>
            <div
              class="field"
              :data-help="t('scripts.failure_keyword_help', {}, '')"
            >
              <label class="field-label" for="sm-fail-kw">{{
                t("scripts.failure_keywords")
              }}</label
              ><NxpTextArea
                id="sm-fail-kw"
                v-model="draft.failureKeywords"
                :placeholder="t('scripts.judge.failure_marker')"
              />
            </div>
          </div>
          <div v-show="draft.judgeScriptEnabled" id="sm-script-box">
            <div class="field" :data-help="t('scripts.judge.language_help')">
              <label class="field-label" for="sm-judge-lang-trigger">{{
                t("scripts.judge_script_language")
              }}</label
              ><NxpSelect
                id="sm-judge-lang"
                v-model="draft.judgeScriptLanguage"
                :options="judgeLanguageOptions"
                :aria-label="t('scripts.judge_script_language')"
              />
            </div>
            <div
              class="field"
              :data-help="t('scripts.judge_script_input_output_help', {}, '')"
            >
              <label class="field-label" for="sm-judge-code"
                >{{ t("scripts.judge_script") }}
                {{ t("scripts.code", {}, "Code") }}<span v-if="draft.judgeScriptEnabled" class="req"> *</span></label
              ><NxpTextArea
                id="sm-judge-code"
                v-model="draft.judgeScript"
                class="mono code-area"
                :placeholder="t('scripts.output_a_json_result')"
                @update:model-value="clearFieldError('sm-judge-code')"
              />
            </div>
          </div>
          <div class="judge-actions">
            <NxpButton
              v-show="draft.judgeScriptEnabled"
              id="sm-upload-btn"
              class="judge-upload-button"
              type="button"
              @click.stop="uploadJudgeScript"
            >
              {{ t("scripts.upload_script_file") }}
            </NxpButton>
            <NxpButton
              id="sm-mode-btn"
              class="judge-mode-card mode-toggle"
              type="button"
              :aria-pressed="draft.judgeScriptEnabled"
              :data-help="t('scripts.judge_script_mode_help', {}, '')"
              :data-hint="t('scripts.script_takes_priority', {}, '')"
              @click.stop="toggleJudge"
            >
              {{ t("scripts.use_judge_script")
              }}<span class="judge-toggle-track" aria-hidden="true"
                ><span class="judge-toggle-thumb"></span
              ></span>
            </NxpButton>
          </div>
        </div>
        <div
          ref="editorSlotRoot"
          class="plugin-slot script-editor-plugin-slot"
          data-plugin-slot="scripts.editor.sections"
          data-plugin-anchor="scripts.editor.sections"
          hidden
        ></div>
    <template #footer>
        <NxpButton class="ghost" type="button" @click.stop="close">
          {{ t("common.cancel") }}
        </NxpButton>
        <NxpButton class="primary" type="button" @click.stop="save">
          {{ t("common.save") }}
        </NxpButton>
    </template>
    </NxpModal>
</template>

<style scoped>
.script-editor-header {
  display: flex;
  min-width: 0;
  flex: 1 1 auto;
  align-items: center;
  justify-content: space-between;
  gap: var(--nx-space-3, 12px);
  text-align: left;
}

.script-editor-header .modal-title {
  min-width: 0;
  max-width: 100%;
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
  text-align: left;
}

.script-transfer-actions {
  display: inline-flex;
  flex: 0 0 auto;
  align-items: center;
  gap: var(--nx-space-2, 8px);
}

.script-transfer-actions button {
  flex: 0 0 80px;
  width: 80px;
  min-width: 80px;
  height: var(--control-height, var(--nx-control-height, 40px));
  min-height: var(--control-height, var(--nx-control-height, 40px));
  margin: 0;
}

.script-transfer-input {
  display: none;
}

</style>
