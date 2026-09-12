<script setup lang="ts">
/** 开关分组容器：与设置页的开关列表使用同一呈现（分组边框 + 分隔行 + 悬停高亮），
 *  插件与声明式表单用它把多个 `nxp-switch-setting` 组成与设置页一致的列表。 */
</script>

<template>
  <div class="settings-list nxp-switch-list"><slot /></div>
</template>

<style>
:host {
  display: block;
  min-width: 0;
}
.nxp-switch-list > nxp-switch-setting {
  display: contents;
}
.nxp-switch-list > .switch-card,
.nxp-switch-list > nxp-switch-setting > .switch-card {
  min-height: 64px;
  border: 0;
  border-bottom: 1px solid var(--border);
  border-radius: 0;
  background: transparent;
}
.nxp-switch-list > .switch-card:hover,
.nxp-switch-list > nxp-switch-setting:hover > .switch-card {
  background: var(--content-card-hover);
}
.nxp-switch-list > .switch-card:last-child,
.nxp-switch-list > nxp-switch-setting:last-child > .switch-card {
  border-bottom: 0;
}
</style>
