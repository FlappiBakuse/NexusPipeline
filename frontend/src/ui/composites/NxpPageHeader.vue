<script setup lang="ts">
/** 页面级头部：统一 eyebrow、标题、说明与右侧 actions 的 `.page-head` 结构。
 *  视觉沿用 shell 既有类，调用方按页面需要填插槽。 */
const props = withDefaults(defineProps<{ eyebrow?: string; title?: string; description?: string }>(), {
  eyebrow: "",
  title: "",
  description: "",
});
</script>

<template>
  <header class="page-head nxp-page-header" :class="{ 'has-actions': Boolean($slots.actions) }">
    <div class="page-head-copy">
      <div v-if="props.eyebrow || $slots.eyebrow" class="eyebrow">
        <slot name="eyebrow">{{ props.eyebrow }}</slot>
      </div>
      <h2><slot name="title">{{ props.title }}</slot></h2>
      <p v-if="props.description || $slots.description" class="page-kicker">
        <slot name="description">{{ props.description }}</slot>
      </p>
    </div>
    <div v-if="$slots.actions" class="page-head-actions"><slot name="actions" /></div>
  </header>
</template>

<style>
.nxp-page-header.has-actions { grid-template-columns: minmax(0, 1fr) auto; }
</style>
