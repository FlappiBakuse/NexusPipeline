<script setup lang="ts">
import type { ExtensionDetails } from '../control/extensions';
import { locale, t } from '../app/locale';
import type { AdapterResult } from './adapter-results';

const props = defineProps<{ items?: AdapterResult['diagnostics']; details?: ExtensionDetails }>();
const levels: Record<string, string> = { information: '信息', warning: '警告', blocking: '阻断' };
function diagnostic(code: string): string {
  const details = props.details;
  return details?.diagnosticLocales[locale.value]?.[code] ||
    details?.diagnosticLocales[details.metadata?.defaultLocale || 'zh-CN']?.[code] || t(code);
}
</script>

<template>
  <ul v-if="items?.length">
    <li v-for="(item, index) in items" :key="index">{{ t(levels[item.severity] || item.severity) }} · {{ diagnostic(item.code) }}</li>
  </ul>
</template>

<style scoped>
ul { margin-block: 8px 0; padding-inline-start: 20px; }
li { overflow-wrap: anywhere; }
</style>
