<script setup lang="ts">
import { computed } from 'vue';
import type { AppStore } from '../app/store';
import { initialValue, schemaAt } from '../control/schema';
import type { AutomationBundle } from '../control/wire.generated';
import BindingPolicyFields from './BindingPolicyFields.vue';

type User = AutomationBundle['users'][number];
type Binding = AutomationBundle['bindings'][number];
const props = defineProps<{
  store: AppStore;
  kind: 'users' | 'bindings' | 'schedules';
  modelValue: Record<string, unknown>;
  bundle: AutomationBundle;
}>();
const emit = defineEmits<{ 'update:modelValue': [value: Record<string, unknown>] }>();
const defaults = initialValue(
  schemaAt('automation-binding-policy.schema.json#/$defs/overrides')
) as NonNullable<User['bindingOverrides']>;
const value = computed(() =>
  props.kind === 'users'
    ? (props.modelValue.bindingOverrides as User['bindingOverrides']) || defaults
    : (props.modelValue as unknown as Binding)
);
function update(next: Binding | NonNullable<User['bindingOverrides']>): void {
  emit(
    'update:modelValue',
    props.kind === 'users'
      ? { ...props.modelValue, bindingOverrides: next }
      : (next as unknown as Record<string, unknown>)
  );
}
</script>

<template>
  <BindingPolicyFields
    v-if="kind !== 'schedules'"
    :id="String(modelValue.id)"
    :store="store"
    :global="kind === 'users'"
    :model-value="value!"
    @update:model-value="update"
  />
</template>
