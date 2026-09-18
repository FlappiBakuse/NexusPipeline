<script setup lang="ts">
/** 开关分组容器：与设置页的开关列表使用同一呈现（分组边框 + 分隔行 + 悬停高亮），
 *  插件与声明式表单用它把多个 `nxp-switch-setting` 组成与设置页一致的列表。 */
</script>

<template>
  <div class="nxp-switch-list"><slot /></div>
</template>

<style>
:host {
  display: block;
  min-width: 0;
}
.nxp-switch-list > nxp-switch-setting {
  display: contents;
}
.nxp-switch-list {
  display: grid;
  gap: 1px;
  margin: var(--space-3, var(--nx-space-3)) 0 var(--space-5, var(--nx-space-5));
  border: 1px solid var(--content-card-border, var(--nx-color-border));
  border-radius: var(--radius-lg, var(--nx-radius-lg));
  overflow: hidden;
  background: var(--content-card-border, var(--nx-color-border));
  --nx-switch-setting-min-height: 64px;
  --nx-switch-setting-border: 0;
  --nx-switch-setting-border-bottom: 0;
  --nx-switch-setting-radius: 0;
  --nx-switch-setting-background: var(--content-card, var(--nx-color-surface));
  --nx-switch-setting-hover-background: var(--content-card-hover, var(--nx-color-surface));
}
</style>
