<script setup lang="ts">
import { ref, useAttrs } from 'vue';
import { useFormLabel } from './form-label';
defineOptions({ inheritAttrs: false });
const props = defineProps<{
  modelValue?: unknown;
  value?: unknown;
  min?: string | number;
  max?: string | number;
  step?: string | number;
  disabled?: boolean;
  readonly?: boolean;
  validate?: (value: number | null) => boolean;
}>();
const emit = defineEmits<{
  'update:modelValue': [value: number | null];
  change: [event: Event];
  input: [event: Event];
}>();
const attrs = useAttrs();
const root = ref<HTMLElement>();
const label = useFormLabel(root, attrs);
const invalid = ref(false);
const numeric = (value: unknown): number | null =>
  value === '' || value == null || !Number.isFinite(Number(value)) ? null : Number(value);
function change(event: CustomEvent<{ value: number | null }>): void {
  event.stopPropagation();
  invalid.value = props.validate ? !props.validate(event.detail.value) : false;
  if (invalid.value) return;
  emit('update:modelValue', event.detail.value);
  Object.assign(root.value!, { value: event.detail.value === null ? '' : String(event.detail.value) });
  for (const name of ['input', 'change']) root.value!.dispatchEvent(new Event(name, { bubbles: true }));
}
</script>
<template>
  <span
    v-bind="attrs"
    ref="root"
    class="form-number"
    :inert="disabled"
    @change="emit('change', $event)"
    @input="emit('input', $event)"
  >
    <nexus-number-input
      aria-required="true"
      :aria-label="label"
      :aria-invalid="invalid || attrs['aria-invalid'] === 'true' ? 'true' : 'false'"
      :value.prop="numeric(modelValue === undefined ? value : modelValue)"
      :min.prop="min === undefined ? null : Number(min)"
      :max.prop="max === undefined ? null : Number(max)"
      :step.prop="step === undefined ? 1 : Number(step)"
      :read-only.prop.camel="!!disabled || !!readonly"
      @nexus-change="change"
    />
  </span>
</template>
