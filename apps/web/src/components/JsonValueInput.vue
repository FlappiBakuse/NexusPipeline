<script setup lang="ts">
import { ref, watch } from 'vue';
import { t, registerMessages } from '../app/locale';
const props = defineProps<{ modelValue: unknown; rawValue?: string; label?: string; disabled?: boolean }>();
const emit = defineEmits<{ 'update:modelValue': [value: unknown]; 'update:rawValue': [value: string] }>();
const text = ref('');
const error = ref('');
registerMessages({
  '请输入合法的 JSON 值。': 'Enter a valid JSON value.',
  'JSON 值超过 2 MiB。': 'The JSON value exceeds 2 MiB.'
});
watch(
  () => props.modelValue,
  (value) => {
    if (props.rawValue !== undefined) return;
    if (error.value) return;
    text.value = JSON.stringify(value === undefined ? null : value, null, 2);
  },
  { immediate: true }
);
watch(
  () => props.rawValue,
  (raw) => {
    if (raw === undefined) return;
    text.value = raw;
    validate(raw);
  },
  { immediate: true }
);
function validate(raw: string): boolean {
  if (new TextEncoder().encode(raw).byteLength > 2 * 1024 * 1024) {
    error.value = 'JSON 值超过 2 MiB。';
    return false;
  }
  try {
    JSON.parse(raw);
    error.value = '';
    return true;
  } catch {
    error.value = '请输入合法的 JSON 值。';
    return false;
  }
}
function edit(value: string): void {
  text.value = value;
  emit('update:rawValue', value);
  if (validate(value)) emit('update:modelValue', JSON.parse(value));
}
</script>
<template>
  <label class="form-field">
    {{ label }}
    <nexus-text-area
      :value.prop="text"
      :disabled.prop="!!disabled"
      :aria-invalid="!!error"
      :aria-label="label"
      :rows.prop="5"
      @nexus-change="edit($event.detail.value)"
    />
    <span v-if="error" role="alert">{{ t(error) }}</span>
  </label>
</template>
