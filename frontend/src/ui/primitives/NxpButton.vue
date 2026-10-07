<script setup lang="ts">
import { Comment, Text, useSlots } from "vue";
import NxpOverflowText from "./NxpOverflowText.vue";
const slots = useSlots();
function textOnlySlot() { return !slots.default || slots.default().every(node => node.type === Text || node.type === Comment); }
withDefaults(defineProps<{
  tone?: "default" | "primary" | "success" | "warning" | "danger";
  variant?: "solid" | "ghost";
  size?: "sm" | "md";
  type?: "button" | "submit" | "reset";
  disabled?: boolean;
  /** 异步操作进行中；显示按钮内加载动画并锁定重复提交。 */
  busy?: boolean;
  /** 按钮文案；自定义元素消费方优先使用该属性，插槽内容作为替代写法。 */
  label?: string;
  wrap?: boolean;
}>(), { tone: "default", variant: "solid", size: "md", type: "button", disabled: false, busy: false, label: "", wrap: false });
</script>

<template>
  <button
    class="nxp-button"
    :class="{ primary: tone === 'primary', sm: size === 'sm', danger: tone === 'danger', ghost: variant === 'ghost', busy }"
    :type="type"
    :disabled="disabled || busy"
    :aria-busy="busy ? 'true' : undefined"
  ><NxpOverflowText v-if="textOnlySlot()" :label="label" :wrap="wrap"><slot>{{ label }}</slot></NxpOverflowText><slot v-else>{{ label }}</slot></button>
</template>

<style>
nxp-button > .nxp-button { flex: 1 1 auto; min-width: 0; }
:host {
  display: inline-flex;
  min-width: 0;
  vertical-align: middle;
}
.nxp-button {
  width: var(--nx-button-width, auto);
  min-width: var(--nx-button-min-width, auto);
  min-height: var(--nx-button-min-height, var(--control-height, var(--nx-control-height, 40px)));
  padding: var(--nx-button-padding, 0 15px);
  border-color: var(--nx-button-border-color, var(--content-control-border, var(--nx-color-border)));
  border-radius: var(--nx-button-radius, var(--radius-md, var(--nx-radius-md)));
  background: var(--nx-button-background, transparent);
  color: var(--nx-button-color, var(--text, var(--nx-color-text)));
}
.nxp-button:not(.primary):not(.ghost):not(.danger):hover {
  background: var(--nx-button-hover-background, var(--content-control-hover, var(--nx-color-surface)));
}
.nxp-button[aria-pressed="true"] {
  border-color: var(--nx-button-pressed-border-color, var(--accent, var(--nx-color-accent)));
  background: var(--nx-button-pressed-background, var(--accent-soft, var(--nx-color-accent-soft)));
  color: var(--nx-button-pressed-color, var(--accent, var(--nx-color-accent)));
}
</style>

<style>
.nxp-button > .nxp-overflow-text { min-width: 0; max-width: 100%; flex: 1 1 auto; }
</style>
