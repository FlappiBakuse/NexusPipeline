<script setup lang="ts">
import { onBeforeUnmount, onMounted, onUpdated, ref, watch, useAttrs } from 'vue';
import { DateControl } from '../ui/date-control';
import { TimeControl } from '../ui/time-control';
import { useFormLabel } from './form-label';
defineOptions({ inheritAttrs: false });
const props = defineProps<{
  modelValue?: string;
  value?: string;
  type?: 'date' | 'time';
  min?: string;
  max?: string;
  disabled?: boolean;
}>();
const emit = defineEmits<{
  'update:modelValue': [value: string];
  change: [event: Event];
  input: [event: Event];
}>();
const attrs = useAttrs();
const root = ref<HTMLElement>();
const label = useFormLabel(root, attrs);
let control: DateControl | TimeControl | undefined;
let kind: 'date' | 'time' | undefined;
function update(): void {
  if (!control) return;
  const options = {
    kind: props.type || 'date',
    value: props.modelValue ?? props.value ?? '',
    label: label.value,
    min: props.min,
    max: props.max,
    disabled: Boolean(props.disabled)
  };
  control.set(options);
}
function mount(): void {
  const nextKind = props.type || 'date';
  if (kind === nextKind) return;
  control?.dispose();
  kind = nextKind;
  const owner = nextKind === 'time' ? TimeControl : DateControl;
  control = new owner(
    root.value!.shadowRoot || root.value!.attachShadow({ mode: 'open', delegatesFocus: true }),
    (value) => {
      emit('update:modelValue', value);
      Object.assign(root.value!, { value });
      for (const name of ['input', 'change']) root.value!.dispatchEvent(new Event(name, { bubbles: true }));
    }
  );
  update();
}
onMounted(mount);
watch(label, update);
watch(() => [props.modelValue, props.value, props.type, props.min, props.max, props.disabled], update);
onUpdated(() => {
  mount();
  update();
});
onBeforeUnmount(() => control?.dispose());
</script>
<template>
  <span
    v-bind="attrs"
    ref="root"
    class="form-date"
    @input="emit('input', $event)"
    @change="emit('change', $event)"
  />
</template>
