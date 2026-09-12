<script setup lang="ts">
withDefaults(defineProps<{
  tone?: "default" | "primary" | "success" | "warning" | "danger";
  variant?: "solid" | "ghost";
  size?: "sm" | "md";
  type?: "button" | "submit" | "reset";
  disabled?: boolean;
  /** 按钮文案；自定义元素消费方优先使用该属性，插槽内容作为替代写法。 */
  label?: string;
}>(), { tone: "default", variant: "solid", size: "md", type: "button", disabled: false, label: "" });
</script>

<template>
  <button class="nxp-button" :class="{ sm: size === 'sm', danger: tone === 'danger', ghost: variant === 'ghost' }" :type="type" :disabled="disabled"><slot>{{ label }}</slot></button>
</template>

<style>
:host {
  display: inline-flex;
  min-width: 0;
  vertical-align: middle;
}
</style>
