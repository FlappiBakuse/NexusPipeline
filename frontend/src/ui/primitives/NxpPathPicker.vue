<script setup lang="ts">
const props = withDefaults(defineProps<{ modelValue?: string; placeholder?: string; disabled?: boolean; ariaLabel?: string }>(), {
  modelValue: "",
  placeholder: "",
  disabled: false,
  ariaLabel: "路径",
});
const emit = defineEmits<{ "update:modelValue": [value: string]; change: [value: string]; browse: [] }>();

function update(event: Event) {
  const value = (event.target as HTMLInputElement).value;
  emit("update:modelValue", value);
  emit("change", value);
}

function browse() {
  emit("browse");
}
</script>

<template>
  <div class="nxp-path-picker">
    <input class="nxp-input" type="text" :value="props.modelValue" :placeholder="props.placeholder" :disabled="props.disabled" :aria-label="props.ariaLabel" @input="update" @change="update" />
    <button class="nxp-path-picker-button" type="button" :disabled="props.disabled" :aria-label="`${props.ariaLabel}浏览`" @click="browse">…</button>
  </div>
</template>

<style>
.nxp-path-picker { display: flex; min-width: 0; gap: var(--nx-space-2); }
.nxp-path-picker .nxp-input { min-width: 0; flex: 1 1 auto; }
.nxp-path-picker-button { min-width: var(--nx-control-height); min-height: var(--nx-control-height); border: 1px solid var(--nx-color-border); border-radius: var(--nx-radius-sm); background: transparent; color: var(--nx-color-text); font: inherit; cursor: pointer; }
.nxp-path-picker-button:disabled { opacity: .5; cursor: not-allowed; }
</style>
