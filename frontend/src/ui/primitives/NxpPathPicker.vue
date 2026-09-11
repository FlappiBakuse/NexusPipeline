<script setup lang="ts">
import NxpIcon from "./NxpIcon.vue";

const props = withDefaults(
  defineProps<{
    id?: string;
    modelValue?: string;
    placeholder?: string;
    disabled?: boolean;
    ariaLabel?: string;
    help?: string;
    kind?: "file" | "folder" | "file-or-folder";
    filter?: string;
  }>(),
  {
    id: "",
    modelValue: "",
    placeholder: "",
    disabled: false,
    ariaLabel: "路径",
    help: "",
    kind: "file",
    filter: "",
  },
);
const emit = defineEmits<{
  "update:modelValue": [value: string];
  change: [value: string];
  browse: [kind: "file" | "folder"];
}>();

function update(event: Event) {
  const value = (event.target as HTMLInputElement).value;
  emit("update:modelValue", value);
  emit("change", value);
}

function browse(
  kind: "file" | "folder" = props.kind === "folder" ? "folder" : "file",
) {
  emit("browse", kind);
}
</script>

<template>
  <div class="nxp-path" :data-help="props.help || undefined">
    <input
      :id="props.id || undefined"
      class="nxp-path-input"
      type="text"
      :value="props.modelValue"
      :placeholder="props.placeholder"
      :disabled="props.disabled"
      :aria-label="props.ariaLabel"
      @input.stop="update"
      @change.stop="update"
    />
    <span
      v-if="props.kind === 'file-or-folder'"
      class="nxp-path-actions"
      role="group"
      :aria-label="`${props.ariaLabel}选择`"
    >
      <button
        class="nxp-path-trigger nxp-path-choice"
        type="button"
        data-path-trigger
        data-testid="path-picker-file"
        :disabled="props.disabled"
        :aria-label="`${props.ariaLabel}选择文件`"
        @click="browse('file')"
      >
        <NxpIcon name="file" />
      </button>
      <button
        class="nxp-path-trigger nxp-path-choice"
        type="button"
        data-path-trigger
        data-testid="path-picker-folder"
        :disabled="props.disabled"
        :aria-label="`${props.ariaLabel}选择文件夹`"
        @click="browse('folder')"
      >
        <NxpIcon name="folder" />
      </button>
    </span>
    <button
      v-else
      class="nxp-path-trigger"
      type="button"
      data-path-trigger
      data-testid="path-picker"
      :disabled="props.disabled"
      :aria-label="`${props.ariaLabel}浏览`"
      @click="browse()"
    >
      <NxpIcon :name="props.kind === 'folder' ? 'folder' : 'file'" />
    </button>
  </div>
</template>

<style>
.nxp-path {
  display: flex;
  min-width: 0;
  align-items: stretch;
}
.nxp-path-input {
  min-width: 0;
  flex: 1 1 auto;
  min-height: var(--nx-control-height);
  padding: 0 12px;
  border: 1px solid var(--content-control-border, var(--nx-color-border));
  border-radius: 8px 0 0 8px;
  background: var(--content-control, transparent);
  color: var(--nx-color-text);
  font: inherit;
}
.nxp-path-trigger {
  display: inline-flex;
  width: var(--field-action-width, 40px);
  min-width: var(--field-action-width, 40px);
  min-height: 40px;
  align-items: center;
  justify-content: center;
  padding: 0;
  border-radius: 0 8px 8px 0;
  color: var(--muted, var(--nx-color-muted));
  font: inherit;
}
.nxp-path-trigger:hover:not(:disabled) {
  border-color: var(--accent, var(--nx-color-accent));
  background: var(--content-control-hover, transparent);
  color: var(--nx-color-text);
}
.nxp-path-trigger:disabled {
  opacity: 0.5;
  cursor: not-allowed;
}
.nxp-path-trigger .nxp-icon {
  width: 18px;
  height: 18px;
}
.nxp-path-actions {
  display: inline-flex;
  flex: 0 0 auto;
  align-items: stretch;
}
.nxp-path-actions .nxp-path-choice {
  border-radius: 0;
}
.nxp-path-actions .nxp-path-choice:last-child {
  border-radius: 0 8px 8px 0;
}
</style>
