<script setup lang="ts">
import { t } from '../app/locale';
import type { SecretField } from './notification-types';

defineProps<{ field: SecretField; label: string; value: string; configured: boolean; disabled: boolean }>();
const emit = defineEmits<{ edit: [field: SecretField, value: string] }>();
</script>

<template>
  <label class="secret-input" :data-secret-field="field">
    {{ t(label) }}
    <input
        data-draft-field="NotificationSecretInput/value"
      :value="value"
      type="password"
      :aria-label="t(label)"
      :placeholder="configured ? t('留空不修改') : ''"
      autocomplete="new-password"
      :disabled="disabled"
      @input="emit('edit', field, ($event.target as HTMLInputElement).value)"
    />
  </label>
</template>

<style scoped>
label {
  display: grid;
  gap: 8px;
  min-width: 0;
  font-size: 12px;
  font-weight: 600;
}
input {
  width: 100%;
  min-height: 40px;
  box-sizing: border-box;
  font: inherit;
  font-weight: 400;
}
</style>
