<script setup lang="ts">
import NxpOverflowText from "./NxpOverflowText.vue";
withDefaults(defineProps<{
  tone?: "muted" | "blue" | "ok" | "warn" | "bad";
  /** 徽章文案；自定义元素消费方优先使用该属性，插槽内容作为替代写法。 */
  label?: string;
  wrap?: boolean;
}>(), { tone: "muted", label: "", wrap: false });
</script>

<template><span class="nxp-badge" :class="[tone, { 'is-wrapping': wrap }]"><NxpOverflowText :label="label" :wrap="wrap"><slot>{{ label }}</slot></NxpOverflowText></span></template>

<style>
:host {
  display: inline-flex;
  min-width: 0;
  vertical-align: middle;
}
.nxp-badge { display: inline-flex; flex: 0 0 auto; align-items: center; min-height: var(--nx-badge-min-height, 24px); padding: var(--nx-badge-padding, 2px 9px); border: var(--nx-badge-border, 1px solid var(--content-control-border, var(--nx-color-border))); border-radius: 6px; background: var(--content-card-soft, var(--nx-color-surface)); color: var(--muted, var(--nx-color-muted)); font-size: var(--nx-badge-font-size, 11px); font-weight: 650; white-space: nowrap; }
.nxp-badge.ok { background: var(--ok-soft, var(--nx-color-success-soft)); color: var(--ok, var(--nx-color-success)); }
.nxp-badge.bad { background: var(--bad-soft, var(--nx-color-danger-soft)); color: var(--bad, var(--nx-color-danger)); }
.nxp-badge.warn { background: var(--warn-soft, var(--nx-color-warning-soft)); color: var(--warn, var(--nx-color-warning)); }
.nxp-badge.muted { background: var(--muted-soft, var(--nx-color-surface)); color: var(--muted, var(--nx-color-muted)); }
.nxp-badge.blue { background: var(--accent-soft, var(--nx-color-accent-soft)); color: var(--accent, var(--nx-color-accent)); }
</style>

<style>
.nxp-badge { min-width: 0; max-width: 100%; flex-shrink: 1; height: var(--nx-badge-min-height, 24px); line-height: 18px; white-space: nowrap !important; }
.nxp-badge > .nxp-overflow-text { flex: 1 1 auto; }
.nxp-badge.is-wrapping { height: auto; white-space: normal !important; }
</style>
