<script setup lang="ts">
import { nextTick, ref } from "vue";
import NxpButton from "../../../ui/primitives/NxpButton.vue";

defineProps<{ title: string; description: string; revealLabel: string; contentId: string }>();
const revealed = ref(false);
const heading = ref<HTMLElement | null>(null);
async function reveal() {
  revealed.value = true;
  await nextTick();
  heading.value?.focus({ preventScroll: true });
}
</script>

<template>
  <div class="history-log-disclosure">
    <NxpButton v-if="!revealed" class="history-log-toggle" :aria-controls="contentId" @click="reveal">
      <span class="history-log-toggle-copy"><strong>{{ title }}</strong><span class="muted">{{ revealLabel }}</span></span>
    </NxpButton>
    <template v-else>
      <div ref="heading" class="qk-row history-log-heading" tabindex="-1">{{ title }} · {{ description }}</div>
      <div :id="contentId" class="history-log-content"><slot /></div>
    </template>
  </div>
</template>

<style scoped>
.history-log-heading:focus { outline: none; }
</style>
