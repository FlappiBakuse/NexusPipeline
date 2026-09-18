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
  surface?: "default" | "secondary";
}>(), {
  title: "",
  description: "",
  expanded: false,
  panelId: "",
  controlsId: "",
  panel: "",
  surface: "default",
});

const emit = defineEmits<{ toggle: [expanded: boolean] }>();

const attrs = useAttrs();
const bodyId = computed(() => props.controlsId || props.panelId || undefined);

function toggle() {
  emit("toggle", !props.expanded);
}
</script>

<template>
  <section v-bind="attrs" class="nxp-collapsible-card" :class="{ 'is-expanded': props.expanded, 'is-secondary': props.surface === 'secondary' }">
    <button
      class="nxp-collapsible-card-toggle"
      type="button"
      data-action="toggle-settings-panel"
      :data-panel="props.panel || props.panelId || undefined"
      :aria-expanded="props.expanded"
      :aria-controls="bodyId"
      @click.stop="toggle"
    >
      <span class="nxp-collapsible-card-copy">
        <slot name="header">
          <strong class="nxp-collapsible-card-title">{{ props.title }}</strong>
          <span v-if="props.description" class="muted">{{ props.description }}</span>
        </slot>
      </span>
      <span class="nxp-collapsible-card-side">
        <slot name="actions" />
        <span class="nxp-collapsible-card-arrow" aria-hidden="true">
          <NxpIcon :name="props.expanded ? 'chevronDown' : 'chevronRight'" class-name="nxp-collapsible-card-arrow-icon" />
        </span>
      </span>
    </button>
    <NxpCollapseTransition>
      <div :id="bodyId" v-show="props.expanded" class="nxp-collapsible-card-body">
        <slot />
      </div>
    </NxpCollapseTransition>
  </section>
</template>

<style>
.nxp-collapsible-card {
  display: block;
  min-width: 0;
  margin: 0;
  overflow: hidden;
  border: 1px solid var(--content-card-border, var(--nx-color-border));
  border-radius: var(--radius-lg, var(--nx-radius-lg));
  background: var(--content-card, var(--nx-color-surface));
  color: var(--text, var(--nx-color-text));
}
.nxp-collapsible-card.is-expanded { border-color: var(--content-control-border, var(--nx-color-border)); }
.nxp-collapsible-card-toggle {
  display: flex;
  width: 100%;
  min-height: 72px;
  align-items: center;
  justify-content: space-between;
  gap: var(--space-4, var(--nx-space-4));
  padding: var(--space-4, var(--nx-space-4)) var(--space-5, var(--nx-space-5));
  border: 0;
  border-radius: 0;
  background: transparent;
  color: inherit;
  text-align: left;
}
.nxp-collapsible-card-toggle:hover { background: var(--content-card-hover, var(--nx-color-surface)); }
.nxp-collapsible-card-toggle:focus-visible { outline: none; box-shadow: var(--focus, 0 0 0 3px var(--accent-soft)); }
.nxp-collapsible-card-copy { display: grid; min-width: 0; gap: 4px; }
.nxp-collapsible-card-title { font-size: 15px; line-height: 1.45; }
.nxp-collapsible-card-copy .muted { line-height: 1.5; }
.nxp-collapsible-card-side { display: inline-flex; flex: 0 0 auto; align-items: center; gap: var(--space-3); }
.nxp-collapsible-card-side > .nxp-button, .nxp-collapsible-card-side > button { min-height: 32px; }
.nxp-collapsible-card-arrow { display: inline-flex; flex: 0 0 auto; align-items: center; justify-content: center; color: var(--accent, var(--nx-color-accent)); }
.nxp-collapsible-card-arrow-icon { width: 22px; height: 22px; }
.nxp-collapsible-card-body { min-width: 0; padding: var(--space-5, var(--nx-space-5)); border-top: 1px solid var(--border, var(--nx-color-border)); }
.nxp-collapsible-card-body[hidden] { display: none; }
.nxp-collapsible-card-body .settings-list:first-child { margin-top: 0; }
.nxp-collapsible-card.is-secondary { border-color: var(--content-card-border); background: var(--content-card-soft); }
</style>
