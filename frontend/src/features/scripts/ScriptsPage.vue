<script setup lang="ts">
import {
  computed,
  nextTick,
  onBeforeUnmount,
  onMounted,
  ref,
} from "vue";
import { isAbortError } from "@legacy/core/api.js";
import { renderPluginSlot } from "@legacy/core/plugin-slots.js";
import { disposePluginSlot } from "@legacy/core/plugin-runtime.js";
import {
  scriptPluginStatus,
  scriptPluginUnavailableMessage,
} from "@legacy/core/format.js";
import { t } from "@legacy/core/i18n.js";
import { setTopbarTitle, toast } from "@legacy/core/ui.js";
import NxpButton from "../../ui/primitives/NxpButton.vue";
import NxpEmptyState from "../../ui/primitives/NxpEmptyState.vue";
import NxpIcon from "../../ui/primitives/NxpIcon.vue";
import NxpModal from "../../ui/primitives/NxpModal.vue";
import { vSortable } from "../../ui/sortable";
import ScriptCard from "./ScriptCard.vue";
import ScriptEditorModal from "./ScriptEditorModal.vue";
import { deleteScript, listScripts, getStatus, reorderScripts as reorderScriptsRequest } from "./services/scriptsApi";
import type { Script, ScriptPlugin } from "./utils/scriptTypes";

const scripts = ref<Script[]>([]);
const plugins = ref<ScriptPlugin[]>([]);
const loading = ref(true);
const error = ref("");
const editorOpen = ref(false);
const chooserOpen = ref(false);
const confirmOpen = ref(false);
const editing = ref<Script | null>(null);
const editorPlugin = ref("");
const deleteTarget = ref<Script | null>(null);
const root = ref<HTMLElement | null>(null);
let disposed = false;

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

function openNew() {
  if (hasSpecialized.value) chooserOpen.value = true;
  else openEditor();
}
function openEditor(script: Script | null = null, plugin = "") {
  chooserOpen.value = false;
  editing.value = script;
  editorPlugin.value = plugin;
  editorOpen.value = true;
}
function closeEditor() {
  editorOpen.value = false;
  editing.value = null;
  editorPlugin.value = "";
}
function askDelete(script: Script) {
  deleteTarget.value = script;
  confirmOpen.value = true;
}
function closeConfirm() {
  confirmOpen.value = false;
  deleteTarget.value = null;
}
function unavailable(script: Script) {
  return (
    scriptPluginStatus(script, plugins.value).specialized &&
    scriptPluginUnavailableMessage(script, plugins.value)
  );
}
function pluginName(script: Script) {
  const plugin = plugins.value.find(
    (item) =>
      String(item.name || "").toLowerCase() ===
      String(script.pluginType || "").toLowerCase(),
  );
  return plugin?.displayName || plugin?.name || script.pluginType || t("scripts.general_script");
}
function openScript(script: Script) {
  const message = unavailable(script);
  if (message) {
    toast(message, "error");
    return;
  }
  openEditor(script);
}
async function reorderScripts(ids: string[]) {
  const byId = new Map(scripts.value.map(script => [script.id, script]));
  const next = ids.map(id => byId.get(id)).filter((script): script is Script => Boolean(script));
  if (next.length !== scripts.value.length || next.every((script, index) => script.id === scripts.value[index]?.id)) return;
  scripts.value = next;
  try {
    await reorderScriptsRequest(next.map(item => item.id));
    toast(t("scripts.script_order_saved"));
  } catch (reason) {
    if (!isAbortError(reason)) toast(reason instanceof Error ? reason.message : String(reason), "error");
    await load();
  }
}
async function load() {
  loading.value = true;
  error.value = "";
  try {
    const [scriptData, status] = (await Promise.all([
      listScripts(),
      getStatus(),
    ])) as [Script[], { plugins?: ScriptPlugin[] }];
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
async function removeScript() {
  const target = deleteTarget.value;
  if (!target) return;
  try {
    await deleteScript(target.id);
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
      />
      <section v-else class="card list-surface">
        <TransitionGroup v-sortable="{ onDrop: reorderScripts }" name="nxp-card" tag="div" class="script-grid">
          <ScriptCard
            v-for="script in scripts"
            :key="script.id"
            :script="script"
            :plugin-label="pluginName(script)"
            :unavailable-message="unavailable(script) || undefined"
            :translate="t"
            @edit="openScript"
            @remove="askDelete"
          />
        </TransitionGroup>
      </section>
    </template>

    <NxpModal
      :open="chooserOpen"
      :closeable="false"
      :locked="true"
      :aria-label="t('scripts.new_script_instance')"
      panel-class="secondary-surface"
      data-locked
    >
      <template #header>
          <h2 class="modal-title">{{ t("scripts.new_script_instance") }}</h2>
          <button
            class="icon-button modal-close"
            type="button"
            :aria-label="t('common.close', {}, 'Close')"
            @click.stop="chooserOpen = false"
          >
            <NxpIcon name="close" />
          </button>
      </template>
      <div class="new-script-chooser">
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
            <strong class="scroll-text"
              ><span class="scroll-inner">{{
                t("scripts.action.create_specialized", {
                  plugin: plugin.displayName || plugin.name || "",
                })
              }}</span></strong
            ><span class="muted">{{ t("scripts.plugin.config_auto") }}</span>
          </button>
        </div>
      <template #footer>
          <button class="ghost" type="button" @click.stop="chooserOpen = false">
            {{ t("common.cancel") }}
          </button>
      </template>
    </NxpModal>
    <ScriptEditorModal
      v-if="editorOpen"
      :script="editing"
      :plugin="editorPlugin"
      :plugins="plugins"
      @close="closeEditor"
      @saved="load"
    />
    <NxpModal
      :open="confirmOpen && Boolean(deleteTarget)"
      :title="t('scripts.delete_script_instance')"
      panel-class="secondary-surface"
      :close-label="t('common.close', {}, 'Close')"
      @close="closeConfirm"
    >
      <p v-if="deleteTarget" class="modal-copy">
        {{ t("scripts.confirm_delete_script_instance", { name: deleteTarget.name }) }}
      </p>
      <template #footer>
        <NxpButton class="ghost" type="button" @click="closeConfirm">{{
          t("common.cancel")
        }}</NxpButton>
        <NxpButton
          class="danger"
          type="button"
          data-action="confirm-delete-script"
          @click="removeScript"
          >{{ t("common.confirm") }}</NxpButton
        >
      </template>
    </NxpModal>
  </main>
</template>
