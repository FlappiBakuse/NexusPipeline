<script setup lang="ts">
import { computed, useAttrs } from "vue";
import NxpIcon from "../primitives/NxpIcon.vue";
import NxpCollapseTransition from "./NxpCollapseTransition.vue";

/** 折叠卡片：标题 + 展开箭头 + body 的唯一实现，折叠动画由组件内含，调用方不写箭头与 transition。
 *  `expanded` 为受控状态，点击箭头后 emit `toggle`；`panelId` 同时用于 `aria-controls` 与 body id，
 *  `controlsId` 用于面板标识与 body id 不一致的宿主 section，`panel` 是折叠协调使用的逻辑面板名。 */
const props = withDefaults(defineProps<{
  title?: string;
  description?: string;
  expanded?: boolean;
  panelId?: string;
  controlsId?: string;
  panel?: string;
}>(), {
  title: "",
  description: "",
  expanded: false,
  panelId: "",
  controlsId: "",
  panel: "",
});

const emit = defineEmits<{ toggle: [expanded: boolean] }>();

const attrs = useAttrs();
const bodyId = computed(() => props.controlsId || props.panelId || undefined);

function toggle() {
  emit("toggle", !props.expanded);
}
</script>

<template>
  <section v-bind="attrs" class="settings-card section-surface nxp-collapsible-card" :class="{ 'is-expanded': props.expanded }">
    <button
      class="settings-card-toggle nxp-collapsible-card-toggle"
      type="button"
      data-action="toggle-settings-panel"
      :data-panel="props.panel || props.panelId || undefined"
      :aria-expanded="props.expanded"
      :aria-controls="bodyId"
      @click.stop="toggle"
    >
      <span class="settings-card-copy">
        <slot name="header">
          <strong class="settings-card-title">{{ props.title }}</strong>
          <span v-if="props.description" class="muted">{{ props.description }}</span>
        </slot>
      </span>
      <span class="nxp-collapsible-card-side">
        <slot name="actions" />
        <span class="settings-card-arrow" aria-hidden="true">
          <NxpIcon :name="props.expanded ? 'chevronDown' : 'chevronRight'" class-name="settings-card-arrow-icon" />
        </span>
      </span>
    </button>
    <NxpCollapseTransition>
      <div :id="bodyId" v-show="props.expanded" class="settings-card-body nxp-collapsible-card-body">
        <slot />
      </div>
    </NxpCollapseTransition>
  </section>
</template>

<style>
:host { display: block; min-width: 0; }
.nxp-collapsible-card-side { display: inline-flex; flex: 0 0 auto; align-items: center; gap: var(--space-3); }
.nxp-collapsible-card-side > .nxp-button, .nxp-collapsible-card-side > button { min-height: 32px; }
</style>
