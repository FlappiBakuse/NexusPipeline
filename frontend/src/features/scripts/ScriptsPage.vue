<script setup lang="ts">
import {
  computed,
  nextTick,
  onBeforeUnmount,
  onMounted,
  reactive,
  ref,
} from "vue";
import { api, isAbortError } from "@legacy/core/api.js";
import { renderPluginSlot } from "@legacy/core/plugin-slots.js";
import { disposePluginSlot } from "@legacy/core/plugin-runtime.js";
import {
  scriptPluginStatus,
  scriptPluginUnavailableMessage,
} from "@legacy/core/format.js";
import { t } from "@legacy/core/i18n.js";
import { setTopbarTitle, toast } from "@legacy/core/ui.js";
import NxpBadge from "../../ui/primitives/NxpBadge.vue";
import NxpButton from "../../ui/primitives/NxpButton.vue";
import NxpEmptyState from "../../ui/primitives/NxpEmptyState.vue";
import NxpEntityIcon from "../../ui/primitives/NxpEntityIcon.vue";
import NxpIcon from "../../ui/primitives/NxpIcon.vue";
import NxpNumberInput from "../../ui/primitives/NxpNumberInput.vue";
import NxpPathPicker from "../../ui/primitives/NxpPathPicker.vue";
import NxpSelect, { type NxpOption } from "../../ui/primitives/NxpSelect.vue";
import NxpSwitch from "../../ui/primitives/NxpSwitch.vue";

interface Script {
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
interface Plugin {
  name?: string;
  displayName?: string;
  kind?: string;
  configuredEnabled?: boolean;
  runtimeEnabled?: boolean;
  state?: string;
  supportsEmulator?: boolean;
  selfManagedPcLaunch?: boolean;
}
interface Draft extends Script {
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

const scripts = ref<Script[]>([]);
const plugins = ref<Plugin[]>([]);
const loading = ref(true);
const error = ref("");
const editorOpen = ref(false);
const chooserOpen = ref(false);
const confirmOpen = ref(false);
const editing = ref<Script | null>(null);
const deleteTarget = ref<Script | null>(null);
const root = ref<HTMLElement | null>(null);
const listSlotRoot = ref<HTMLElement | null>(null);
const editorSlotRoot = ref<HTMLElement | null>(null);
const draggingScriptId = ref("");
let disposed = false;

const draft = reactive<Draft>({
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
});
const specializedPlugins = computed(() =>
  plugins.value.filter(
    (plugin) =>
      plugin.kind === "data-specialized" &&
      plugin.configuredEnabled !== false &&
      plugin.runtimeEnabled !== false &&
      (!plugin.state || plugin.state === "Active"),
  ),
);
const hasSpecialized = computed(() => specializedPlugins.value.length > 0);
const gameModeOptions = computed<NxpOption[]>(() => [
  { value: "pc", label: t("scripts.pc_client") },
  { value: "emulator", label: t("scripts.android_emulator") },
]);
const judgeLanguageOptions = computed<NxpOption[]>(() => [
  { value: "javascript", label: t("scripts.javascript_built_in_engine") },
  { value: "python", label: t("scripts.python_system_interpreter") },
]);
const currentPlugin = computed(() =>
  plugins.value.find(
    (plugin) =>
      String(plugin.name || "").toLowerCase() ===
      String(draft.pluginType || "").toLowerCase(),
  ),
);
const emulatorAllowed = computed(
  () => !draft.pluginType || currentPlugin.value?.supportsEmulator === true,
);
const selfManagedPc = computed(
  () =>
    draft.gameMode !== "emulator" &&
    currentPlugin.value?.selfManagedPcLaunch === true,
);

function resetDraft(script: Script | null = null, plugin = "") {
  const value: Partial<Script> = script || {};
  editing.value = script;
  Object.assign(draft, {
    id: value.id || "",
    pluginType: value.pluginType || plugin,
    name: value.name || "",
    pluginInputs:
      value.pluginInputs && typeof value.pluginInputs === "object"
        ? { ...value.pluginInputs }
        : {},
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
  });
}
function openNew() {
  if (hasSpecialized.value) chooserOpen.value = true;
  else openEditor();
}
function openEditor(script: Script | null = null, plugin = "") {
  chooserOpen.value = false;
  resetDraft(script, plugin);
  editorOpen.value = true;
}
function closeEditor() {
  editorOpen.value = false;
  editing.value = null;
}
function askDelete(script: Script) {
  deleteTarget.value = script;
  confirmOpen.value = true;
}
function closeConfirm() {
  confirmOpen.value = false;
  deleteTarget.value = null;
}
function toggleJudge() {
  draft.judgeScriptEnabled = !draft.judgeScriptEnabled;
}
function toggleField(
  field: "launchGame" | "forceCloseGame" | "autoUpdateConfig",
) {
  draft[field] = !draft[field];
}
async function browseDraftPath(
  field: "rootPath" | "mainExe" | "configPath" | "logPath" | "gameExe",
  kind: "file" | "folder",
) {
  try {
    const result = await api("POST", "/api/native-dialog", {
      kind,
      title: t("common.select_path"),
      initialPath: draft.rootPath || undefined,
      filter: "",
    }) as { path?: string };
    if (result?.path) draft[field] = result.path;
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
function stripQuotes(value: string) {
  const trimmed = String(value || "").trim();
  return trimmed.length >= 2 &&
    ((trimmed.startsWith('"') && trimmed.endsWith('"')) ||
      (trimmed.startsWith("'") && trimmed.endsWith("'")))
    ? trimmed.slice(1, -1).trim()
    : trimmed;
}
function unavailable(script: Script) {
  return (
    scriptPluginStatus(script, plugins.value).specialized &&
    scriptPluginUnavailableMessage(script, plugins.value)
  );
}
function startScriptDrag(event: DragEvent, id: string) {
  draggingScriptId.value = id;
  if (event.dataTransfer) {
    event.dataTransfer.effectAllowed = "move";
    event.dataTransfer.setData("text/plain", id);
  }
}
function clearScriptDrag() {
  draggingScriptId.value = "";
}
async function dropScript(targetId: string) {
  const sourceId = draggingScriptId.value;
  clearScriptDrag();
  if (!sourceId || sourceId === targetId) return;
  const next = scripts.value.slice();
  const sourceIndex = next.findIndex(item => item.id === sourceId);
  const targetIndex = next.findIndex(item => item.id === targetId);
  if (sourceIndex < 0 || targetIndex < 0) return;
  const [moved] = next.splice(sourceIndex, 1);
  next.splice(targetIndex, 0, moved);
  scripts.value = next;
  try {
    await api("PUT", "/api/scripts/order", { ids: next.map(item => item.id) });
    toast(t("scripts.script_order_saved"));
  } catch (reason) {
    if (!isAbortError(reason)) toast(reason instanceof Error ? reason.message : String(reason), "error");
    await load();
  }
}
function pluginName(script: Script) {
  const plugin = plugins.value.find(
    (item) =>
      String(item.name || "").toLowerCase() ===
      String(script.pluginType || "").toLowerCase(),
  );
  return (
    plugin?.displayName ||
    plugin?.name ||
    script.pluginType ||
    t("scripts.general_script")
  );
}
function openScript(script: Script) {
  const message = unavailable(script);
  if (message) {
    toast(message, "error");
    return;
  }
  openEditor(script);
}
async function load() {
  loading.value = true;
  error.value = "";
  try {
    const [scriptData, status] = (await Promise.all([
      api("GET", "/api/scripts"),
      api("GET", "/api/status"),
    ])) as [Script[], { plugins?: Plugin[] }];
    if (disposed) return;
    scripts.value = Array.isArray(scriptData) ? scriptData : [];
    plugins.value = Array.isArray(status?.plugins) ? status.plugins : [];
  } catch (reason) {
    if (!disposed && !isAbortError(reason))
      error.value = reason instanceof Error ? reason.message : String(reason);
  } finally {
    if (!disposed) loading.value = false;
  }
  await paintListSlots();
}
async function save() {
  const required = [draft.name, draft.rootPath, draft.gameExe, draft.maxAttempts, draft.logStallTimeoutMinutes, draft.totalTimeoutMinutes];
  if (!draft.pluginType) required.push(draft.mainExe, draft.configPath, draft.logPath);
  if (required.some((value) => !String(value || "").trim())) {
    toast(t("scripts.validation.required_fields"), "error");
    return;
  }
  if (new TextEncoder().encode(draft.name.trim()).length > 64) {
    toast(t("scripts.validation.name_length", { bytes: 64 }), "error");
    return;
  }
  if (draft.judgeScriptEnabled && !draft.judgeScript.trim()) {
    toast(t("scripts.editor.judge_code_help"), "error");
    return;
  }
  const payload = {
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
  try {
    await api(
      draft.id ? "PUT" : "POST",
      draft.id
        ? `/api/scripts/${encodeURIComponent(draft.id)}`
        : "/api/scripts",
      payload,
    );
    closeEditor();
    toast(t("scripts.script_instance_saved"));
    await load();
  } catch (reason) {
    if (!isAbortError(reason)) toast(reason instanceof Error ? reason.message : String(reason), "error");
  }
}
async function removeScript() {
  const target = deleteTarget.value;
  if (!target) return;
  try {
    await api("DELETE", `/api/scripts/${encodeURIComponent(target.id)}`);
    closeConfirm();
    toast(t("scripts.script_instance_deleted"));
    await load();
  } catch (reason) {
    if (!isAbortError(reason)) toast(reason instanceof Error ? reason.message : String(reason), "error");
  }
}
async function paintListSlots() {
  await nextTick();
  const slots =
    root.value?.querySelectorAll<HTMLElement>(
      '[data-plugin-slot="scripts.list.badges"]',
    ) || [];
  for (const slot of slots)
    await renderPluginSlot(slot, "scripts.list.badges", {
      mode: "list",
      primaryId: slot.dataset.pluginPrimaryId || "",
    });
}
async function paintEditorSlot() {
  await nextTick();
  if (editorSlotRoot.value)
    await renderPluginSlot(editorSlotRoot.value, "scripts.editor.sections", {
      mode: draft.id ? "edit" : "create",
      primaryId: draft.id,
    });
}
onMounted(() => {
  disposed = false;
  setTopbarTitle(t("common.script_instance"));
  void load();
});
onBeforeUnmount(() => {
  disposed = true;
  const slots =
    root.value?.querySelectorAll<HTMLElement>("[data-plugin-slot]") || [];
  for (const slot of slots) void disposePluginSlot(slot);
});
</script>

<template>
  <main id="view" ref="root" class="view-root" data-testid="main-view">
    <NxpEmptyState v-if="loading" :title="t('common.loading')" />
    <NxpEmptyState
      v-else-if="error"
      :title="t('scripts.load.instances_failed')"
      :description="error"
      tone="danger"
    />
    <template v-else>
      <header class="page-head">
        <div class="page-head-copy">
          <div class="eyebrow">{{ t("scripts.automation_management") }}</div>
          <h2>{{ t("common.script_instance") }}</h2>
          <p class="page-kicker">{{ t("scripts.page.help") }}</p>
        </div>
        <div class="page-head-actions">
          <button
            class="primary"
            type="button"
            data-testid="new-script"
            @click="openNew"
          >
            {{ t("scripts.new_script_instance") }}
          </button>
        </div>
      </header>
      <NxpEmptyState
        v-if="!scripts.length"
        :title="t('scripts.no_script_instances_yet')"
        :description="t('scripts.page.empty_help')"
        ><button class="back-link" type="button" @click="openNew">
          {{ t("scripts.new_script_instance") }}
        </button></NxpEmptyState
      >
      <section v-else class="card list-surface">
        <div class="script-grid">
          <article
            v-for="script in scripts"
            :key="script.id"
            class="script-card"
            :class="{ 'is-unavailable': unavailable(script) }"
            data-testid="script-card"
            @dragover.prevent
            @drop="dropScript(script.id)"
          >
            <span
              class="drag-handle"
              role="button"
              tabindex="0"
              :aria-label="t('common.reorder.keyboard_help')"
              :title="t('common.drag_to_reorder')"
              draggable="true"
              @dragstart="startScriptDrag($event, script.id)"
              @dragend="clearScriptDrag"
              ><NxpIcon name="grip" /></span
            ><NxpEntityIcon :id="script.id" />
            <div class="script-main">
              <button
                class="entity-link"
                type="button"
                :disabled="Boolean(unavailable(script))"
                :aria-label="
                  t('scripts.accessibility.instance_action', {
                    action: unavailable(script)
                      ? t('common.error.specialized_script_instance')
                      : t('scripts.edit_script_instance'),
                    name: script.name,
                  })
                "
                @click.stop="openScript(script)"
              >
                <span class="scroll-text"
                  ><span class="scroll-inner">{{ script.name }}</span></span
                >
              </button>
              <div class="meta-line script-meta">
                <NxpBadge
                  :tone="
                    script.pluginType
                      ? unavailable(script)
                        ? 'warn'
                        : 'muted'
                      : 'muted'
                  "
                  >{{
                    script.pluginType
                      ? pluginName(script)
                      : t("scripts.general_script")
                  }}</NxpBadge
                ><NxpBadge v-if="script.launchGame" tone="muted">{{
                  script.gameMode === "emulator"
                    ? t("scripts.android_emulator")
                    : t("scripts.pc_client")
                }}</NxpBadge
                ><NxpBadge
                  v-if="script.judgeScriptEnabled && script.judgeScript"
                  tone="muted"
                  >{{ t("scripts.judge_script") }}</NxpBadge
                ><NxpBadge
                  v-else-if="script.successKeywords || script.failureKeywords"
                  tone="muted"
                  >{{ t("scripts.keyword_judge") }}</NxpBadge
                ><NxpBadge
                  v-if="script.logStallTimeoutMinutes === -1"
                  tone="warn"
                  >{{ t("scripts.long_running_policy") }}</NxpBadge
                ><span
                  class="plugin-slot script-plugin-slot"
                  data-plugin-slot="scripts.list.badges"
                  data-plugin-anchor="scripts.list.badges"
                  data-plugin-mode="list"
                  :data-plugin-primary-id="script.id"
                  hidden
                ></span>
              </div>
            </div>
            <div class="script-ops row-actions entity-actions">
              <button
                class="tertiary"
                type="button"
                :disabled="Boolean(unavailable(script))"
                @click.stop="openScript(script)"
              >
                {{ t("scripts.edit_script") }}</button
              ><button
                class="danger"
                type="button"
                data-action="delete-script"
                :data-id="script.id"
                :data-name="script.name"
                @click.stop="askDelete(script)"
              >
                {{ t("scripts.delete_script") }}
              </button>
            </div>
          </article>
        </div>
      </section>
    </template>

    <div v-if="chooserOpen" class="modal-mask" role="presentation" data-locked>
      <section
        class="modal secondary-surface"
        role="dialog"
        aria-modal="true"
        :aria-label="t('scripts.new_script_instance')"
      >
        <div class="modal-header">
          <h2 class="modal-title">{{ t("scripts.new_script_instance") }}</h2>
          <button
            class="modal-close"
            type="button"
            :aria-label="t('common.close', {}, 'Close')"
            @click.stop="chooserOpen = false"
          >
            <NxpIcon name="close" />
          </button>
        </div>
        <div class="modal-body new-script-chooser">
          <button
            class="chooser-card"
            type="button"
            @click.stop="openEditor(null, '')"
          >
            <strong>{{ t("scripts.new_general_script_instance") }}</strong
            ><span class="muted">{{
              t("scripts.editor.manual_config_help")
            }}</span></button
          ><button
            v-for="plugin in specializedPlugins"
            :key="plugin.name"
            class="chooser-card"
            type="button"
            @click.stop="openEditor(null, plugin.name || '')"
          >
            <strong>{{
              t("scripts.action.create_specialized", {
                plugin: plugin.displayName || plugin.name || "",
              })
            }}</strong
            ><span class="muted">{{ t("scripts.plugin.config_auto") }}</span>
          </button>
        </div>
        <div class="modal-footer">
          <button class="ghost" type="button" @click.stop="chooserOpen = false">
            {{ t("common.cancel") }}
          </button>
        </div>
      </section>
    </div>

    <div v-if="editorOpen" class="modal-mask" role="presentation" data-locked>
      <section
        class="modal wide secondary-surface"
        role="dialog"
        aria-modal="true"
        :aria-label="
          editing
            ? t('scripts.edit_script_instance')
            : t('scripts.new_script_instance')
        "
      >
        <div class="modal-header">
          <h2 class="modal-title">
            {{
              editing
                ? t("scripts.edit_script_instance")
                : draft.pluginType
                  ? t("scripts.action.new_specialized", {
                      plugin: pluginName(draft),
                    })
                  : t("scripts.new_general_script_instance")
            }}
          </h2>
          <button
            class="modal-close"
            type="button"
            :aria-label="t('common.close', {}, 'Close')"
            @click.stop="closeEditor"
          >
            <NxpIcon name="close" />
          </button>
        </div>
        <div class="modal-body">
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
              <div class="switch-row settings-option switch-card">
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
                  v-model="draft.launchGame"
                  :aria-label="t('scripts.launch_game')"
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
                <div class="field">
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
                <div class="field">
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
                <div class="field">
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
                <div class="field">
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
              <div class="field">
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
              <div class="field">
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
              <div class="field">
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
              <div class="field">
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
        </div>
        <div class="modal-footer">
          <button class="ghost" type="button" @click.stop="closeEditor">
            {{ t("common.cancel") }}</button
          ><button class="primary" type="button" @click.stop="save">
            {{ t("common.save") }}
          </button>
        </div>
      </section>
    </div>

    <div
      v-if="confirmOpen && deleteTarget"
      class="modal-mask"
      role="presentation"
    >
      <section
        class="modal secondary-surface"
        role="dialog"
        aria-modal="true"
        :aria-label="t('common.delete')"
      >
        <div class="modal-header">
          <h2 class="modal-title">{{ t("scripts.delete_script_instance") }}</h2>
          <button
            class="modal-close"
            type="button"
            :aria-label="t('common.close', {}, 'Close')"
            @click.stop="closeConfirm"
          >
            <NxpIcon name="close" />
          </button>
        </div>
        <div class="modal-body">
          <p class="modal-copy">
            {{
              t("scripts.confirm_delete_script_instance", {
                name: deleteTarget.name,
              })
            }}
          </p>
        </div>
        <div class="modal-footer">
          <button class="ghost" type="button" @click.stop="closeConfirm">
            {{ t("common.cancel") }}</button
          ><button
            class="danger"
            type="button"
            data-action="confirm-delete-script"
            @click.stop="removeScript"
          >
            {{ t("common.confirm") }}
          </button>
        </div>
      </section>
    </div>
  </main>
</template>
