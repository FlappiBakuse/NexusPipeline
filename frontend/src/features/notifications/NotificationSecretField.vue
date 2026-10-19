<script setup lang="ts">
import { t } from "../../platform/i18n";
import NxpField from "../../ui/primitives/NxpField.vue";
import NxpTextInput from "../../ui/primitives/NxpTextInput.vue";
import type { SecretAction } from "./notificationTypes";

defineProps<{ modelValue: SecretAction; label: string; configured?: boolean; help?: string; type?: "text" | "password" }>();
const emit = defineEmits<{ "update:modelValue": [value: SecretAction] }>();
function update(value: string) {
  emit("update:modelValue", { action: value ? "replace" : "keep", value });
}
</script>

<template>
  <NxpField :label="label" :help="help">
    <NxpTextInput :model-value="modelValue.value || ''" :type="type || 'password'" :autocomplete="type === 'text' ? 'off' : 'new-password'" :placeholder="configured ? t('common.leave_blank_to_keep') : ''" :aria-label="label" @update:model-value="update" />
  </NxpField>
</template>
