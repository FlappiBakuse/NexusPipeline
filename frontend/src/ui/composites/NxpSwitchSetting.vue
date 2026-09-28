<script setup lang="ts">
import NxpSwitch from "../primitives/NxpSwitch.vue";
const props = withDefaults(
  defineProps<{
    id?: string;
    modelValue?: boolean;
    label: string;
    description?: string;
    help?: string;
    disabled?: boolean;
    ariaLabel?: string;
  }>(),
  { id: "", modelValue: false, description: "", help: "", disabled: false, ariaLabel: "" },
);
const emit = defineEmits<{
  "update:modelValue": [value: boolean];
  change: [value: boolean];
}>();

function update(value: boolean) {
  emit("update:modelValue", value);
  emit("change", value);
}
</script>

<template>
  <div class="nxp-switch-setting-root" :data-help="props.help || undefined">
    <div class="nxp-switch-setting-copy">
      <strong>{{ props.label }}</strong>
      <span v-if="props.description" class="muted">{{ props.description }}</span>
    </div>
    <NxpSwitch
      class="nxp-switch-setting-control"
      :id="props.id || undefined"
      :model-value="props.modelValue"
      :disabled="props.disabled"
      :aria-label="props.ariaLabel || props.label"
      @update:model-value="update"
    />
  </div>
</template>

<style>
:host { display: block; min-width: 0; }
.nxp-switch-setting-root {
  display: flex;
  min-width: 0;
  min-height: var(--nx-switch-setting-min-height, 64px);
  height: var(--nx-switch-setting-height, auto);
  align-items: flex-start;
  justify-content: space-between;
  gap: var(--space-4, var(--nx-space-4));
  padding: var(--nx-switch-setting-padding, 12px 16px);
  border: var(--nx-switch-setting-border, 1px solid var(--content-card-border, var(--nx-color-border)));
  border-bottom: var(--nx-switch-setting-border-bottom, var(--nx-switch-setting-border, 1px solid var(--content-card-border, var(--nx-color-border))));
  border-radius: var(--nx-switch-setting-radius, var(--radius-md, var(--nx-radius-md)));
  background: var(--nx-switch-setting-background, var(--content-card-soft, var(--nx-color-surface)));
  color: var(--nx-color-text, inherit);
}
.nxp-switch-setting-root:hover { background: var(--nx-switch-setting-hover-background, var(--nx-switch-setting-background, var(--content-card-soft, var(--nx-color-surface)))); }
.nxp-switch-setting-copy { flex: 1 1 auto; min-width: 0; padding-right: var(--nx-switch-setting-copy-padding, 0); }
.nxp-switch-setting-copy > strong { display: block; font-size: 13px; line-height: 1.45; }
.nxp-switch-setting-copy > .muted { display: block; margin-top: 4px; color: var(--muted, var(--nx-color-muted)); font-size: 12px; line-height: 1.55; }
.nxp-switch-setting-control { align-self: center; }
.nxp-switch-setting-root { --nx-switch-control-width: var(--nx-switch-setting-control-width, 56px); --nx-switch-control-height: var(--nx-switch-setting-control-height, 40px); --nx-switch-track-width: var(--nx-switch-setting-track-width, 52px); --nx-switch-track-height: var(--nx-switch-setting-track-height, 30px); }
@media (max-width: 480px) {
  .nxp-switch-setting-root { align-items: flex-start; }
  .nxp-switch-setting-copy { padding-right: 8px; }
}
</style>
