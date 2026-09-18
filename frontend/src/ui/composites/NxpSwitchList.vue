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
  gap: 0;
  margin: var(--space-3, var(--nx-space-3)) 0 var(--space-5, var(--nx-space-5));
  border: 1px solid var(--content-card-border, var(--nx-color-border));
  border-radius: var(--radius-lg, var(--nx-radius-lg));
  overflow: hidden;
  background: var(--content-card, var(--nx-color-surface));
}
.nxp-switch-list > .nxp-switch-setting-root,
.nxp-switch-list > nxp-switch-setting > .nxp-switch-setting-root {
  min-height: 64px;
  border: 0;
  border-bottom: 1px solid var(--border, var(--nx-color-border));
  border-radius: 0;
  background: transparent;
}
.nxp-switch-list > .nxp-switch-setting-root:hover,
.nxp-switch-list > nxp-switch-setting:hover > .nxp-switch-setting-root {
  background: var(--content-card-hover, var(--nx-color-surface));
}
.nxp-switch-list > .nxp-switch-setting-root:last-child,
.nxp-switch-list > nxp-switch-setting:last-child > .nxp-switch-setting-root {
  border-bottom: 0;
}
</style>
