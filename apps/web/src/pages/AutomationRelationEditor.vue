<script setup lang="ts">
import { computed } from 'vue';
import type { AutomationStore } from '../app/store-ports';
import { initialValue, schemaAt } from '../control/schema';
import type { AutomationDraft } from '../app/automation-draft';
import type { AutomationBundle } from '../control/wire.generated';
import BindingPolicyFields from './BindingPolicyFields.vue';

type User = AutomationBundle['users'][number];
type Binding = AutomationBundle['bindings'][number];
const props = defineProps<{
  store: AutomationStore;
  kind: 'users' | 'bindings' | 'schedules';
  modelValue: AutomationDraft;
  bundle: AutomationBundle;
}>();
const emit = defineEmits<{ 'update:modelValue': [value: AutomationDraft] }>();
const defaults = initialValue(
  schemaAt('automation-binding-policy.schema.json#/$defs/overrides')
) as NonNullable<User['bindingOverrides']>;
const value = computed(() =>
  props.kind === 'users'
    ? (props.modelValue.bindingOverrides as User['bindingOverrides']) || defaults
    : (props.modelValue as Binding)
);
function update(next: Binding | NonNullable<User['bindingOverrides']>): void {
  emit(
    'update:modelValue',
    props.kind === 'users'
      ? { ...props.modelValue, bindingOverrides: next as NonNullable<User['bindingOverrides']> }
      : (next as AutomationDraft)
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
