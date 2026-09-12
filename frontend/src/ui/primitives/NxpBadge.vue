<script setup lang="ts">
withDefaults(defineProps<{
  tone?: "muted" | "blue" | "ok" | "warn" | "bad";
  /** 徽章文案；自定义元素消费方优先使用该属性，插槽内容作为替代写法。 */
  label?: string;
}>(), { tone: "muted", label: "" });
</script>

<template><span class="badge nxp-badge" :class="tone"><slot>{{ label }}</slot></span></template>

<style>
:host {
  display: inline-flex;
  min-width: 0;
  vertical-align: middle;
}
</style>
