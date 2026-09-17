<script setup lang="ts">
import NxpBadge from "../../ui/primitives/NxpBadge.vue";
import NxpEntityIcon from "../../ui/primitives/NxpEntityIcon.vue";
import NxpButton from "../../ui/primitives/NxpButton.vue";
import NxpActionGroup from "../../ui/composites/NxpActionGroup.vue";
import NxpDragHandle from "../../ui/composites/NxpDragHandle.vue";
import NxpEntityRow from "../../ui/composites/NxpEntityRow.vue";
import type { Queue, QueuePluginIssue, QueueTranslator } from "./queueTypes";

defineProps<{
  queue: Queue;
  firstScriptId: string;
  nextLabel: string;
  pluginIssue?: QueuePluginIssue | null;
  translate: QueueTranslator;
}>();

const emit = defineEmits<{
  edit: [queue: Queue];
  remove: [queue: Queue];
}>();
</script>

<template>
  <NxpEntityRow
    as="article"
    class="script-card queue-card"
    data-testid="queue-card"
    :item-id="queue.id"
  >
    <template #leading>
      <NxpDragHandle
        :label="translate('common.reorder.keyboard_help')"
        :title="translate('common.drag_to_reorder')"
      />
      <NxpEntityIcon :id="firstScriptId" />
    </template>
    <template #content>
      <div class="script-main">
        <NxpButton
          class="entity-link"
          type="button"
          data-action="edit-queue"
          :data-id="queue.id"
          :aria-label="translate('queues.editor.title', { name: queue.name })"
          @click="emit('edit', queue)"
        >
          <span class="scroll-text"><span class="scroll-inner">{{ queue.name }}</span></span>
        </NxpButton>
        <div class="meta-line queue-meta">
          <NxpBadge tone="muted" data-testid="queue-card-tasks-badge">{{ translate("common.unit.tasks", { count: queue.tasks?.length || 0 }) }}</NxpBadge>
          <NxpBadge tone="muted" data-testid="queue-card-completion-badge">{{ queue.completionAction && queue.completionAction !== "none" ? translate("queues.completion.label", { action: queue.completionAction }) : translate("queues.completion.none") }}</NxpBadge>
          <NxpBadge v-if="pluginIssue" :tone="pluginIssue.tone" :title="pluginIssue.title">{{ pluginIssue.label }}</NxpBadge>
          <NxpBadge v-if="queue.autoRunMode === 'none'" tone="blue" data-testid="queue-card-manual-badge">{{ translate("queues.manual_only") }}</NxpBadge>
          <NxpBadge tone="blue" data-testid="queue-next">{{ nextLabel }}</NxpBadge>
          <NxpBadge :tone="queue.notifyEnabled ? 'ok' : 'muted'" data-testid="queue-card-notify-badge">{{ queue.notifyEnabled ? translate("queues.notification.enabled") : translate("queues.notification.disabled") }}</NxpBadge>
          <div
            class="plugin-slot queue-plugin-slot"
            data-plugin-slot="queues.list.badges"
            data-plugin-anchor="queues.list.badges"
            data-plugin-mode="list"
            :data-plugin-primary-id="queue.id"
            hidden
          ></div>
        </div>
      </div>
    </template>
    <template #actions>
      <NxpActionGroup class="queue-ops row-actions entity-actions">
        <NxpButton
          class="tertiary queue-edit"
          type="button"
          data-action="edit-queue-direct"
          :data-id="queue.id"
          @click="emit('edit', queue)"
        >{{ translate("queues.edit_queue_button") }}</NxpButton>
        <NxpButton
          tone="danger"
          type="button"
          data-action="delete-queue"
          :data-id="queue.id"
          :data-name="queue.name"
          @click="emit('remove', queue)"
        >{{ translate("queues.delete_queue") }}</NxpButton>
      </NxpActionGroup>
    </template>
  </NxpEntityRow>
</template>
