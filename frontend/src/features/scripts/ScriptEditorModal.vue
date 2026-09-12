<script setup lang="ts">
import { computed, nextTick, reactive, ref, watch } from "vue";
import { isAbortError } from "../../platform/api";
import { renderPluginSlot } from "@bridge/index";
import { disposePluginSlot } from "@bridge/index";
import { t } from "../../platform/i18n";
import { toast } from "../../platform/toast";
import NxpModal from "../../ui/primitives/NxpModal.vue";
import NxpNumberInput from "../../ui/primitives/NxpNumberInput.vue";
import NxpPathPicker from "../../ui/primitives/NxpPathPicker.vue";
import NxpSelect, { type NxpOption } from "../../ui/primitives/NxpSelect.vue";
import NxpSwitch from "../../ui/primitives/NxpSwitch.vue";
import { browseNativeDialog, createScript, probeScriptRoot, updateScript } from "./services/scriptsApi";
import { createRootProbe } from "./utils/scriptProbe";
import { emptyScriptDraft, scriptDraftFrom, scriptPayload, validateScriptDraft } from "./utils/scriptTypes";
import type { Script, ScriptDraft, ScriptPlugin } from "./utils/scriptTypes";

/** 脚本实例编辑弹窗：承担草稿、字段联动、专项 root 探测、判定脚本上传与保存事务。

 *  新增/编辑共用同一表单；专项脚本隐藏通用路径字段并锁定配置自动同步。 */

const props = defineProps<{
  script: Script | null;
  plugin: string;
  plugins: ScriptPlugin[];
}>();
const emit = defineEmits<{ close: []; saved: [] }>();

const draft = reactive<ScriptDraft>({ ...emptyScriptDraft });
const editorSlotRoot = ref<HTMLElement | null>(null);
const scriptLabel = computed(() => props.script || null);

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
      draft[field] = result.path;
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
  await rootProbe.probe(draft.pluginType, value);
}
async function save() {
  const invalid = validateScriptDraft(draft);
  if (invalid) {
    toast(invalid.args ? t(invalid.key, invalid.args) : t(invalid.key), "error");
    return;
  }
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
    :title="
      scriptLabel
        ? t('scripts.edit_script_instance')
        : draft.pluginType
          ? t('scripts.action.new_specialized', {
              plugin: pluginName(draft),
            })
          : t('scripts.new_general_script_instance')
    "
    :aria-label="scriptLabel ? t('scripts.edit_script_instance') : t('scripts.new_script_instance')"
    panel-class="secondary-surface"
    size="wide"
    @close="close"
    >
        <div class="form-grid">
          <div class="field">
            <label class="field-label" for="sm-name"
              >{{ t("scripts.script_name") }}
              <span class="req">*</span></label
            ><input id="sm-name" v-model="draft.name" type="text" />
          </div>
          <div class="field">
            <label class="field-label" for="sm-root"
              >{{ t("scripts.script_root_directory") }}
              <span class="req">*</span></label
            ><NxpPathPicker
              id="sm-root"
              v-model="draft.rootPath"
              kind="folder"
              :placeholder="t('scripts.script_root_directory')"
              :aria-label="t('scripts.script_root_directory')"
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
                v-model="draft.mainExe"
                kind="file"
                :disabled="!draft.rootPath"
                :placeholder="t('scripts.main_program_file')"
                :aria-label="t('scripts.main_program_path')"
                @browse="browseDraftPath('mainExe', $event)"
              />
            </div>
            <div class="field">
              <label class="field-label" for="sm-args">{{
                t("scripts.script_startup_arguments")
              }}</label
              ><input
                id="sm-args"
                v-model="draft.args"
                type="text"
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
                v-model="draft.configPath"
                kind="file-or-folder"
                :disabled="!draft.rootPath"
                :placeholder="t('scripts.editor.root_required')"
                :aria-label="t('scripts.configuration_file_folder')"
                @browse="browseDraftPath('configPath', $event)"
              />
            </div>
            <div class="field">
              <label class="field-label" for="sm-log"
                >{{ t("scripts.editor.log_path.help") }} <span class="req">*</span></label
              ><NxpPathPicker
                id="sm-log"
                v-model="draft.logPath"
                kind="file-or-folder"
                :disabled="!draft.rootPath"
                :placeholder="t('scripts.log_file_path')"
                :aria-label="t('scripts.log_path')"
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
          <div class="toggle-grid switch-grid">
            <div class="switch-row settings-option switch-card" :data-tooltip="selfManagedPc ? t('scripts.editor.pc_client_disabled') : undefined">
              <div>
                <strong>{{ t("scripts.launch_game") }}</strong
                ><span class="muted">{{
                  t(
                    "scripts.editor.game_launch.help",
                    {},
                    "Launch the game before running the script",
                  )
                }}</span>
              </div>
              <NxpSwitch
                id="sm-launch"
                :model-value="selfManagedPc ? false : draft.launchGame"
                :disabled="selfManagedPc"
                :aria-label="t('scripts.launch_game')"
                @update:model-value="draft.launchGame = $event"
              />
            </div>
            <div class="switch-row settings-option switch-card">
              <div>
                <strong>{{ t("scripts.force_close") }}</strong
                ><span class="muted">{{
                  t(
                    "scripts.game.cleanup_help",
                    {},
                    "Close the game after the run",
                  )
                }}</span>
              </div>
              <NxpSwitch
                id="sm-force"
                v-model="draft.forceCloseGame"
                :aria-label="t('scripts.force_close')"
              />
            </div>
            <div class="switch-row settings-option switch-card">
              <div>
                <strong>{{ t("scripts.editor.config_auto_update") }}</strong
                ><span class="muted">{{
                  t(
                    "scripts.editor.config_sync_help",
                    {},
                    "Keep configuration synchronized automatically",
                  )
                }}</span>
              </div>
              <NxpSwitch
                id="sm-autoupdate"
                v-model="draft.autoUpdateConfig"
                :disabled="Boolean(draft.pluginType)"
                :aria-label="t('scripts.editor.config_auto_update')"
              />
            </div>
          </div>
          <div class="nested-panel">
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
                  @browse="browseDraftPath('gameExe', $event)"
                /><input
                  v-else
                  id="sm-game-exe"
                  v-model="draft.gameExe"
                  type="text"
                  :placeholder="t('scripts.emulator_adb_address')"
                />
              </div>
              <div
                class="field"
                :data-help="selfManagedPc ? t('scripts.editor.pc_client_disabled') : draft.gameMode === 'emulator' ? t('scripts.android.arguments_mode_help') : undefined"
              >
                <label class="field-label" for="sm-game-args">{{
                  t("scripts.script_startup_arguments")
                }}</label
                ><input
                  id="sm-game-args"
                  v-model="draft.gameArgs"
                  type="text"
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
              ><textarea
                id="sm-succ-kw"
                v-model="draft.successKeywords"
                :placeholder="t('scripts.judge.keyword_syntax')"
              ></textarea>
            </div>
            <div
              class="field"
              :data-help="t('scripts.failure_keyword_help', {}, '')"
            >
              <label class="field-label" for="sm-fail-kw">{{
                t("scripts.failure_keywords")
              }}</label
              ><textarea
                id="sm-fail-kw"
                v-model="draft.failureKeywords"
                :placeholder="t('scripts.judge.failure_marker')"
              ></textarea>
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
                {{ t("scripts.code", {}, "Code") }}</label
              ><textarea
                id="sm-judge-code"
                v-model="draft.judgeScript"
                class="mono code-area"
                :placeholder="t('scripts.output_a_json_result')"
              ></textarea>
            </div>
          </div>
          <div class="judge-actions">
            <button
              v-show="draft.judgeScriptEnabled"
              id="sm-upload-btn"
              class="judge-upload-button"
              type="button"
              @click.stop="uploadJudgeScript"
            >
              {{ t("scripts.upload_script_file") }}</button
            ><button
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
            </button>
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
        <button class="ghost" type="button" @click.stop="close">
          {{ t("common.cancel") }}</button
        ><button class="primary" type="button" @click.stop="save">
          {{ t("common.save") }}
        </button>
    </template>
    </NxpModal>
</template>
