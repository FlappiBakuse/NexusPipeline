<script setup lang="ts">
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
}>(), { tone: "default", variant: "solid", size: "md", type: "button", disabled: false, busy: false, label: "" });
</script>

<template>
  <button
    class="nxp-button"
    :class="{ primary: tone === 'primary', sm: size === 'sm', danger: tone === 'danger', ghost: variant === 'ghost', busy }"
    :type="type"
    :disabled="disabled || busy"
    :aria-busy="busy ? 'true' : undefined"
  ><slot>{{ label }}</slot></button>
</template>

<style>
nxp-button > .nxp-button { flex: 1 1 auto; min-width: 0; }
:host {
  display: inline-flex;
  min-width: 0;
  vertical-align: middle;
}
.nxp-button[aria-pressed="true"] {
  border-color: var(--accent, var(--nx-color-accent));
  background: var(--accent-soft, var(--nx-color-accent-soft));
  color: var(--accent, var(--nx-color-accent));
}
</style>
