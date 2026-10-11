<script setup lang="ts">
import { computed } from "vue";
import NxpModal from "../../ui/primitives/NxpModal.vue";
import NxpButton from "../../ui/primitives/NxpButton.vue";
import NxpSortableList from "../../ui/composites/NxpSortableList.vue";
import NxpDragHandle from "../../ui/composites/NxpDragHandle.vue";
import { formatList, t } from "../../platform/i18n";
import type { DashboardLayoutEditor } from "./layout-editor";

const props = defineProps<{ editor: DashboardLayoutEditor }>();
const current = computed(() => props.editor.draft.map(id => props.editor.committed?.cards.find(card => card.cardId === id)).filter(card => card !== undefined));
const available = computed(() => props.editor.committed?.cards.filter(card => !props.editor.draft.includes(card.cardId)) ?? []);
const frozen = computed(() => props.editor.saving || props.editor.pending);
function loadLatest() {
  if (window.confirm(t("dashboard.layout.discard_confirm"))) props.editor.loadLatest();
}
function close() {
  if (props.editor.pending && !window.confirm(t("dashboard.layout.pending_cancel"))) return;
  props.editor.cancel();
}
</script>

<template>
  <NxpModal :open="editor.editing" :title="t('dashboard.layout.edit')" :close-label="t('common.close')"
    surface="secondary" size="wide" :locked="editor.saving" footer @close="close">
    <div class="dashboard-layout-editor">
      <p class="muted">{{ t('dashboard.layout.preview_help') }}</p>
      <div v-if="editor.pending" class="callout callout-warning" role="status">
        <p>{{ t('dashboard.layout.pending') }}</p>
        <NxpButton :busy="editor.reading" @click="editor.refresh()">{{ t('dashboard.layout.read_again') }}</NxpButton>
      </div>
      <div v-else-if="editor.conflict" class="callout callout-warning" role="alert">
        <p>{{ t('dashboard.layout.conflict') }}</p>
        <div class="dashboard-layout-actions">
          <NxpButton :disabled="!editor.connected || editor.reading" @click="loadLatest">{{ t('dashboard.layout.load_latest') }}</NxpButton>
          <NxpButton :disabled="!editor.connected || editor.reading" @click="editor.rebase()">{{ t('dashboard.layout.keep_mine') }}</NxpButton>
        </div>
      </div>
      <p v-if="!editor.connected" class="muted" role="status">{{ t('dashboard.layout.disconnected') }}</p>
      <p v-if="editor.error" role="alert">{{ t(`api.error.${editor.error}`) }}</p>
      <NxpButton v-if="!editor.connected && !editor.pending" :busy="editor.reading" @click="editor.refresh()">{{ t('dashboard.layout.read_again') }}</NxpButton>
      <p v-if="editor.removed.length || editor.added.length" role="status">{{ t('dashboard.layout.rebased', { removed: editor.removed.length, added: editor.added.length }) }}</p>
      <p v-if="editor.removedLabels.length">{{ t('dashboard.layout.removed_items', { items: formatList(editor.removedLabels) }) }}</p>
      <p v-if="editor.addedLabels.length">{{ t('dashboard.layout.added_items', { items: formatList(editor.addedLabels) }) }}</p>
      <section :aria-label="t('dashboard.layout.current')">
        <h3>{{ t('dashboard.layout.current') }}</h3>
        <NxpSortableList :disabled="frozen" class="dashboard-layout-list" @reorder="ids => editor.select(ids)">
          <p v-if="!current.length" class="dashboard-layout-empty muted">{{ t('dashboard.layout.none') }}</p>
          <article v-for="card in current" :key="card.cardId" :data-dnd-id="card.cardId" class="dashboard-layout-row">
            <NxpDragHandle class="dashboard-layout-handle" :disabled="frozen" :label="t('dashboard.layout.drag', { title: card.title })" :title="t('common.reorder.keyboard_help')" />
            <div class="dashboard-layout-copy"><strong>{{ card.title }}</strong><p v-if="card.description">{{ card.description }}</p></div>
            <div class="dashboard-layout-actions">
              <NxpButton :disabled="frozen" :aria-label="t('dashboard.layout.remove', { title: card.title })" @click="editor.select(editor.draft.filter(id => id !== card.cardId))">{{ t('dashboard.layout.remove_button') }}</NxpButton>
            </div>
          </article>
        </NxpSortableList>
      </section>
      <section :aria-label="t('dashboard.layout.available')">
        <h3>{{ t('dashboard.layout.available') }}</h3>
        <div class="dashboard-layout-list">
          <p v-if="!available.length" class="dashboard-layout-empty muted">{{ t('dashboard.layout.all_added') }}</p>
          <article v-for="card in available" :key="card.cardId" class="dashboard-layout-row">
            <div class="dashboard-layout-copy"><strong>{{ card.title }}</strong><p v-if="card.description">{{ card.description }}</p></div>
            <NxpButton :disabled="frozen" :aria-label="t('dashboard.layout.add', { title: card.title })" @click="editor.select([...editor.draft, card.cardId])">{{ t('dashboard.layout.add_button') }}</NxpButton>
          </article>
        </div>
      </section>
    </div>
    <template #footer>
      <div class="dashboard-layout-actions dashboard-layout-footer">
        <NxpButton :disabled="frozen || editor.conflict" @click="editor.defaults()">{{ t('dashboard.layout.defaults') }}</NxpButton>
        <NxpButton :disabled="editor.saving" @click="close">{{ t('common.cancel') }}</NxpButton>
        <NxpButton tone="primary" :busy="editor.saving" :disabled="!editor.canSave" @click="editor.save()">{{ t('common.save') }}</NxpButton>
      </div>
    </template>
  </NxpModal>
</template>

<style scoped>
.dashboard-layout-editor { display: grid; gap: var(--space-4); min-width: 0; }
.dashboard-layout-list { display: grid; gap: 0; min-width: 0; overflow: hidden; border: 1px solid var(--content-card-border); border-radius: var(--radius-lg); background: var(--content-card); }
.dashboard-layout-editor h3 { margin: 0 0 var(--space-3); }
.dashboard-layout-editor p { line-height: 1.6; }
.dashboard-layout-row { display: flex; align-items: center; gap: var(--space-3); min-height: 64px; padding: 12px 16px; min-width: 0; }
.dashboard-layout-row + .dashboard-layout-row { border-top: 1px solid var(--border); }
.dashboard-layout-empty { margin: 0; padding: 16px; }
.dashboard-layout-copy { flex: 1; min-width: 0; overflow-wrap: anywhere; }
.dashboard-layout-handle, .dashboard-layout-handle:hover, .dashboard-layout-handle:focus-visible { border-color: transparent; }
.dashboard-layout-copy p { margin: 4px 0 0; color: var(--muted); font-size: 12px; }
.dashboard-layout-actions { display: flex; flex-wrap: wrap; align-items: center; gap: var(--space-2); }
.dashboard-layout-footer { justify-content: flex-end; }
@media (max-width: 600px) {
  .dashboard-layout-row { gap: var(--space-2); padding: 12px; }
  .dashboard-layout-actions { flex-shrink: 0; }
}
</style>
