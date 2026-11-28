<script setup lang="ts">
import { t } from '../app/locale';
import { extensionIcon } from '../control/extension-icons';
import type { AutomationBundle } from '../control/wire.generated';

defineProps<{
  binding: AutomationBundle['bindings'][number];
  script?: AutomationBundle['scripts'][number];
}>();
</script>

<template>
  <span class="binding-identity">
    <img :src="extensionIcon(script?.extensionId)" alt="" />
    <span class="binding-label">
      <strong>{{ script?.name }}</strong>
      <span class="binding-badges">
        <nexus-badge :tone="binding.enabled ? 'success' : 'neutral'" :label="t(binding.enabled ? '已启用' : '已停用')" />
        <nexus-badge :label="binding.runDays === -1 ? t('永久运行') : t('运行天数') + ' ' + binding.runDays" />
      </span>
    </span>
  </span>
</template>

<style scoped>
.binding-identity {
  display: flex;
  align-items: center;
  flex: 1;
  gap: 12px;
  min-width: 0;
}
img { width: 28px; height: 28px; flex: none; object-fit: contain; }
.binding-label { display: grid; gap: 4px; min-width: 0; }
.binding-label strong { font-size: 13px; overflow-wrap: anywhere; }
.binding-badges { display: flex; flex-wrap: wrap; gap: 6px; }
</style>
