<script setup lang="ts">
import { computed } from 'vue';
import type { Schema } from '../control/schema';
import { initialValue } from '../control/schema';
import { fieldLabel } from '../app/labels';
import { valueLabel } from '../app/value-labels';
import { t, registerMessages } from '../app/locale';
import JsonValueInput from './JsonValueInput.vue';
registerMessages({ 添加: 'Add', 移除: 'Remove' });
const props = defineProps<{
  schema: Schema;
  modelValue: unknown;
  label?: string;
  fieldKey?: string;
  disabled?: boolean;
  choices?: Record<string, readonly { readonly id: string; readonly name: string }[] | undefined>;
}>();
const emit = defineEmits<{ 'update:modelValue': [value: unknown] }>();
const current = computed(() => (props.modelValue as Record<string, unknown>) || {});
const fields = computed(() =>
  Object.entries(props.schema.properties || {}).filter(
    ([name]) => !['id', 'format', 'version', 'order'].includes(name)
  )
);
const items = computed(() => (Array.isArray(props.modelValue) ? props.modelValue : []));
const options = computed(() =>
  (props.choices?.[props.fieldKey || ''] || []).map((item) => ({ id: item.id, label: item.name }))
);
const changeValue = (event: Event): unknown => (event as CustomEvent<{ value: unknown }>).detail.value;
const shape = computed(
  () =>
    props.schema.oneOf?.find((item) => {
      const kind = item.properties?.kind?.const;
      return kind !== undefined && kind === current.value.kind;
    }) || props.schema.oneOf?.[0]
);
function update(name: string, value: unknown): void {
  emit('update:modelValue', { ...current.value, [name]: value });
}
function updateItem(index: number, value: unknown): void {
  const next = [...items.value];
  next[index] = value;
  emit('update:modelValue', next);
}
function remove(index: number): void {
  emit(
    'update:modelValue',
    items.value.filter((_, position) => position !== index)
  );
}
function optional(name: string, schema: Schema): void {
  const next = { ...current.value };
  if (name in next) delete next[name];
  else next[name] = initialValue(schema);
  emit('update:modelValue', next);
}
const text = (event: Event): string => (event.target as HTMLInputElement).value;
</script>
<template>
  <fieldset v-if="schema.type === 'object'" class="form-group" :disabled="disabled" :inert="disabled">
    <legend v-if="label">{{ t(label) }}</legend>
    <template v-for="[name, field] in fields" :key="name">
      <div v-if="!schema.required?.includes(name)" class="optional-field">
        <label>
          <input type="checkbox" :checked="name in current" @change="optional(name, field)" />
          {{ fieldLabel(name) }}
        </label>
      </div>
      <SchemaFields
        v-if="schema.required?.includes(name) || name in current"
        :schema="field"
        :model-value="current[name]"
        :field-key="name"
        :label="fieldLabel(name)"
        :disabled="disabled"
        :choices="choices"
        @update:model-value="update(name, $event)"
      />
    </template>
  </fieldset>
  <div v-else-if="schema.oneOf && shape" class="form-group">
    <label>
      {{ label ? t(label) : '' }}
      <FormSelect
        :disabled="disabled"
        :value="schema.oneOf.indexOf(shape)"
        @change="emit('update:modelValue', initialValue(schema.oneOf![Number(text($event))]))"
      >
        <option v-for="(item, index) in schema.oneOf" :value="index">
          {{ valueLabel(item.properties?.kind?.const || index + 1) }}
        </option>
      </FormSelect>
    </label>
    <SchemaFields
      :schema="shape"
      :model-value="modelValue"
      :disabled="disabled"
      :choices="choices"
      @update:model-value="emit('update:modelValue', $event)"
    />
  </div>
  <div v-else-if="schema.type === 'array'" class="form-group array-field">
    <div class="row">
      <strong>{{ label ? t(label) : '' }}</strong>
      <button
        type="button"
        :disabled="disabled || items.length >= (schema.maxItems || 4096)"
        @click="emit('update:modelValue', [...items, initialValue(schema.items || {})])"
      >
        {{ t('添加') }}
      </button>
    </div>
    <div v-for="(item, index) in items" :key="index" class="array-item">
      <SchemaFields
        :schema="schema.items || {}"
        :model-value="item"
        :field-key="fieldKey"
        :disabled="disabled"
        :choices="choices"
        @update:model-value="updateItem(index, $event)"
      />
      <div class="row">
        <button
          type="button"
          :disabled="disabled || index === 0"
          @click="emit('update:modelValue', items.toSpliced(index - 1, 2, items[index], items[index - 1]))"
        >
          ↑
        </button>
        <button type="button" :disabled="disabled" @click="remove(index)">{{ t('移除') }}</button>
      </div>
    </div>
  </div>
  <JsonValueInput
    v-else-if="schema.const === undefined && !schema.type && !schema.enum"
    :model-value="modelValue"
    :label="label"
    :disabled="disabled"
    @update:model-value="emit('update:modelValue', $event)"
  />
  <div
    v-else-if="schema.const === undefined"
    class="form-field"
    :class="{ 'switch-field': schema.type === 'boolean' }"
  >
    <span>{{ label ? t(label) : '' }}</span>
    <nexus-select
      v-if="schema.enum || options.length"
      :aria-label="label"
      :inert="disabled"
      :value.prop="String(modelValue ?? '')"
      :options.prop="
        schema.enum?.map((item) => ({ id: String(item), label: valueLabel(item, fieldKey) })) || options
      "
      @nexus-change="
        emit(
          'update:modelValue',
          schema.enum?.find((item) => String(item) === changeValue($event)) ?? changeValue($event)
        )
      "
    />
    <nexus-switch
      v-else-if="schema.type === 'boolean'"
      :aria-label="label"
      :disabled.prop="disabled"
      :value.prop="Boolean(modelValue)"
      @nexus-change="emit('update:modelValue', changeValue($event))"
    />
    <nexus-number-input
      v-else-if="schema.type === 'integer' || schema.type === 'number'"
      :aria-label="label"
      aria-required="true"
      :read-only.prop.camel="disabled"
      :value.prop="modelValue ?? null"
      :min.prop="schema.minimum ?? -Number.MAX_SAFE_INTEGER"
      :max.prop="schema.maximum ?? Number.MAX_SAFE_INTEGER"
      @nexus-change="emit('update:modelValue', changeValue($event))"
    />
    <nexus-text-area
      v-else-if="(schema.maxLength || 0) > 4096"
      :aria-label="label"
      :read-only.prop.camel="disabled"
      :value.prop="modelValue || ''"
      :max-length.prop.camel="schema.maxLength"
      @nexus-change="emit('update:modelValue', changeValue($event))"
    />
    <nexus-text-input
      v-else
      :aria-label="label"
      :disabled.prop="disabled"
      :value.prop="modelValue || ''"
      :max-length.prop.camel="schema.maxLength ?? 32767"
      @nexus-change="emit('update:modelValue', changeValue($event))"
    />
  </div>
</template>
