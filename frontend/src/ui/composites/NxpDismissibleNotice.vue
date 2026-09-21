<script setup lang="ts">
import NxpCollapseTransition from "./NxpCollapseTransition.vue";
import NxpIconButton from "../primitives/NxpIconButton.vue";
import NxpIcon from "../primitives/NxpIcon.vue";
defineProps<{ visible: boolean; closeLabel: string }>();
defineEmits<{ dismiss: [] }>();
</script>

<template>
  <NxpCollapseTransition>
    <aside v-if="visible" class="nxp-dismissible-notice" role="status">
      <div class="nxp-dismissible-notice-copy"><slot /></div>
      <NxpIconButton :label="closeLabel" @click="$emit('dismiss')"><NxpIcon name="close" /></NxpIconButton>
    </aside>
  </NxpCollapseTransition>
</template>

<style scoped>
.nxp-dismissible-notice { display: flex; align-items: flex-start; gap: 12px; padding: 12px; border: 1px solid var(--warn, var(--nx-color-warning)); border-radius: var(--nx-radius-md); background: var(--content-card-soft); color: var(--warn, var(--nx-color-warning)); }
.nxp-dismissible-notice-copy { flex: 1; min-width: 0; font-size: 12px; line-height: 1.7; }
</style>
