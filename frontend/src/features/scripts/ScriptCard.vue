<script setup lang="ts">
import NxpBadge from "../../ui/primitives/NxpBadge.vue";
import NxpCard from "../../ui/primitives/NxpCard.vue";
import NxpEntityIcon from "../../ui/primitives/NxpEntityIcon.vue";
import NxpIcon from "../../ui/primitives/NxpIcon.vue";

type Translator = (
  key: string,
  args?: Record<string, unknown>,
  fallback?: string,
) => string;

interface ScriptCardScript {
  id: string;
  name: string;
  pluginType?: string;
  launchGame?: boolean;
  gameMode?: string;
  judgeScriptEnabled?: boolean;
  judgeScript?: string;
  successKeywords?: string;
  failureKeywords?: string;
  logStallTimeoutMinutes?: number;
}

defineProps<{
  script: ScriptCardScript;
  pluginLabel: string;
  unavailableMessage?: string;
  translate: Translator;
}>();

const emit = defineEmits<{
  edit: [script: ScriptCardScript];
  remove: [script: ScriptCardScript];
}>();
</script>

<template>
  <NxpCard
    as="article"
    unstyled
    class="script-card"
    :class="{ 'is-unavailable': unavailableMessage }"
    data-testid="script-card"
    :data-dnd-id="script.id"
  >
    <span
      class="drag-handle"
      role="button"
      tabindex="0"
      :aria-label="translate('common.reorder.keyboard_help')"
      :title="translate('common.drag_to_reorder')"
      ><NxpIcon name="grip" /></span
    ><NxpEntityIcon :id="script.id" />
    <div class="script-main">
      <button
        class="entity-link"
        type="button"
        :disabled="Boolean(unavailableMessage)"
        :aria-label="
          translate('scripts.accessibility.instance_action', {
            action: unavailableMessage
              ? translate('common.error.specialized_script_instance')
              : translate('scripts.edit_script_instance'),
            name: script.name,
          })
        "
        @click.stop="emit('edit', script)"
      >
        <span class="scroll-text"
          ><span class="scroll-inner">{{ script.name }}</span></span
        >
      </button>
      <div class="meta-line script-meta">
        <NxpBadge :tone="script.pluginType && unavailableMessage ? 'warn' : 'muted'">{{
          script.pluginType
            ? pluginLabel
            : translate("scripts.general_script")
        }}</NxpBadge
        ><NxpBadge v-if="script.launchGame" tone="muted">{{
          script.gameMode === "emulator"
            ? translate("scripts.android_emulator")
            : translate("scripts.pc_client")
        }}</NxpBadge
        ><NxpBadge
          v-if="script.judgeScriptEnabled && script.judgeScript"
          tone="muted"
          >{{ translate("scripts.judge_script") }}</NxpBadge
        ><NxpBadge
          v-else-if="script.successKeywords || script.failureKeywords"
          tone="muted"
          >{{ translate("scripts.keyword_judge") }}</NxpBadge
        ><NxpBadge
          v-if="script.logStallTimeoutMinutes === -1"
          tone="warn"
          >{{ translate("scripts.long_running_policy") }}</NxpBadge
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
        :disabled="Boolean(unavailableMessage)"
        @click.stop="emit('edit', script)"
      >
        {{ translate("scripts.edit_script") }}</button
      ><button
        class="danger"
        type="button"
        data-action="delete-script"
        :data-id="script.id"
        :data-name="script.name"
        @click.stop="emit('remove', script)"
      >
        {{ translate("scripts.delete_script") }}
      </button>
    </div>
  </NxpCard>
</template>
