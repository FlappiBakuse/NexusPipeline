<script setup lang="ts">
import { ref } from 'vue';
import { registerMessages, t } from '../app/locale';
import { scriptLogRoleLabels, type ScriptLogRole, type ScriptLogSelection } from './script-log-roles';

defineProps<{ title: string; roles: readonly ScriptLogRole[]; selected: ScriptLogSelection; live?: boolean }>();
const emit = defineEmits<{ select: [role: ScriptLogSelection]; scroll: [] }>();
const viewport = ref<HTMLElement>();
defineExpose({ viewport });

registerMessages({ 实时日志: 'Live log', 前置: 'Pre-hook', 主任务: 'Main task', 后置: 'Post-hook' });
</script>

<template>
  <div class="script-log-panel" :class="{ 'script-log-live': live }">
    <header class="script-log-toolbar">
      <strong>{{ title }}</strong>
      <div v-if="roles.length" class="script-log-roles" role="group" :aria-label="t('日志来源')">
        <button v-for="role in (['all', ...roles] as const)" :key="role" type="button"
          :data-role="role" :aria-pressed="selected === role" @click="emit('select', role)">
          {{ t(scriptLogRoleLabels[role]) }}
        </button>
      </div>
    </header>
    <nexus-scroll-area ref="viewport" class="script-log-body" :class="live ? 'run-log' : 'history-log-preview'"
      direction="vertical" :label="title" @scroll="emit('scroll')">
      <slot />
    </nexus-scroll-area>
  </div>
</template>

<style scoped>
.script-log-panel {
  display: flex;
  flex-direction: column;
  min-width: 0;
  border: 1px solid var(--border);
  border-radius: 8px;
  overflow: clip;
  background: var(--log-bg);
  color: var(--log-text);
}
.script-log-live { height: 100%; }
.script-log-toolbar {
  display: flex;
  flex-wrap: wrap;
  justify-content: space-between;
  align-items: center;
  gap: 4px 8px;
  padding: 5px 9px;
  border-bottom: 1px solid var(--border);
  font-size: 12px;
}
.script-log-toolbar strong { color: var(--muted); }
.script-log-roles {
  display: flex;
  flex-wrap: wrap;
  gap: 4px;
}
.script-log-roles button {
  min-height: 24px;
  padding: 1px 6px;
  border-color: transparent;
  border-radius: 5px;
  background: transparent;
  color: var(--muted);
  font-size: 12px;
}
.script-log-roles button[aria-pressed='true'] {
  border-color: var(--border);
  background: var(--accent-soft);
  color: var(--text);
}
.script-log-body {
  min-width: 0;
  min-height: 0;
  flex: 1;
}
.history-log-preview {
  min-height: 84px;
  max-height: 260px;
  background: inherit;
}
.script-log-body :slotted(pre) {
  margin: 0;
  padding: 12px;
  color: var(--log-text);
  font: 12px/1.6 'Cascadia Mono', Consolas, monospace;
  white-space: pre-wrap;
  overflow-wrap: anywhere;
}
</style>
