<script setup lang="ts">
import { t } from '../app/locale';

const name = defineModel<string>({ required: true });
</script>

<template>
  <label class="account-name-field">
    {{ t('用户名') }}
    <em>*</em>
    <nexus-text-input
      data-draft-field="AccountFields/modelValue.name"
      :aria-label="t('用户名')"
      aria-required="true"
      :value.prop="name"
      :max-length.prop.camel="120"
      @nexus-change="name = $event.detail.value"
    />
  </label>
</template>

<style scoped>
.account-name-field {
  display: flex;
  flex-wrap: wrap;
  gap: 7px 2px;
  color: var(--muted);
  font-size: 12px;
  font-weight: 600;
}
nexus-text-input { flex-basis: 100%; }
em {
  color: var(--bad);
  font-style: normal;
}
</style>
