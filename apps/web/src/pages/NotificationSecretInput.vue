<script setup lang="ts">
import { fieldHelp as vHelp } from '../components/field-help';
import { t } from '../app/locale';
import type { SecretField } from './notification-types';

defineProps<{ field: SecretField; label: string; value: string; configured: boolean; disabled: boolean }>();
const emit = defineEmits<{ edit: [field: SecretField, value: string] }>();
</script>

<template>
  <label v-help="configured ? t('已有凭据留空不修改；输入新值后在保存时替换，秘密内容不会写入提示。') : undefined" class="secret-input" :data-secret-field="field">
    <span class="field-help-caption"><span>{{ t(label) }}</span></span>
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
